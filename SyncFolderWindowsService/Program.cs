using System;
using System.Collections.Generic;
using System.Configuration.Install;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;


namespace SyncFolderWindowsService
{
    internal class Program : ServiceBase
    {
        private const string EventLogSource = "DGTIT Sync Folders";
        private const string EventLogName = "Application";
        private EventLog eventLog;

        private const string StorageSyncIdPath = @"SOFTWARE\DGTITFolders";
        private const string StorageSyncIdKey = "syncid";

        private readonly TimeSpan syncInterval = TimeSpan.FromSeconds(5);

        private Task processTask;
        private CancellationTokenSource cancellationTokenSource;

        static void Main(string[] args)
        {
            ServiceBase.Run(new Program());
        }

        public Program()
        {
            this.ServiceName = "DGTIT Sync Folders";

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

            processTask = Task.Run( () =>
            {
                while (!cancelationToken.IsCancellationRequested)
                {
                    eventLog.WriteEntry("Access sync id", EventLogEntryType.Information);

                    // get the current id
                    try
                    {
                        // open the registry key from the 64-bit registry view
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        {
                            using (RegistryKey key = baseKey.OpenSubKey(StorageSyncIdPath, true))
                            {
                                if (key != null)
                                {
                                    long id = 0;
                                    var currentValue = key.GetValue(StorageSyncIdKey)?.ToString();
                                    if (!string.IsNullOrEmpty(currentValue))
                                    {
                                        id = long.TryParse(currentValue, out long currentId)
                                            ? currentId
                                            : throw new ArgumentException($"can't parse the stored id '{currentValue}'");
                                    }

                                    id++;

                                    // set the new value
                                    key.SetValue(StorageSyncIdKey, id.ToString());
                                }
                                else
                                {
                                    eventLog.WriteEntry("Registry key not found.", EventLogEntryType.Error);
                                }
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        eventLog.WriteEntry($"Error al actualizar el id: {ex.Message} {ex.StackTrace}", EventLogEntryType.Error);
                    }

                    System.Threading.Thread.Sleep(syncInterval);
                }
            }, cancelationToken);
        }
        
        protected override void OnStop()
        {
            this.cancellationTokenSource?.Cancel();
            eventLog.WriteEntry("DGTIT Sync Service stopped", EventLogEntryType.Information);
            base.OnStop();
        }
    }
}
