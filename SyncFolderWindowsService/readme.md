
## SETTINGS

### Set the Database connection string
To configure the database connection for this application, open the App.config or SyncFolderWindowsService.exe.config file and ensure the following <connectionStrings> section is present:

```xml
<configuration>
    <configSections>
    </configSections>
    <connectionStrings>
        <add name="SyncFolderWindowsService.Properties.Settings.SJP_CARPETAS_CON"
            connectionString="Server=localhost;Database=SJP_CARPETAS;User Id=usr;Password=pass;Encrypt=true;TrustServerCertificate=true;" />
    </connectionStrings>
    .
    .
</configuration>
```

### Allow Execute PS1 Files

Configure to execute .ps1 files on the current user.

```shell
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
```