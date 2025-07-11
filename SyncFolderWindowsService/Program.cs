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
using RabbitMQ.Client;

namespace SyncFolderWindowsService
{
    internal class Program : ServiceBase
    {
        private const string EventLogSource = "DGTIT Sync Folders";
        private const string EventLogName = "Application";
        private EventLog eventLog;

        private const string StorageSyncIdPath = @"SOFTWARE\DGTITFolders";
        private const string StorageSyncIdKey = "syncid";

        private readonly TimeSpan syncInterval = TimeSpan.FromSeconds(15);
        private readonly string rabbitMqHost = "localhost";

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
        private (IEnumerable<string>, long) GetChangedFolders(long syncId)
        {
            // TODO: with the sync id, get the folders modified and the new syncId
            long[] foldersModified = new long[] { (100) + syncId }; // sample ids


            var foldersSnapshot = new string[foldersModified.Length];
            for(int i = 0; i < foldersModified.Length; i++)
            {
                foldersSnapshot[i] = this.GetFolderSnapshot(foldersModified[i]);
            }

            var newSyncId = syncId + 1;
            return (foldersSnapshot, newSyncId);
        }

        private string GetFolderSnapshot(long folderId)
        {
            // TODO: call the stored procedure and get the folder snapshot
            var folder1 = "{\r\n    \\\"ID_CARPETA\\\": 1,\r\n    \\\"NUC\\\": \\\"1\\\",\r\n    \\\"RAC\\\": \\\"1\\\",\r\n    \\\"NUM\\\": \\\"1\\\",\r\n    \\\"NAC\\\": \\\"1\\\",\r\n    \\\"NUMERO_SOLICITUD\\\": \\\"1\\\",\r\n    \\\"DETENIDO\\\": true,\r\n    \\\"FECHA_NUC\\\": \\\"2025-06-17T00:00:00\\\",\r\n    \\\"FECHA_RAC\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"FECHA_NUM\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"FECHA_NAC\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"FECHA_NUMERO_SOLICITUD\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"FECHA_ESTADO_NUC\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"FECHA_ESTADO_RAC\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"FECHA_ESTADO_NUM\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"FECHA_ESTADO_NAC\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"ACTIVO_NUC\\\": true,\r\n    \\\"ACTIVO_RAC\\\": true,\r\n    \\\"ACTIVO_NUM\\\": true,\r\n    \\\"ACTIVO_NAC\\\": true,\r\n    \\\"FECHA_REGISTRO\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"DH\\\": true,\r\n    \\\"MODIFICADO\\\": 1,\r\n    \\\"ENVIAR_PERICIALES\\\": 1,\r\n    \\\"NUC_NUEVO\\\": \\\"1\\\",\r\n    \\\"RAC_NUEVO\\\": \\\"1\\\",\r\n    \\\"NUM_NUEVO\\\": \\\"1\\\",\r\n    \\\"NAC_NUEVO\\\": \\\"1\\\",\r\n    \\\"NUMERO_SOLICITUD_NUEVO\\\": \\\"1\\\",\r\n    \\\"FECHA_PRESCRIPCION\\\": \\\"2025-06-18T00:00:00\\\",\r\n    \\\"ID_MUNICIPIO_CARPETA\\\": 1,\r\n    \\\"MUNICIPIO_CARPETA\\\": \\\"ABASOLO \\\",\r\n    \\\"IdTipoDQ\\\": 1,\r\n    \\\"TipoDQ\\\": \\\"DENUNCIA\\\",\r\n    \\\"ID_MASC\\\": 1,\r\n    \\\"MASC\\\": \\\"MEDIACION\\\",\r\n    \\\"ID_FORMA_INICIO\\\": 1,\r\n    \\\"FORMA_INICIO\\\": \\\"COMPARECENCIA\\\",\r\n    \\\"ID_IPH\\\": 1,\r\n    \\\"IPH\\\": \\\"DENUNCIA ANONIMA / RESERVA DE IDENTIDAD\\\",\r\n    \\\"ID_ESTADO_NUC\\\": 1,\r\n    \\\"ID_ESTADO_RAC\\\": 1,\r\n    \\\"ID_ESTADO_NUM\\\": 1,\r\n    \\\"ID_ESTADO_NAC\\\": 1,\r\n    \\\"ID_USUARIO_NUC\\\": 1,\r\n    \\\"ID_USUARIO_RAC\\\": 1,\r\n    \\\"ID_USUARIO_NUM\\\": 1,\r\n    \\\"ID_USUARIO_NAC\\\": 1,\r\n    \\\"ID_USUARIO_NUMERO_SOLICITUD\\\": 1,\r\n    \\\"ID_MP_NUC\\\": 1,\r\n    \\\"ID_MP_RAC\\\": 1,\r\n    \\\"ID_MP_NUM\\\": 1,\r\n    \\\"ID_MP_NAC\\\": 1,\r\n    \\\"ID_MP_RAC_ACTUAL\\\": 1,\r\n    \\\"ID_MP_NUC_ACTUAL\\\": 1,\r\n    \\\"CARPETA_PERSONA\\\": [\r\n        {\r\n            \\\"ID_CARPETA\\\": 1,\r\n            \\\"ID_PERSONA\\\": 1,\r\n            \\\"ID_PERSONA_CARPETA\\\": 1,\r\n            \\\"FOLIO\\\": \\\"1\\\",\r\n            \\\"LEER_ESCRIBIR\\\": true,\r\n            \\\"VIVO\\\": true,\r\n            \\\"DETENIDO\\\": true,\r\n            \\\"FECHA_REGISTRO\\\": \\\"2025-06-17T00:00:00\\\",\r\n            \\\"NombrePadre\\\": \\\"Rangel\\\",\r\n            \\\"NombreMadre\\\": \\\"Almaguer\\\",\r\n            \\\"ID_PUSO_DISPOSICION\\\": 1,\r\n            \\\"PUSO_DISPOSICION\\\": \\\"SEDENA\\\",\r\n            \\\"ID_ESTADO_CIVIL\\\": 1,\r\n            \\\"ESTDO_CVL\\\": \\\"CASADO(A)\\\",\r\n            \\\"ID_ESCOLARIDAD\\\": 1,\r\n            \\\"ESCOLARIDAD\\\": \\\"BACHILLERATO\\\",\r\n            \\\"ID_OCUPACION\\\": 11,\r\n            \\\"OCUPACION\\\": \\\"CHOFER\\\",\r\n            \\\"ID_IDENTIFICACION\\\": 1,\r\n            \\\"IDENTIFICACION\\\": \\\"CARTILLA MILITAR\\\",\r\n            \\\"ID_TIPO_ACTOR\\\": 1,\r\n            \\\"TIPO_ACTOR\\\": \\\"DENUNCIANTE\\\",\r\n            \\\"ID_ESTADO_CARPETA\\\": 1,\r\n            \\\"ESTADO_CARPETA\\\": \\\"RECIBIDA\\\",\r\n            \\\"ID_PROFESION\\\": 1,\r\n            \\\"PROFESION\\\": \\\"LIC. EN INFORMATICA\\\",\r\n            \\\"ID_ETNIA\\\": 1,\r\n            \\\"ETNIA\\\": \\\"SIN DATO\\\",\r\n            \\\"ID_IPH\\\": 1,\r\n            \\\"IPH\\\": \\\"DENUNCIA ANONIMA / RESERVA DE IDENTIDAD\\\",\r\n            \\\"IdAgresionPeriodista\\\": true,\r\n            \\\"PERSONAS\\\": [\r\n                {\r\n                    \\\"ID_PERSONA\\\": 1,\r\n                    \\\"PATERNO\\\": \\\"Rangel\\\",\r\n                    \\\"MATERNO\\\": \\\"Almaguer\\\",\r\n                    \\\"NOMBRE\\\": \\\"Juan\\\",\r\n                    \\\"FECHA_NACIMIENTO_MAL\\\": \\\"1993-12-17T00:00:00\\\",\r\n                    \\\"FECHA_NACIMIENTO\\\": \\\"1993-12-17\\\",\r\n                    \\\"EDAD\\\": 32,\r\n                    \\\"RFC\\\": \\\"RAAJ931217SX4\\\",\r\n                    \\\"CURP\\\": \\\"RAAJ931217HTGNLN03\\\",\r\n                    \\\"DECLARACION\\\": \\\"No aplica\\\",\r\n                    \\\"FECHA_REGISTRO\\\": \\\"2025-06-18T00:00:00\\\",\r\n                    \\\"EspecificoFN\\\": true,\r\n                    \\\"TIPO_CALCULO\\\": \\\"1\\\",\r\n                    \\\"LGBTI\\\": true,\r\n                    \\\"PAREJA_ACTUAL\\\": \\\"1\\\",\r\n                    \\\"SALARIO_MENSUAL\\\": \\\"1\\\",\r\n                    \\\"TieneAdicciones\\\": true,\r\n                    \\\"ESPECIFIQUE_ADICCIONES\\\": \\\"Cocacola\\\",\r\n                    \\\"EspecifiqueLGBTI\\\": 1,\r\n                    \\\"EDAD_CALCULADA\\\": 32,\r\n                    \\\"ID_MUNICIPIO_PERSONA\\\": 1,\r\n                    \\\"MUNICIPIO_PERSONA\\\": \\\"ABASOLO \\\",\r\n                    \\\"ID_SEXO\\\": 1,\r\n                    \\\"SEXO\\\": \\\"MASCULINO\\\",\r\n                    \\\"ID_NACIONALIDAD\\\": 1,\r\n                    \\\"NACIONALIDAD\\\": \\\"AFRICANO\\\",\r\n                    \\\"ID_PAIS\\\": 1,\r\n                    \\\"PAIS\\\": \\\"MEXICO\\\",\r\n                    \\\"ID_ESTADO\\\": 1,\r\n                    \\\"ESTADO\\\": \\\"AGUASCALIENTES\\\",\r\n                    \\\"ID_MUNICIPIO\\\": 1,\r\n                    \\\"MUNICIPIO\\\": \\\"AGUASCALIENTES \\\",\r\n                    \\\"ID_IPH\\\": 1\r\n                }\r\n            ]\r\n        }\r\n    ],\r\n    \\\"DELITOS\\\": [\r\n        {\r\n            \\\"ID_CARPETA\\\": 1,\r\n            \\\"ID_DELITO\\\": 1,\r\n            \\\"ID_CONSECUTIVO_DELITO\\\": 1,\r\n            \\\"ID_LUGAR_HECHOS\\\": 1,\r\n            \\\"FECHA_REGISTRO\\\": \\\"2025-06-16T00:00:00\\\",\r\n            \\\"FECHA_MODIFICACION\\\": \\\"2025-06-16T00:00:00\\\",\r\n            \\\"ExisteIndicio\\\": true,\r\n            \\\"NumVictimas\\\": 1,\r\n            \\\"DescripcionTortura\\\": \\\"Alguna descripcon aqui....\\\",\r\n            \\\"VEHICULO_INVOLUCRADO\\\": 1,\r\n            \\\"TOTAL_VEHICULO\\\": \\\"1\\\",\r\n            \\\"ID_MUNICIPIO_DELITO\\\": 1,\r\n            \\\"MUNICIPIO_DELITO\\\": \\\"ABASOLO \\\",\r\n            \\\"ID_MODALIDAD\\\": 1,\r\n            \\\"MODALIDAD\\\": \\\"DOLOSO\\\",\r\n            \\\"ID_VIOLENCIA\\\": true,\r\n            \\\"VIOLENCIA\\\": \\\"SI\\\",\r\n            \\\"ID_GRAVE\\\": true,\r\n            \\\"ID_PRINCIPAL\\\": true,\r\n            \\\"ID_ACCION\\\": 1,\r\n            \\\"ACCION\\\": \\\"RIÑA\\\",\r\n            \\\"IdGradoEjecucion\\\": 1,\r\n            \\\"IdCircunstancia\\\": 1,\r\n            \\\"CIRCUNSTANCIA\\\": \\\"TORTURA CON LA AUTORIDAD MINISTERIAL\\\"\r\n        }\r\n    ],\r\n    \\\"LUGAR_HECHOS\\\": [\r\n        {\r\n            \\\"ID_CARPETA\\\": 1,\r\n            \\\"ID_LUGAR_HECHOS\\\": 1,\r\n            \\\"FECHA_HECHOS\\\": \\\"2025-06-17T00:00:00\\\",\r\n            \\\"HORA_HECHOS\\\": \\\"09:00\\\",\r\n            \\\"NO_EXTERIOR\\\": \\\"1\\\",\r\n            \\\"MANZANA\\\": \\\"1\\\",\r\n            \\\"LOTE\\\": \\\"1\\\",\r\n            \\\"LATITUD\\\": \\\"18.222\\\",\r\n            \\\"LONGITUD\\\": \\\"121.1111\\\",\r\n            \\\"REFERENCIAS\\\": \\\"Sin referencias\\\",\r\n            \\\"FECHA_REGISTRO\\\": \\\"2025-06-17T00:00:00\\\",\r\n            \\\"BRECHA\\\": \\\"123\\\",\r\n            \\\"KILOMETRO\\\": \\\"123\\\",\r\n            \\\"CIRCUNSTANCIAS_LOCALIZACION\\\": \\\"11\\\",\r\n            \\\"PROFUNDIDAD_FOSA\\\": \\\"1\\\",\r\n            \\\"NUMERO_MIN_INDIVIDUOS\\\": 1,\r\n            \\\"NUMERO_RESTOS\\\": 1,\r\n            \\\"NUMERO_CADAVERES\\\": 1,\r\n            \\\"MetodologiaDeRecuperacion\\\": \\\"1\\\",\r\n            \\\"CP\\\": \\\"87025\\\",\r\n            \\\"CONOCE_COORDENADAS\\\": 1,\r\n            \\\"ID_MUNICIPIO_LUGAR_HECHOS\\\": 1,\r\n            \\\"MUNICIPIO_LUGAR_HECHOS\\\": \\\"ABASOLO \\\",\r\n            \\\"ID_TIPO_LUGAR\\\": 1,\r\n            \\\"TIPO_LUGAR\\\": \\\"CERRADO\\\",\r\n            \\\"ID_PAIS\\\": 1,\r\n            \\\"PAIS\\\": \\\"MEXICO\\\",\r\n            \\\"ID_ESTADO\\\": 28,\r\n            \\\"ESTADO\\\": \\\"TAMAULIPAS\\\",\r\n            \\\"ID_MUNICIPIO\\\": 2,\r\n            \\\"MUNICIPIO\\\": \\\"ALDAMA \\\",\r\n            \\\"ID_LOCALIDAD\\\": 42,\r\n            \\\"LOCALIDAD\\\": \\\"PAPALOTE, EL\\\",\r\n            \\\"ID_COLONIA\\\": 1,\r\n            \\\"COLONIA\\\": \\\"DOMICILIO CONOCIDO\\\",\r\n            \\\"ID_CALLE\\\": 1,\r\n            \\\"CALLE\\\": \\\"SIN NOMBRE\\\",\r\n            \\\"ID_ENTRE_CALLE\\\": 2,\r\n            \\\"ID_Y_CALLE\\\": 3,\r\n            \\\"ID_TIPO_FOSA\\\": 1,\r\n            \\\"ID_CLASE_ENTIERRO\\\": 1,\r\n            \\\"CLASE_ENTIERRO\\\": \\\"PRIMARIO\\\",\r\n            \\\"ID_TIPO_ENTIERRO\\\": 1,\r\n            \\\"TIPO_ENTIERRO\\\": \\\"DIRECTO\\\",\r\n            \\\"ID_TIPO_INHUMACION\\\": 1,\r\n            \\\"TIPO_INHUMACION\\\": \\\"SINCRONO\\\",\r\n            \\\"ID_IPH\\\": 1,\r\n            \\\"IPH\\\": \\\"DENUNCIA ANONIMA / RESERVA DE IDENTIDAD\\\",\r\n            \\\"ID_TIPO_SUELO\\\": 1,\r\n            \\\"TIPO_SUELO\\\": \\\"ARENOSO\\\",\r\n            \\\"ID_SUELO\\\": 1,\r\n            \\\"SUELO\\\": \\\"PAVIMENTO\\\",\r\n            \\\"ID_CLIMA\\\": 1,\r\n            \\\"CLIMA\\\": \\\"NUBLADO\\\",\r\n            \\\"ID_ILUMINACION\\\": 1,\r\n            \\\"ILUMINACION\\\": \\\"NATURAL\\\",\r\n            \\\"ID_LUGAR_INSPECCION\\\": 1\r\n        }\r\n    ]\r\n}";
            return folder1;
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
            var factory = new ConnectionFactory() { HostName = this.rabbitMqHost };
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
