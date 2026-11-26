@echo off
title GrowSandbox Launcher & Auto-Updater
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0updater.ps1"
