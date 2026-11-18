@echo off
rem Undo "Start with Windows". Click Yes on the permission prompt.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\autostart.ps1" -Remove
