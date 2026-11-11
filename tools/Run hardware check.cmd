@echo off
rem Phase 1: lists every sensor this PC exposes and how Salad runs. Read-only, changes nothing.
rem The report opens in Notepad and is saved as logs\discovery-report.txt
start "" "%~dp0..\agent\GPU-Watchdog-Agent.exe" --discover
