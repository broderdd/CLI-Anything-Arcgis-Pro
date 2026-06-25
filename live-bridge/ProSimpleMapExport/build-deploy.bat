@echo off
REM Build + deploy the ProSimpleMapExport ArcGIS Pro bridge add-in, then remind to restart Pro.
REM Double-click this, or run it from a terminal.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-deploy.ps1"
echo.
pause
