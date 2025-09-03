# Windows Service Sync

Enable Change Tracking on your database and tables

```sql
ALTER DATABASE YourDatabase SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
```

Enable Change Tracking for each table

```sql
ALTER TABLE [dbo].[PGJ_CARPETA] ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = ON);
ALTER TABLE [dbo].[PGJ_DOCUMENTO] ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = ON);
```

Add Tables to Configuration: Simply add each table to the `tablesToSync` list:

```c#
new TableSyncInfo("PGJ_CARPETA", "ID_CARPETA"),
new TableSyncInfo("PGJ_DOCUMENTO", "ID_DOCUMENTO"),
// Add all 40 tables here...
```

# Windows Service Sync

## Multi-Database Synchronization

This service supports synchronizing data from multiple databases to a centralized PostgreSQL database. Each source database is identified by unique identifiers to prevent ID conflicts.

### Configuration for Multiple Databases

Each Windows Service instance should be configured with unique identifiers:

```xml
<appSettings>
    <!-- Unique database identifier -->
    <add key="DatabaseIdentifier" value="SJP_CARPETAS_LOCATION_A"/>
    <!-- Unique location code -->
    <add key="LocationCode" value="LOC_A"/>
</appSettings>
```

### Data Structure

Each synchronized record includes:
- `SourceDatabase`: Identifies the source database
- `LocationCode`: Short location identifier
- `GlobalId`: Composite key format: `{LocationCode}_{TableName}_{OriginalId}`
- `SyncTimestamp`: UTC timestamp of synchronization
- All original table columns

### PostgreSQL Centralized Database Schema

```sql
-- Example table structure for centralized database
CREATE TABLE sync_pgj_carpeta (
    global_id VARCHAR(255) PRIMARY KEY,
    source_database VARCHAR(100) NOT NULL,
    location_code VARCHAR(20) NOT NULL,
    original_id BIGINT NOT NULL,
    operation CHAR(1) NOT NULL, -- I/U/D
    change_version BIGINT NOT NULL,
    sync_timestamp TIMESTAMP WITH TIME ZONE NOT NULL,
    -- Original table columns
    ultima_modificacion TIMESTAMP,
    -- Add other columns as needed
    UNIQUE(location_code, original_id)
);
```

In Database create a Store Procedure to handle changes

```sql
CREATE OR ALTER PROCEDURE [SYNCF].[SP_ObtenerCarpetaModificadas]
    @PREVIOUS_SYNC_VERSION BIGINT = 0
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @CURRENT_VERSION BIGINT = CHANGE_TRACKING_CURRENT_VERSION();
    DECLARE @MIN_VALID_VERSION BIGINT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'dbo.PGJ_CARPETA'));

    IF @PREVIOUS_SYNC_VERSION IS NULL SET @PREVIOUS_SYNC_VERSION = 0;

    IF @MIN_VALID_VERSION IS NOT NULL AND @PREVIOUS_SYNC_VERSION < @MIN_VALID_VERSION

    BEGIN
        THROW 51000, 'Sync anchor is too old for dbo.PGJ_CARPETA. Reinitialize and retry.', 1;
    END;

    SELECT
        CT.ID_CARPETA,
        CT.SYS_CHANGE_OPERATION AS OPERATION,      -- 'I','U','D'
        CT.SYS_CHANGE_VERSION   AS CHANGE_VERSION, -- change tracking version of the change
        CARPETA.ULTIMA_MODIFICACION
    FROM CHANGETABLE(CHANGES [dbo].[PGJ_CARPETA], @PREVIOUS_SYNC_VERSION) AS CT
    LEFT JOIN [dbo].[PGJ_CARPETA] AS CARPETA ON CT.ID_CARPETA = CARPETA.ID_CARPETA;
    -- Note: Use LEFT JOIN to include deleted rows
    --       CARPETA.* will be NULL for deleted rows

    SELECT @CURRENT_VERSION AS synchronization_version;
END;
GO
```
