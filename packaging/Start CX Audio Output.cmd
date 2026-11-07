@echo off
setlocal
cd /d "%~dp0"

if not exist "CXAudioOutput.exe" (
  echo.
  echo CXAudioOutput.exe was not found next to this launcher.
  echo Please extract the complete CX Audio Output folder first.
  echo.
  pause
  exit /b 1
)

echo Starting CX Audio Output...
echo The native WinUI control window will open.
echo Keep the app running while listening. Close the window to stop the server.
echo.
CXAudioOutput.exe
if errorlevel 1 (
  echo.
  echo CX Audio Output stopped with an error.
  pause
)
endlocal
