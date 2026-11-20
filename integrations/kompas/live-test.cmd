@echo off
REM Export test in a live KOMPAS-3D: real documents, real PDF converter.
REM Usage: live-test.cmd <folder with COPIES of .cdw .spw .kdw .frw .m3d .a3d files> [--attach]
REM Starts KOMPAS (about 30 s) and quits it afterwards. A KOMPAS that is already running belongs to someone:
REM the test refuses to use it unless --attach is given.
REM If the extension is installed (NormaCadSetup.exe), also checks that KOMPAS lists it under Applications.
REM Builds bin\LiveExportTest.exe. Exit code: 0 passed, 1 failed, 2 KOMPAS not started (failed or already running).
setlocal
set "HERE=%~dp0"
if "%~1"=="" (
  echo Usage: live-test.cmd ^<folder with copies of .cdw .spw .kdw .frw .m3d .a3d files^> [--attach]
  echo Work on COPIES only: the test opens the documents in KOMPAS-3D.
  exit /b 1
)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found: .NET Framework 4 is required.
  exit /b 1
)
if not exist "%HERE%bin" mkdir "%HERE%bin"

REM Same references and options as test.cmd
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll
set OPTS=/nologo /codepage:65001 /optimize+ /platform:anycpu /warn:4
call "%HERE%library-sources.cmd"

"%CSC%" %OPTS% /target:exe /main:NormaCad.Kompas.Tests.LiveExportTest /out:"%HERE%bin\LiveExportTest.exe" %REFS% %SRC% "%HERE%test\LiveExportTest.cs"
if errorlevel 1 exit /b 1
"%HERE%bin\LiveExportTest.exe" %*
exit /b %errorlevel%
