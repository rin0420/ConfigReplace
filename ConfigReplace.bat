@echo off
setlocal
chcp 65001 >nul

rem ConfigReplace simple launcher. Requires only Windows PowerShell.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0ConfigReplace.ps1" %*
if errorlevel 1 (
  echo.
  echo ConfigReplace failed.
  pause
)

endlocal
