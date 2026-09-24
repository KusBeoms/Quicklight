@echo off
rem Tests, then packages Quicklight as ONE self-contained exe: dist\Quicklight.exe
rem (launcher + MCP server via "Quicklight.exe --mcp"; the .NET runtime is inside, nothing to install).
setlocal
cd /d "%~dp0"

dotnet test tests\Quicklight.Tests -c Release || exit /b 1
if exist dist rmdir /s /q dist
dotnet publish src\Quicklight -c Release -r win-x64 --self-contained true -o dist ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=embedded -p:SatelliteResourceLanguages=ko || exit /b 1
echo.
echo Packaged: %cd%\dist\Quicklight.exe
echo Install for this user with:  powershell -ExecutionPolicy Bypass -File install.ps1
