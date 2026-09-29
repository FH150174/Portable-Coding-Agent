@echo off
setlocal
set "ROOT=%~dp0.."
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WEBEXT=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Web.Extensions.dll"
if not exist "%CSC%" (
    echo Windows .NET Framework 4.x C# compiler is missing. Build on your own Windows PC with .NET Framework 4.x.
    exit /b 1
)
if not exist "%WEBEXT%" (
    echo System.Web.Extensions.dll is missing from .NET Framework.
    exit /b 1
)
if not exist "%ROOT%\Launcher\PortableAgent.cs" (
    echo Missing Launcher\PortableAgent.cs.
    exit /b 1
)
if not exist "%ROOT%\Launcher\SessionRelocator.cs" (
    echo Missing Launcher\SessionRelocator.cs.
    exit /b 1
)
"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /reference:"%WEBEXT%" /out:"%ROOT%\Launcher\PortableAgent.exe" "%ROOT%\Launcher\PortableAgent.cs" "%ROOT%\Launcher\SessionRelocator.cs"
if errorlevel 1 exit /b 1
echo Built Launcher\PortableAgent.exe
exit /b 0
