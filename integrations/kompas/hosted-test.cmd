@echo off
REM Run only with exclusive KOMPAS access. Never attaches to an existing session.
REM hosted-test.cmd <KOMPAS exe> <fixture folder> <new output folder> [installed DLL]
setlocal
set "HERE=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%HERE%bin" mkdir "%HERE%bin"
"%CSC%" /nologo /codepage:65001 /optimize+ /platform:anycpu /warn:4 /out:"%HERE%bin\HostedWorkflowTest.exe" /r:System.dll /r:System.Core.dll /r:System.Security.dll /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll" /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll" "%HERE%test\HostedWorkflowTest.cs"
if errorlevel 1 exit /b 1
"%HERE%bin\HostedWorkflowTest.exe" %*
exit /b %errorlevel%

