@echo off
setlocal

REM ============================================================
REM  Builds PitWatch and packages it with Velopack.
REM  Produces an installer AND the update files for a release.
REM ============================================================

if "%~1"=="" (
  echo Usage: build-release.bat VERSION
  echo   e.g. build-release.bat 1.0.0
  exit /b 1
)
set VERSION=%~1

echo.
echo === Checking for the Velopack CLI ===
where vpk >nul 2>nul
if errorlevel 1 (
  echo Installing the Velopack CLI ^(one time only^)...
  dotnet tool install -g vpk
  if errorlevel 1 (
    echo Failed to install vpk. Install it manually with: dotnet tool install -g vpk
    exit /b 1
  )
)

echo.
echo === Publishing %VERSION% ===
dotnet publish PitWatch.Gui -c Release -r win-x64 --self-contained true -o publish
if errorlevel 1 (
  echo Publish failed.
  exit /b 1
)

echo.
echo === Checking release notes ===
if not exist RELEASE_NOTES.md (
  echo WARNING: RELEASE_NOTES.md not found.
  echo Users will see "no release notes were included" when they check what's new.
  echo Create RELEASE_NOTES.md describing this version, then run again.
  echo.
  set NOTES_ARG=
) else (
  echo Using RELEASE_NOTES.md
  set NOTES_ARG=--releaseNotes RELEASE_NOTES.md
)

echo.
echo === Safety check: dev launcher must not be in the release ===
if exist publish\PitWatch.DevLauncher.exe (
  echo ERROR: PitWatch.DevLauncher.exe ended up in the publish folder.
  echo That is a developer-only tool and must not ship. Aborting.
  exit /b 1
)
echo OK - dev launcher not present.

echo.
echo === Packaging with Velopack ===
vpk pack --packId PitWatch --packVersion %VERSION% --packDir publish --mainExe PitWatch.Gui.exe --packTitle "PitWatch" --icon PitWatch.Gui\Assets\PitWatch.ico %NOTES_ARG%
if errorlevel 1 (
  echo Packaging failed.
  exit /b 1
)

echo.
echo ============================================================
echo  Done. Everything you need is in the "Releases" folder.
echo.
echo  Upload the ENTIRE contents of that folder to a GitHub
echo  Release tagged v%VERSION%.
echo.
echo  - PitWatch-win-Setup.exe  is what new users download
echo  - the .nupkg and RELEASES files are what existing
echo    installs use to update themselves
echo.
echo  If you only upload the Setup.exe, auto-update will not
echo  work - the other files are what existing users check.
echo ============================================================
pause
