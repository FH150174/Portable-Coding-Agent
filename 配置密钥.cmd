@echo off
setlocal
set "ROOT=%~dp0"
if not exist "%ROOT%Launcher\PortableAgent.exe" (
    echo Missing Launcher\PortableAgent.exe. Copy the complete assembled release first.
    pause
    exit /b 1
)
"%ROOT%Launcher\PortableAgent.exe" key
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Key setup failed. Check the message above.
pause
exit /b %RESULT%
