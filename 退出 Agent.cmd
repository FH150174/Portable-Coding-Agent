@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0Launcher\PortableAgent.exe" (
  echo Missing Launcher\PortableAgent.exe. Assemble the release package on your own PC.
  pause
  exit /b 1
)
"%~dp0Launcher\PortableAgent.exe" stop
pause
