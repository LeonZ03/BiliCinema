@echo off
setlocal EnableExtensions
pushd "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0script\start-watch.ps1"
set "APP_EXIT=%ERRORLEVEL%"
if "%APP_EXIT%"=="0" goto normal_exit

echo.
echo BiliCinema watch mode stopped with exit code %APP_EXIT%.
pause

:normal_exit
popd
exit /b %APP_EXIT%
