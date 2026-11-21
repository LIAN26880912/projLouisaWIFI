@echo off
rem Double-click to build (Release) and start the Louisa Wi-Fi tray app.
setlocal
cd /d "%~dp0"
set "EXE=%~dp0src\LouisaWiFi.Windows\bin\Release\net10.0-windows10.0.19041.0\LouisaWiFi.exe"

rem Already running: its exe is locked, so skip the build and just pass the command on.
tasklist /FI "IMAGENAME eq LouisaWiFi.exe" /NH | find /I "LouisaWiFi.exe" >nul
if not errorlevel 1 if exist "%EXE%" (
    start "" "%EXE%" %*
    exit /b 0
)

dotnet build src\LouisaWiFi.Windows\LouisaWiFi.Windows.csproj -c Release -nologo -v q
if errorlevel 1 (
    echo Build failed. Install the .NET 10 SDK or check the errors above.
    pause
    exit /b 1
)
start "" "%EXE%" %*
