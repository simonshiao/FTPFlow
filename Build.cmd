@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "FX=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FX%\WPF\PresentationFramework.dll" set "FX=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%CSC%" (
 echo .NET Framework 4.7.2 or later is required.
 exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:FTPFlow.exe /win32icon:assets\FTPFlow.ico /win32manifest:src\app.manifest /resource:src\MainWindow.xaml,MainWindow.xaml /reference:"%FX%\WPF\PresentationFramework.dll" /reference:"%FX%\WPF\PresentationCore.dll" /reference:"%FX%\WPF\WindowsBase.dll" /reference:"%FX%\System.Xaml.dll" /reference:"%FX%\System.Windows.Forms.dll" /reference:"%FX%\System.Xml.Linq.dll" /reference:"%FX%\System.Security.dll" /reference:"%FX%\System.Web.Extensions.dll" src\FtpClient.cs src\Program.cs src\AssemblyInfo.cs
if errorlevel 1 exit /b 1
echo Build succeeded: FTPFlow.exe
exit /b 0
