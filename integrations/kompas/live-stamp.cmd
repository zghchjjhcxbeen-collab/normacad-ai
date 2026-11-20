@echo off
REM Live stamp reading (#45) in KOMPAS-3D on COPIES of documents: live-stamp.cmd <folder>.
REM Take the lock .claude\kompas.lock first. Exit code: 0 all read, 1 errors, 2 user's KOMPAS is running.
setlocal
set "HERE=%~dp0"
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

"%CSC%" %OPTS% /target:exe /main:NormaCad.Kompas.Tests.LiveStampTest /out:"%HERE%bin\LiveStampTest.exe" %REFS% %SRC% "%HERE%test\LiveStampTest.cs"
if errorlevel 1 exit /b 1
"%HERE%bin\LiveStampTest.exe" %1
exit /b %errorlevel%
