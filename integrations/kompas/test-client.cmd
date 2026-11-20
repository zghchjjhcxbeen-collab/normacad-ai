@echo off
REM Site client test without network or KOMPAS: token settings, Bearer header, account summary, refusal texts.
REM Window view test without WinForms: stages and queue, report cards and filters, account and document lines.
REM Builds bin\ClientTest.exe and bin\ViewTest.exe and runs both. Exit code 0 means all checks passed.
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
set SRC="%HERE%src\ApiClient.cs"

"%CSC%" %OPTS% /target:exe /main:NormaCad.Kompas.Tests.ClientTest /out:"%HERE%bin\ClientTest.exe" %REFS% %SRC% "%HERE%test\ClientTest.cs"
if errorlevel 1 exit /b 1
"%HERE%bin\ClientTest.exe"
set "RC=%errorlevel%"
"%CSC%" %OPTS% /target:exe /main:NormaCad.Kompas.Tests.ViewTest /out:"%HERE%bin\ViewTest.exe" %REFS% %SRC% "%HERE%src\CheckView.cs" "%HERE%test\ViewTest.cs"
if errorlevel 1 exit /b 1
"%HERE%bin\ViewTest.exe"
if not "%errorlevel%"=="0" set "RC=1"
exit /b %RC%
