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
        private const string StorageSyncIdKeyPrefix = "synchronizationid_";
        private readonly string rabbitMqHost;
        private readonly int rabbitMqPort;
        private readonly string rabbitMqUser;
        private readonly string rabbitMqPassword;
        private readonly TimeSpan syncInterval = TimeSpan.FromSeconds(15);

        // Database/Location identification for multi-source sync
        private readonly string databaseIdentifier;
        private readonly string locationCode;

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

            // Get database/location identifier from config
            databaseIdentifier = ConfigurationManager.AppSettings["DatabaseIdentifier"] ?? Environment.MachineName;
            locationCode = ConfigurationManager.AppSettings["LocationCode"] ?? "DEFAULT";

            // Set up event logging
            eventLog = new EventLog();

            // Create the event source if it doesn't exist
            if (!EventLog.SourceExists(EventLogSource))
            {
                EventLog.CreateEventSource(EventLogSource, EventLogName);
            }

            eventLog.Source = EventLogSource;
            eventLog.Log = EventLogName;
            InitializeSyncIds();
        }

        // List of tables to sync (add all your tables here)
        private readonly List<TableSyncInfo> tablesToSync = new List<TableSyncInfo>
        {
            new TableSyncInfo("PGJ_CARPETA", "ID_CARPETA"),
            new TableSyncInfo("PGJ_PERSONA_2", "ID_PERSONA"),
            //new TableSyncInfo("PGJ_DOCUMENTO", "ID_DOCUMENTO"),
            //new TableSyncInfo("PGJ_EXPEDIENTE", "ID_EXPEDIENTE"),
            // Add more tables: new TableSyncInfo("TABLE_NAME", "ID_COLUMN")
        };

        protected override void OnStart(string[] args)
        {
            eventLog.WriteEntry("DGTIT Sync Service started", EventLogEntryType.Information);

            cancellationTokenSource = new CancellationTokenSource();
            var cancelationToken = cancellationTokenSource.Token;

            processTask = Task.Run(async () =>
            {
                while (!cancelationToken.IsCancellationRequested)
                {
                    eventLog.WriteEntry("Start synchronized data.", EventLogEntryType.Information);
                    try
                    {
                        foreach (var table in tablesToSync)
                        {
                            // 1. Get current sync id for this table
                            var syncId = GetCurrentSyncId(table.TableName);

                            // 2. Get changed rows and new sync id
                            var (changedRows, newSyncId) = GetChangedRowsWithData(table, syncId);

                            // 3. Serialize each changed row to JSON and send to RabbitMQ
                            var snapshots = new List<string>();
                            foreach (var row in changedRows)
                            {
                                var jsonData = Newtonsoft.Json.JsonConvert.SerializeObject(row);
                                snapshots.Add(jsonData);
                            }

                            // 4. Send to RabbitMQ
                            await SendData(snapshots);

                            // 5. Save new sync id for this table
                            SaveSyncId(table.TableName, newSyncId);
                        }
                    }
                    catch (Exception ex)
                    {
                        eventLog.WriteEntry($"Error: {ex.Message} {ex.StackTrace}", EventLogEntryType.Error);
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
        // Helper class for table info
        private class TableSyncInfo
        {
            public string TableName { get; }
            public string IdColumn { get; }
            public TableSyncInfo(string tableName, string idColumn)
            {
                TableName = tableName;
                IdColumn = idColumn;
            }
        }

        // Get changed rows and their data for a table using Change Tracking
        private (List<Dictionary<string, object>>, long) GetChangedRowsWithData(TableSyncInfo table, long syncId)
        {
            using (var sqlConnection = new SqlConnection(Properties.Settings.Default.SJP_CARPETAS_CON))
            {
                sqlConnection.Open();

                // Get current and min valid version
                long currentVersion = 0;
                long minValidVersion = 0;
                using (var cmdVersion = new SqlCommand($@"
            SELECT CHANGE_TRACKING_CURRENT_VERSION() AS CurrentVersion, 
                   CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'dbo.{table.TableName}')) AS MinValidVersion", sqlConnection))
                using (var reader = cmdVersion.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        currentVersion = reader.GetInt64(0);
                        minValidVersion = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    }
                }

                // SI CHANGE TRACKING NO ESTÁ HABILITADO, minValidVersion será NULL
                if (minValidVersion == 0)
                {
                    // Change Tracking no está habilitado, usar enfoque alternativo
                    eventLog.WriteEntry($"Change Tracking not enabled for {table.TableName}. Using alternative method.", EventLogEntryType.Warning);
                    return (new List<Dictionary<string, object>>(), syncId);
                }

                // MANEJO AUTOMÁTICO: Si el syncId es demasiado viejo, resetear a minValidVersion
                if (syncId < minValidVersion)
                {
                    eventLog.WriteEntry($"Sync anchor too old for {table.TableName}. Resetting from {syncId} to {minValidVersion}", EventLogEntryType.Warning);
                    syncId = minValidVersion;

                    // Actualizar el valor en el registro también
                    SaveSyncId(table.TableName, minValidVersion);
                }

                // Resto del código para obtener cambios...
                var changedRows = new List<Dictionary<string, object>>();

                if (syncId < currentVersion)
                {
                    var query = $@"
                SELECT CT.{table.IdColumn}, 
                       CT.SYS_CHANGE_OPERATION, 
                       CT.SYS_CHANGE_VERSION, 
                       T.*
                FROM CHANGETABLE(CHANGES dbo.{table.TableName}, @syncId) AS CT
                LEFT JOIN dbo.{table.TableName} AS T ON CT.{table.IdColumn} = T.{table.IdColumn}";

                    using (var cmd = new SqlCommand(query, sqlConnection))
                    {
                        cmd.Parameters.AddWithValue("@syncId", syncId);
                        using (var adapter = new SqlDataAdapter(cmd))
                        {
                            var dt = new DataTable();
                            adapter.Fill(dt);

                            foreach (DataRow row in dt.Rows)
                            {
                                var dict = new Dictionary<string, object>
                                {
                                    ["SourceDatabase"] = databaseIdentifier,
                                    ["LocationCode"] = locationCode,
                                    ["TableName"] = table.TableName,
                                    ["Operation"] = row["SYS_CHANGE_OPERATION"].ToString(),
                                    ["ChangeVersion"] = row["SYS_CHANGE_VERSION"],
                                    ["SyncTimestamp"] = DateTime.UtcNow,
                                    ["GlobalId"] = $"{locationCode}_{table.TableName}_{row[table.IdColumn]}"
                                };

                                foreach (DataColumn col in dt.Columns)
                                {
                                    if (!col.ColumnName.StartsWith("SYS_CHANGE_"))
                                    {
                                        dict[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
                                    }
                                }
                                changedRows.Add(dict);
                            }
                        }
                    }
                }

                return (changedRows, currentVersion);
            }
        }

        private void InitializeSyncIds()
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    // Crear la clave principal si no existe
                    using (RegistryKey key = baseKey.CreateSubKey(StorageSyncIdPath, true))
                    {
                        foreach (var table in tablesToSync)
                        {
                            string keyName = StorageSyncIdKeyPrefix + table.TableName;
                            if (key.GetValue(keyName) == null)
                            {
                                key.SetValue(keyName, "0");
                                eventLog.WriteEntry($"Initialized sync ID for {table.TableName}", EventLogEntryType.Information);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                eventLog.WriteEntry($"Error initializing sync IDs: {ex.Message}", EventLogEntryType.Error);
            }
        }
        #endregion

        #region Windows register access
        // Registry helpers for per-table sync id
        private long GetCurrentSyncId(string tableName)
        {
            try
            {
                string keyName = StorageSyncIdKeyPrefix + tableName;

                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    // Intentar abrir la clave con permisos de escritura
                    using (RegistryKey key = baseKey.OpenSubKey(StorageSyncIdPath, true))
                    {
                        if (key == null)
                        {
                            // Crear la clave completa si no existe
                            using (RegistryKey newKey = baseKey.CreateSubKey(StorageSyncIdPath))
                            {
                                newKey.SetValue(keyName, "0");
                                eventLog.WriteEntry($"Created new registry key for {tableName} with sync ID 0", EventLogEntryType.Information);
                                return 0;
                            }
                        }

                        var currentValue = key.GetValue(keyName)?.ToString();
                        if (string.IsNullOrEmpty(currentValue))
                        {
                            // Crear el valor si no existe
                            key.SetValue(keyName, "0");
                            eventLog.WriteEntry($"Created new sync ID for {tableName} with value 0", EventLogEntryType.Information);
                            return 0;
                        }

                        if (long.TryParse(currentValue, out long currentId))
                        {
                            return currentId;
                        }
                        else
                        {
                            // Si el valor no es válido, resetear a 0
                            key.SetValue(keyName, "0");
                            eventLog.WriteEntry($"Invalid sync ID for {tableName}. Reset to 0", EventLogEntryType.Warning);
                            return 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                eventLog.WriteEntry($"Error getting sync ID for {tableName}: {ex.Message}. Using default 0", EventLogEntryType.Error);
                return 0; // Fallback to 0
            }
        }

        private void SaveSyncId(string tableName, long syncid)
        {
            try
            {
                string keyName = StorageSyncIdKeyPrefix + tableName;

                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    // Abrir o crear la clave
                    using (RegistryKey key = baseKey.OpenSubKey(StorageSyncIdPath, true))
                    {
                        if (key == null)
                        {
                            // Crear la clave completa si no existe
                            using (RegistryKey newKey = baseKey.CreateSubKey(StorageSyncIdPath))
                            {
                                newKey.SetValue(keyName, syncid.ToString());
                                return;
                            }
                        }

                        key.SetValue(keyName, syncid.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                eventLog.WriteEntry($"Error saving sync ID for {tableName}: {ex.Message}", EventLogEntryType.Error);
            }
        }
        #endregion


        #region RabbitMQ
        private async Task SendData(IEnumerable<string> data)
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

                foreach(var item in data)
                {
                    await SendMessage(channel, item);
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
