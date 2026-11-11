@echo off
setlocal EnableDelayedExpansion
title GPU Watchdog tools
cd /d "%~dp0"
set "ROOT=%~dp0.."
set "TRASH=%~dp0..\_to_delete"

:menu
cls
echo.
echo   GPU Watchdog tools
echo   ------------------
echo   1  Upload to GitHub (updates the website)
echo   2  Start GPU Watchdog with Windows
echo   3  Stop starting with Windows
echo   4  Hardware check (report opens in Notepad)
echo   5  Find out why GPU Watchdog doesn't start
echo   6  Removed files: delete them for good, or restore one
echo   0  Close
echo.
choice /c 1234560 /n /m "  Pick a number: "
if errorlevel 7 exit /b 0
if errorlevel 6 goto trash
if errorlevel 5 goto diagnose
if errorlevel 4 goto hwcheck
if errorlevel 3 goto autooff
if errorlevel 2 goto autoon
if errorlevel 1 goto upload
goto menu

rem ---------------------------------------------------------------- 1 upload
:upload
echo.
echo  Uploading GPU Watchdog to github.com/starchip942/Salad-Watchdog ...
where git >nul 2>nul
if errorlevel 1 (
  echo  Git isn't installed on this PC. It's needed once to upload.
  choice /m " Install Git now (free, from Microsoft's winget)"
  if errorlevel 2 goto back
  winget install --id Git.Git -e --source winget --accept-package-agreements --accept-source-agreements
  set "PATH=!PATH!;C:\Program Files\Git\cmd"
  where git >nul 2>nul || (echo  Git still not found. Close this window and run it again. & goto back)
)
if not exist "%~dp0salad-watchdog.bundle" (echo  salad-watchdog.bundle is missing in the tools folder. & goto back)
set "WORK=%~dp0.upload-temp"
if exist "!WORK!" rmdir /s /q "!WORK!"
git init -q "!WORK!" || goto upfail
git -C "!WORK!" fetch -q "%~dp0salad-watchdog.bundle" main:main || goto upfail
echo  If a GitHub sign-in window opens, sign in there (only the first time).
git -C "!WORK!" push https://github.com/starchip942/Salad-Watchdog.git main:main || goto upfail
rmdir /s /q "!WORK!"
echo.
echo  Done! The website rebuilds itself in about 2 minutes:
echo  https://starchip942.github.io/Salad-Watchdog/
goto back
:upfail
echo.
echo  Upload failed. Take a screenshot of this window and send it to Claude.
if exist "!WORK!" rmdir /s /q "!WORK!"
goto back

rem ---------------------------------------------------------------- 2-5
:autoon
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\autostart.ps1"
goto back
:autooff
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\autostart.ps1" -Remove
goto back
:hwcheck
start "" "%ROOT%\agent\GPU-Watchdog-Agent.exe" --discover
echo  The hardware check is running; its report opens in Notepad when it's done.
goto back
:diagnose
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\diagnose.ps1"
goto back

rem ---------------------------------------------------------------- 6 removed files
:trash
cls
echo.
echo   Removed files (in the _to_delete folder)
echo   ----------------------------------------
set n=0
if exist "%TRASH%" for /f "delims=" %%F in ('dir /b /a-d "%TRASH%" 2^>nul') do (
  set /a n+=1
  set "f!n!=%%F"
  echo   !n!  %%F
)
if !n!==0 (echo   Nothing here. & goto back)
echo.
echo   D  Delete ALL of these for good
echo   R  Restore one
echo   B  Back
echo.
choice /c DRB /n /m "  D, R or B: "
if errorlevel 3 goto menu
if errorlevel 2 goto restore
echo.
choice /m "  Delete all !n! files permanently? This can't be undone"
if errorlevel 2 goto trash
del /q "%TRASH%\*" 2>nul
echo  Deleted.
goto back

:restore
echo.
set "pick="
set /p "pick=  Number of the file to restore (1-!n!): "
if not defined pick goto trash
set "name=!f%pick%!"
if not defined name (echo  There's no number %pick%. & goto back)
rem old watchdog versions go back into agent\, everything else into tools\
set "dest=%~dp0"
if /i "!name:~-4!"==".exe" set "dest=%ROOT%\agent\"
if exist "!dest!!name!" (echo  A file called !name! is already there, so nothing was moved. & goto back)
move "%TRASH%\!name!" "!dest!" >nul && echo  Restored !name! to !dest!
goto back

:back
echo.
pause
goto menu
