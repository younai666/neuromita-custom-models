@echo off
rem ---------------------------------------------------------------------------
rem  NeuroMita.CustomModels - one-click installer
rem
rem  Double-click this file. It installs BepInEx if needed, puts the plugin in
rem  place, and creates the CustomModels folder.
rem
rem  You can also drag the game folder (or NeuroMita.exe) onto this file.
rem ---------------------------------------------------------------------------

setlocal
if "%~1"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -GameDir "%~1"
)
endlocal
