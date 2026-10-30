@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Demo.ps1" -Stage Compile -SimulatedCamera
pause
