@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Open-Monitor.ps1" -View system-live %*
if errorlevel 1 pause
