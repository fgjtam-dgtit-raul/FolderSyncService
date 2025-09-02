using System;
using System.Data;
using System.Data.SqlClient;
using System.Collections.Generic;
using System.Configuration.Install;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using RabbitMQ.Client;
using System.Data.Common;
using System.Configuration;

namespace SyncFolderWindowsService
{

    internal class Program : ServiceBase
    {
        private const string EventLogSource = "DGTIT Sync Folders";
        private const string EventLogName = "Application";
        private EventLog eventLog;

        private const string StorageSyncIdPath = @"SOFTWARE\DGTITFolders";
        private const string StorageSyncIdKey = "synchronizationid";
        private readonly string rabbitMqHost;
        private readonly int rabbitMqPort;
        private readonly string rabbitMqUser;
        private readonly string rabbitMqPassword;
        private readonly TimeSpan syncInterval = TimeSpan.FromSeconds(15);

        private Task processTask;
        private CancellationTokenSource cancellationTokenSource;

        static void Main(string[] args)
        {
            ServiceBase.Run(new Program());
        }

        public Program()
        {
            this.ServiceName = "DGTIT Sync Folders";
            rabbitMqHost = ConfigurationManager.AppSettings["RabbitMqHost"];
            rabbitMqPort = int.Parse(ConfigurationManager.AppSettings["RabbitMqPort"]);
            rabbitMqUser = ConfigurationManager.AppSettings["RabbitMqUser"];
            rabbitMqPassword = ConfigurationManager.AppSettings["RabbitMqPassword"];

            // Set up event logging
            eventLog = new EventLog();

            // Create the event source if it doesn't exist
            if (!EventLog.SourceExists(EventLogSource))
            {
                EventLog.CreateEventSource(EventLogSource, EventLogName);
            }

            eventLog.Source = EventLogSource;
            eventLog.Log = EventLogName;
        }

        protected override void OnStart(string[] args)
        {
            eventLog.WriteEntry("DGTIT Sync Service started", EventLogEntryType.Information);

            cancellationTokenSource = new CancellationTokenSource();
            var cancelationToken = cancellationTokenSource.Token;

            processTask = Task.Run( async () =>
            {
                while (!cancelationToken.IsCancellationRequested)
                {
                    eventLog.WriteEntry("Start synchronized data.", EventLogEntryType.Information);
                    try
                    {
                        // 1.- Get current synchronized id
                        var syncId = GetCurrentSyncId();

                        // 2.- With the synchronized id retrive the new changes of the database and the new synchronized id
                        var (folders, newSyncId) = GetChangedFolders(syncId);

                        // 3.- Send each folderdata to RabbitMQ Queue
                        await SendFolders(folders);

                        // 4.- Save the new synchronized id
                        SaveSyncId(newSyncId);
                    }
                    catch(Exception ex)
                    {
                        eventLog.WriteEntry($"Error al actualizar las carpetas: {ex.Message} {ex.StackTrace}", EventLogEntryType.Error);
                    }
                    eventLog.WriteEntry("End synchronized data.", EventLogEntryType.Information);

                    await Task.Delay(syncInterval, cancelationToken);
                }
            }, cancelationToken);
        }
        
        protected override void OnStop()
        {
            this.cancellationTokenSource?.Cancel();
            eventLog.WriteEntry("DGTIT Sync Service stopped", EventLogEntryType.Information);
            base.OnStop();
        }


        #region SQLServer access
        private (IEnumerable<string>, long) GetChangedFolders(long synchronizationId)
        {
            var sqlConnection = new SqlConnection(Properties.Settings.Default.SJP_CARPETAS_CON);
            sqlConnection.Open();
            var foldersSnapshot = new List<string>();
            long newSynchronizationId = synchronizationId;
            try
            {
                // * get the folders modified and the new syncId
                var (foldersModified, newSyncId) = this.GetFolders(sqlConnection, synchronizationId);
               
                // * get the snapshot of each folder
                foreach (var folderId in foldersModified)
                {
                    foldersSnapshot.Add(this.GetFolderSnapshot(sqlConnection, folderId));
                }
                newSynchronizationId = newSyncId;
            }
            catch(Exception ex)
            {
                eventLog.WriteEntry($"Error al get the folders data: {ex.Message}", EventLogEntryType.Error);
            }
            finally
            {
                sqlConnection.Close();
                sqlConnection.Dispose();
            }

            return (foldersSnapshot, newSynchronizationId);
        }

