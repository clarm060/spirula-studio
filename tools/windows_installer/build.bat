@echo off
rem Builds SpirulaSetup.exe with the C# compiler that ships with Windows.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe from .NET Framework 4 not found
    exit /b 1
)
set HERE=%~dp0
set OUT=%~1
if "%OUT%"=="" set OUT=%HERE%SpirulaSetup.exe

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
    /out:"%OUT%" ^
    /win32icon:"%HERE%..\..\assets\icon.ico" ^
    /win32manifest:"%HERE%app.manifest" ^
    /resource:"%HERE%..\..\assets\icon.ico",app.ico ^
    /r:System.dll /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll /r:System.IO.Compression.dll ^
    /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll ^
    "%HERE%SpirulaSetup.cs"
