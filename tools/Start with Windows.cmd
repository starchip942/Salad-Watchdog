@echo off
rem Makes GPU Watchdog start by itself in the tray when you sign in to Windows. Click Yes on the permission prompt.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\autostart.ps1"
