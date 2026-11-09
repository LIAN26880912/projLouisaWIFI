@echo off
rem Double-click to build (Release) and start the Louisa Wi-Fi tray app.
setlocal
cd /d "%~dp0"
set "EXE=%~dp0src\LouisaWiFi.Windows\bin\Release\net10.0-windows10.0.19041.0\LouisaWiFi.exe"

dotnet build src\LouisaWiFi.Windows\LouisaWiFi.Windows.csproj -c Release -nologo -v q
if errorlevel 1 (
    if not exist "%EXE%" (
        echo Build failed. Install the .NET 10 SDK or check the errors above.
        pause
        exit /b 1
    )
    rem Usually the app is already running and its exe is locked; the new launch just shows its status.
    echo Build skipped or failed; starting the last built version.
    timeout /t 3 >nul
)
start "" "%EXE%" %*
