@echo off
rem Bovie Sim installer: turns a downloaded ZIP folder into a git clone (or clones a fresh copy).
rem It runs from the parent folder, on one parsed line, so this folder can be renamed while it runs.
pushd "%~dp0.." & (powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %* & echo. & pause & popd)
