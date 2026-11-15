@echo off
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set VER=1.1.0

echo [1/2] Building HWInspector.exe ...
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:app.ico /out:HWInspector.exe ^
  /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Xaml.dll ^
  /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\WPF\WindowsFormsIntegration.dll" ^
  HWInspector.cs Board.cs Board3D.cs || exit /b 1

echo [2/2] Building installer ...
if not exist dist mkdir dist
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:app.ico /out:dist\HWInspector-Setup-%VER%.exe ^
  /resource:HWInspector.exe,payload.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  Setup.cs || exit /b 1

echo Done: dist\HWInspector-Setup-%VER%.exe
