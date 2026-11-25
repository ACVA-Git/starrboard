@echo off
set "SCRIPT=%~dp0Uninstall-Starrboard.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','%SCRIPT%','-Pause'); exit $process.ExitCode"
if errorlevel 1 (
  echo.
  echo Starrboard uninstall failed. Review the newest Uninstall log under %ProgramData%\Starrboard\backups.
  pause
)
