@echo off
setlocal
set PYTHONUTF8=1
if defined HOLOLENS_PYTHON goto custom
if exist "%~dp0.venv\Scripts\python.exe" goto venv
if exist "%~dp0..\HoloLens1Environment\tools\Python\3.14.10\python.exe" goto shared
where py >nul 2>nul
if not errorlevel 1 goto launcher
where python >nul 2>nul
if not errorlevel 1 goto system
echo Python 3.11+ is required. Install Python or set HOLOLENS_PYTHON to its executable. 1>&2
exit /b 1

:custom
"%HOLOLENS_PYTHON%" -u "%~dp0server\server.py" --open-browser %*
exit /b %errorlevel%
:venv
"%~dp0.venv\Scripts\python.exe" -u "%~dp0server\server.py" --open-browser %*
exit /b %errorlevel%
:shared
"%~dp0..\HoloLens1Environment\tools\Python\3.14.10\python.exe" -u "%~dp0server\server.py" --open-browser %*
exit /b %errorlevel%
:launcher
py -3 -u "%~dp0server\server.py" --open-browser %*
exit /b %errorlevel%
:system
python -u "%~dp0server\server.py" --open-browser %*
exit /b %errorlevel%
