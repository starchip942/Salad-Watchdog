@echo off
rem Collects why GPU Watchdog doesn't start. Read-only. Result: logs\diagnose.txt (opens in Notepad).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\diagnose.ps1"
