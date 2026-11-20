@echo off
REM Build NormaCadKompas.dll (KOMPAS-3D library), NormaCadCheck.exe (standalone window)
REM and NormaCadSetup.exe (one-file installer with both embedded as resources).
REM Needs only .NET Framework 4.x, which ships with Windows 10/11. No SDK, no KOMPAS.
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

REM Native MENU 2000, as in the installed SDK's CSharp Step2 sample.
"%CSC%" %OPTS% /target:exe /out:"%HERE%bin\BuildMenuResource.exe" "%HERE%src\BuildMenuResource.cs"
if errorlevel 1 exit /b 1
"%HERE%bin\BuildMenuResource.exe" "%HERE%bin\NormaCadMenu.res"
if errorlevel 1 exit /b 1
set "ILASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\ilasm.exe"
set "RTWARCH=/x64 /pe64"
if not exist "%ILASM%" (
  set "ILASM=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\ilasm.exe"
  set "RTWARCH=/x86"
)
"%ILASM%" /quiet /dll %RTWARCH% /resource="%HERE%bin\NormaCadMenu.res" /output="%HERE%bin\NormaCadKompas.rtw" "%HERE%src\KompasEntry.il"
if errorlevel 1 exit /b 1
"%CSC%" %OPTS% /target:library /win32res:"%HERE%bin\NormaCadMenu.res" /out:"%HERE%bin\NormaCadKompas.dll" %REFS% %SRC%
if errorlevel 1 exit /b 1
"%CSC%" %OPTS% /target:winexe /main:NormaCad.Kompas.Program /out:"%HERE%bin\NormaCadCheck.exe" %REFS% %SRC% "%HERE%src\Program.cs"
if errorlevel 1 exit /b 1
REM The installer links ApiClient.cs: uninstall signs out on the server the same way as the window's "Log out"
set SETUPREFS=%REFS% /r:System.Xml.dll /r:System.Xml.Linq.dll
"%CSC%" %OPTS% /target:winexe /main:NormaCad.Kompas.Setup.SetupProgram /out:"%HERE%bin\NormaCadSetup.exe" %SETUPREFS% /resource:"%HERE%bin\NormaCadKompas.dll",NormaCadKompas.dll /resource:"%HERE%bin\NormaCadKompas.rtw",NormaCadKompas.rtw /resource:"%HERE%bin\NormaCadCheck.exe",NormaCadCheck.exe "%HERE%src\Setup.cs" "%HERE%src\ApiClient.cs"
if errorlevel 1 exit /b 1
echo Build OK: %HERE%bin
