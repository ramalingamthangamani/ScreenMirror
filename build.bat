@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: dotnet was not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download
  goto :fail
)

echo Publishing ScreenMirror to dist\ ...
if exist dist rmdir /s /q dist
dotnet publish src\ScreenMirror -c Release -p:SelfContained=false -p:PublishSingleFile=true -o dist
if errorlevel 1 (
  echo.
  echo ERROR: publish failed. If ScreenMirror.exe is running, close it and try again.
  goto :fail
)

if not exist "dist\ScreenMirror.exe" (
  echo ERROR: dist\ScreenMirror.exe was not produced.
  goto :fail
)

echo.
echo Done: %CD%\dist\ScreenMirror.exe
endlocal
exit /b 0

:fail
endlocal
if /i not "%~1"=="--no-pause" pause
exit /b 1
