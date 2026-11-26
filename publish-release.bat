@echo off
title GrowSandbox - Kirim Update ke GitHub
echo ==============================================
echo   GROWSANDBOX - KIRIM UPDATE KE TEMAN (1 KLIK)
echo ==============================================
echo.
echo Semua perubahan akan di-commit, di-push ke GitHub,
echo lalu ZIP baru di-build dan diunggah ke GitHub Releases.
echo Teman cukup klik Update_dan_Main.bat untuk update otomatis.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish-release.ps1" %*
echo.
pause
