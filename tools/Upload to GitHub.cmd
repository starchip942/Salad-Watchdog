@echo off
setlocal
title Upload GPU Watchdog to GitHub
cd /d "%~dp0"
echo.
echo  Uploading GPU Watchdog to github.com/starchip942/Salad-Watchdog ...
echo.

where git >nul 2>nul
if errorlevel 1 (
  echo  Git isn't installed on this PC. It's needed once to upload.
  choice /m " Install Git now (free, from Microsoft's winget)"
  if errorlevel 2 goto :end
  winget install --id Git.Git -e --source winget --accept-package-agreements --accept-source-agreements
  set "PATH=%PATH%;C:\Program Files\Git\cmd"
  where git >nul 2>nul || (echo  Git still not found. Close this window and run it again. & goto :end)
)

if not exist "salad-watchdog.bundle" (echo  salad-watchdog.bundle is missing in the tools folder. & goto :end)

set "WORK=%~dp0.upload-temp"
if exist "%WORK%" rmdir /s /q "%WORK%"
git init -q "%WORK%" || goto :fail
git -C "%WORK%" fetch -q "%~dp0salad-watchdog.bundle" main:main || goto :fail

echo  If a GitHub sign-in window opens, sign in there (only the first time).
echo.
git -C "%WORK%" push https://github.com/starchip942/Salad-Watchdog.git main:main
if errorlevel 1 goto :fail

rmdir /s /q "%WORK%"
echo.
echo  Done! The website rebuilds itself on GitHub in about 2 minutes:
echo  https://starchip942.github.io/Salad-Watchdog/
goto :end

:fail
echo.
echo  Upload failed. Take a screenshot of this window and send it to Claude.
if exist "%WORK%" rmdir /s /q "%WORK%"

:end
echo.
pause
