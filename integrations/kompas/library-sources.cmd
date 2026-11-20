@echo off
REM Shared source list for the KOMPAS library and its in-process test harnesses.
set SRC="%~dp0src\KompasLibrary.cs" "%~dp0src\ApiClient.cs" "%~dp0src\CheckForm.cs" "%~dp0src\StampReader.cs" "%~dp0src\StampCheck.cs" "%~dp0src\CheckView.cs"
set SRC=%SRC% "%~dp0src\WindowUi.cs" "%~dp0src\Launcher.cs" "%~dp0src\ProgressView.cs" "%~dp0src\ReportView.cs" "%~dp0src\FailureView.cs" "%~dp0src\Dialogs.cs"
set SRC=%SRC% /resource:"%~dp0..\..\public\assets\img\icon-192.png",NormaCAD.Mark.png
