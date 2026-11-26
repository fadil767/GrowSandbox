@echo off
setlocal
cd /d "%~dp0"

echo Memeriksa GrowSandbox Server di UDP port 9050...
powershell -NoProfile -Command "if (-not (Get-NetUDPEndpoint -LocalPort 9050 -ErrorAction SilentlyContinue)) { exit 1 }"
if errorlevel 1 (
    echo Menyalakan GrowSandbox.Server...
    start "GrowSandbox Server" cmd /k "cd /d ""%~dp0"" && dotnet run --project src\GrowSandbox.Server --no-build"
    timeout /t 2 /nobreak >nul
) else (
    echo GrowSandbox.Server sudah berjalan di port 9050.
)

echo Menyalakan GrowSandbox.Client...
start "GrowSandbox Client" /D "%~dp0src\GrowSandbox.Client\bin\Debug\net8.0" "%~dp0src\GrowSandbox.Client\bin\Debug\net8.0\GrowSandbox.Client.exe"
endlocal

