@echo off
title PitWatch Dev Launcher
cd /d "%~dp0"

REM ============================================================
REM  Opens the developer launcher.
REM  Double-click this file - no terminal commands needed.
REM
REM  Builds the WHOLE solution, not just the launcher: launching
REM  a stale PitWatch build and wondering why your changes aren't
REM  there is a very easy hour to lose.
REM ============================================================

echo Building the solution...
echo.

dotnet build PitWatch.sln -c Debug -v quiet --nologo
if errorlevel 1 (
  echo.
  echo Build failed - see the errors above.
  echo.
  pause
  exit /b 1
)

echo Build OK. Opening the launcher...
start "" dotnet run --project PitWatch.DevLauncher -c Debug --no-build
exit /b 0