        private (IEnumerable<long>, long) GetFolders(SqlConnection sqlConnection, long synchronizationId)
        {
            if (sqlConnection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("The connection is closed");
            }

            var sqlCommand = new SqlCommand("[SYNCF].[SP_ObtenerCarpetaModificadas]", sqlConnection)
            {
                CommandType = CommandType.StoredProcedure
            };
            sqlCommand.Parameters.AddWithValue("@PREVIOUS_SYNC_ID", synchronizationId);

            // * prepara data set
            var dataset = new DataSet();

            // * fill the response into the dataset
            var adapter = new SqlDataAdapter(sqlCommand);
            adapter.Fill(dataset);
            adapter.Dispose();

            // * process the response
            if(dataset.Tables.Count != 2)
            {
                throw new Exception("Invalid response from the StoreProcedure");
            }

            // * retrive the folders updated
            var listFolders = new List<long>();
            foreach(DataRow row in dataset.Tables[0].Rows)
            {
                var __folderId = long.TryParse(row["ID_CARPETA"].ToString(), out long fi) ? fi : 0;
                if(__folderId > 0)
                {
                    listFolders.Add(__folderId);
                }
            }

            // * retrive the new synchronization Id
            long newSynchronizationId = long.Parse(dataset.Tables[1].Rows[0][0].ToString());

            return (listFolders, newSynchronizationId);
        }

        private string GetFolderSnapshot(SqlConnection sqlConnection, long folderId)
        {
            if(sqlConnection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("The connection is closed");
            }

            var sqlCommand = new SqlCommand("[SYNCF].[SP_CarpetaSnapshot]", sqlConnection)
            {
                CommandType = CommandType.StoredProcedure
            };
            sqlCommand.Parameters.AddWithValue("@ID_CARPETA", folderId);

            var sqlResult = sqlCommand.ExecuteScalar().ToString();
            return sqlResult;
        }
        #endregion


        #region Windows register access
        private long GetCurrentSyncId()
        {
            long syncid = 0;
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (RegistryKey key = baseKey.OpenSubKey(StorageSyncIdPath))
                {
                    if (key == null)
                    {
                        throw new ArgumentNullException("key", "The RegistryKey was not found.");
                    }

                    var currentValue = key.GetValue(StorageSyncIdKey)?.ToString();
                    if (string.IsNullOrEmpty(currentValue))
                    {
                        throw new ArgumentException("key", "The RegistryKey value has a invalid format.");
                    }

                    syncid = long.TryParse(currentValue, out long currentId)
                            ? currentId
                            : throw new ArgumentException($"can't parse the stored id '{currentValue}'");
                }
            }
            return syncid;
        }

        private void SaveSyncId(long syncid)
        {
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (RegistryKey key = baseKey.OpenSubKey(StorageSyncIdPath, true)) // Specify to write
                {
                    if (key == null)
                    {
                        throw new ArgumentNullException("key", "The RegistryKey was not found.");
                    }
                    key.SetValue(StorageSyncIdKey, syncid.ToString());
                }
            }
        }
        #endregion


        #region RabbitMQ
        private async Task SendFolders(IEnumerable<string> folders)
        {
            var factory = new ConnectionFactory() {
                HostName = rabbitMqHost,
                Port = rabbitMqPort,
                UserName = rabbitMqUser,
                Password = rabbitMqPassword
            };
            var connection = await factory.CreateConnectionAsync();
            using (var channel = await connection.CreateChannelAsync())
            {
                await channel.QueueDeclareAsync(
                    queue: "sync_queue",
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null
                );

                foreach(var folder in folders)
                {
                    await SendMessage(channel, folder);
                }
            }
            await connection.DisposeAsync();
        }

        private async Task SendMessage(IChannel channel, string jsonPayload)
        {
            var body = Encoding.UTF8.GetBytes(jsonPayload);

            //var properties = channel.CreateBasicProperties();
            //properties.Persistent = true;

            await channel.BasicPublishAsync(
                exchange: "",
                routingKey: "sync_queue",
                body: body
            );
            eventLog.WriteEntry("Sent data: " +jsonPayload, EventLogEntryType.Information);
        }
        #endregion
    }
}
