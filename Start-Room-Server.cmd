@echo off
setlocal EnableExtensions
pushd "%~dp0"

set "RoomServer__ListenUrl=http://127.0.0.1:5077"
set "DOTNET_EXE=dotnet.exe"

if not exist "%TEMP%\downkyi-dotnet\dotnet.exe" goto check_sdk
set "DOTNET_ROOT=%TEMP%\downkyi-dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_EXE=%DOTNET_ROOT%\dotnet.exe"

:check_sdk
"%DOTNET_EXE%" --version >nul 2>nul
if errorlevel 1 goto bundled_server

echo Room server: ws://127.0.0.1:5077/ws
echo Health check: http://127.0.0.1:5077/health
echo Create a room in BiliCinema to generate a Cloudflare invite.
echo Press Ctrl+C to stop the server.
echo.
"%DOTNET_EXE%" run --project "src\DownKyi.RoomServer\DownKyi.RoomServer.csproj" -c Release --no-launch-profile --verbosity quiet
set "SERVER_EXIT=%ERRORLEVEL%"
goto finished

:bundled_server
if not exist "artifacts\RoomServer-win-x64\DownKyi.RoomServer.exe" goto missing_runtime
echo Room server: ws://127.0.0.1:5077/ws
echo Health check: http://127.0.0.1:5077/health
echo Create a room in BiliCinema to generate a Cloudflare invite.
echo Press Ctrl+C to stop the server.
echo.
"artifacts\RoomServer-win-x64\DownKyi.RoomServer.exe"
set "SERVER_EXIT=%ERRORLEVEL%"
goto finished

:missing_runtime
echo .NET 10 SDK or a locally built room server was not found.
echo Install the .NET 10 SDK, then double-click this file again.
set "SERVER_EXIT=1"

:finished
echo.
echo Room server stopped with exit code %SERVER_EXIT%.
pause
popd
exit /b %SERVER_EXIT%
