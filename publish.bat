@echo off
rem Tests, then packages Quicklight as ONE self-contained exe: dist\Quicklight.exe
rem (launcher + MCP server via "Quicklight.exe --mcp" + bundled Everything; the .NET runtime is inside).
rem Usage: publish.bat [version]     e.g. publish.bat 0.2.0  -> upload dist\Quicklight.exe to a GitHub release tagged v0.2.0
setlocal
cd /d "%~dp0"
set VERSION=%1
if "%VERSION%"=="" for /f "tokens=3 delims=<>" %%v in ('findstr /c:"<Version>" Directory.Build.props') do set VERSION=%%v
if /i "%VERSION:~0,1%"=="v" set VERSION=%VERSION:~1%

dotnet test tests\Quicklight.Tests -c Release || exit /b 1
if exist dist rmdir /s /q dist
dotnet publish src\Quicklight -c Release -r win-x64 --self-contained true -o dist ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=embedded -p:SatelliteResourceLanguages=ko -p:Version=%VERSION% || exit /b 1
rem Sign with the release key and pack dist\Quicklight_v<version>.zip (Quicklight.exe + Quicklight.exe.sig).
pwsh -NoProfile -ExecutionPolicy Bypass -File tools\sign-release.ps1 -Version %VERSION% || exit /b 1
echo.
echo Packaged v%VERSION%: %cd%\dist\Quicklight.exe
echo Release: create a GitHub release tagged v%VERSION% and attach dist\Quicklight_v%VERSION%.zip
echo Install for this user with:  powershell -ExecutionPolicy Bypass -File install.ps1
