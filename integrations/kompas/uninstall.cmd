@echo off
REM Remove the KOMPAS-3D extension for the current user. Close KOMPAS-3D first.
setlocal
set "SETUP=%~dp0bin\NormaCadSetup.exe"
if not exist "%SETUP%" (
  echo %SETUP% not found. Run build.cmd first.
  pause
  exit /b 1
)
"%SETUP%" /uninstall
