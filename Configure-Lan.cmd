@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Configure-Lan.ps1" -AllowLan
pause
