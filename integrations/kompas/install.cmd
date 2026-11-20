@echo off
REM Install the KOMPAS-3D extension for the current user (no administrator rights needed).
REM Run build.cmd first. Close KOMPAS-3D before installing.
setlocal
set "SETUP=%~dp0bin\NormaCadSetup.exe"
if not exist "%SETUP%" (
  echo %SETUP% not found. Run build.cmd first.
  pause
  exit /b 1
)
"%SETUP%" /install
