@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0worker-tools.ps1" check
pause
