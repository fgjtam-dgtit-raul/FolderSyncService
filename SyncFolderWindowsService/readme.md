# Sync FJGTam Folders Windows Service

## Overview
This project is a **Windows Service** built with .NET. It is designed to run in the background and perform tasks without user interaction.

## Prerequisites
- .NET Framework
- Windows PowerShell (to run the install script)
- Administrator privileges (required to install services)
- Visual Studio (Optional)

## Build the Project
To build the project, simply compile the solution.

### Option 1: Using Visual Studio

1. Open the solution in Visual Studio.
2. Set the configuration to `Release`.
3. Build the solution (`Build > Build Solution`).

### Option 2: Using .NET CLI

```bash
dotnet build -c Release
```

### Insatall Service
To install the service, follow these steps:
    1.- Open a PowerShell terminal as Administrator.
    2.- Navigate to the output directory (bin\Release\netX) of the built project.
    3.- Run the installation script:
  ```bash
    .\install.ps1
  ```

### Allow Execute PS1 Files

Configure to execute .ps1 files on the current user.

```shell
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
```
