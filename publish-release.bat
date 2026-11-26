@echo off
title GrowSandbox - Push & Publish Release ke GitHub
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish-release.ps1"
echo.
pause
