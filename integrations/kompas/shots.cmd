@echo off
REM Window screenshots for checking against mockup\*.png: the real window on a fake local server (port 18047) and a
REM fake KOMPAS export. No KOMPAS and no real site needed. Builds bin\WindowShots.exe and writes:
REM   100\ - as at 100% (process is not DPI-aware: on a 150% screen the window is drawn at 96 dpi);
REM   150\ - progress and report at the screen scale (normal run, window is DPI-aware).
REM First argument - output folder, default bin\shots. Screenshots are not committed.
setlocal
set "HERE=%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%HERE%bin\shots"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found: .NET Framework 4 is required.
  exit /b 1
)
if not exist "%HERE%bin" mkdir "%HERE%bin"

set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll
set OPTS=/nologo /codepage:65001 /optimize+ /platform:anycpu /warn:4
call "%HERE%library-sources.cmd"

"%CSC%" %OPTS% /target:exe /main:NormaCad.Kompas.Tests.WindowShots /out:"%HERE%bin\WindowShots.exe" %REFS% %SRC% "%HERE%test\WindowShots.cs"
if errorlevel 1 exit /b 1

set "RC=0"
set "NORMACAD_KOMPAS_DPI=unaware"
"%HERE%bin\WindowShots.exe" "%OUT%" 100 account local progress report report-dark no-internet limit dialogs
if errorlevel 1 set "RC=1"
set "NORMACAD_KOMPAS_DPI="
"%HERE%bin\WindowShots.exe" "%OUT%" 150 progress report report-dark
if errorlevel 1 set "RC=1"
exit /b %RC%
