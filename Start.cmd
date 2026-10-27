@echo off
cd /d "%~dp0"
if not exist "FTPFlow.exe" call Build.cmd
if not exist "FTPFlow.exe" (
 pause
 exit /b 1
)
start "" "%~dp0FTPFlow.exe"
