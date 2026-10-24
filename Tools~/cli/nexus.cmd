@echo off
rem Nexus CLI: wrapper for cmd and git bash, calls nexus.ps1 next to it
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0nexus.ps1" %*
exit /b %ERRORLEVEL%
