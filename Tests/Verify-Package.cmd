@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ROOT=%~dp0.."
set "APP=%ROOT%\Launcher\PortableAgent.exe"
if not exist "%APP%" (
  echo FAIL: Launcher\PortableAgent.exe is missing.
  exit /b 1
)
"%APP%" verify
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
  echo FAIL: Offline package verification failed.
  exit /b %RESULT%
)
echo PASS: Runtime self-check completed. Compare ZIP and launcher SHA-256 against the release record before use.
echo F01-F10 still require the steps in the acceptance guide.
exit /b 0