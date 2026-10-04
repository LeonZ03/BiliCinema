@echo off
setlocal EnableExtensions
pushd "%~dp0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "AVALONIA_TELEMETRY_OPTOUT=1"

set "DOTNET_EXE=dotnet.exe"
if not exist "%TEMP%\downkyi-dotnet\dotnet.exe" goto check_sdk
set "DOTNET_ROOT=%TEMP%\downkyi-dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_EXE=%DOTNET_ROOT%\dotnet.exe"

:check_sdk
"%DOTNET_EXE%" --version >nul 2>nul
if errorlevel 1 goto missing_app

echo Opening BiliCinema watch mode from current source...
"%DOTNET_EXE%" run --project "DownKyi\DownKyi.csproj" -c Release --no-launch-profile --verbosity quiet -- --watch
set "APP_EXIT=%ERRORLEVEL%"
goto finished

:missing_app
echo .NET 10 SDK was not found.
echo Install the .NET 10 SDK, then double-click this file again.
set "APP_EXIT=1"

:finished
if "%APP_EXIT%"=="0" goto normal_exit
echo.
echo Watch app stopped with exit code %APP_EXIT%.
pause

:normal_exit
popd
exit /b %APP_EXIT%
