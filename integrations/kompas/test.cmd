@echo off
REM Checks without KOMPAS. Builds everything (build.cmd), then:
REM   bin\ExportTest.exe - export on fake KOMPAS API objects, the same late-bound calls;
REM   bin\SetupTest.exe  - the installer on temporary folders and a temporary registry key,
REM                        with the files embedded in bin\NormaCadSetup.exe.
REM Exit code 0 means all checks passed.
setlocal
set "HERE=%~dp0"
call "%HERE%build.cmd"
if errorlevel 1 exit /b 1
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll
set OPTS=/nologo /codepage:65001 /optimize+ /platform:anycpu /warn:4
call "%HERE%library-sources.cmd"

"%CSC%" %OPTS% /target:exe /main:NormaCad.Kompas.Tests.ExportTest /out:"%HERE%bin\ExportTest.exe" %REFS% %SRC% "%HERE%test\ExportTest.cs"
if errorlevel 1 exit /b 1
"%HERE%bin\ExportTest.exe"
set "RC=%errorlevel%"
"%CSC%" %OPTS% /target:exe /main:NormaCad.Kompas.Tests.SetupTest /out:"%HERE%bin\SetupTest.exe" %REFS% /r:System.Xml.dll /r:System.Xml.Linq.dll "%HERE%src\Setup.cs" "%HERE%src\ApiClient.cs" "%HERE%test\SetupTest.cs"
if errorlevel 1 exit /b 1
"%HERE%bin\SetupTest.exe" "%HERE%bin\NormaCadSetup.exe"
if not "%errorlevel%"=="0" set "RC=1"
exit /b %RC%
