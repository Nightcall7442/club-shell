@echo off
rem Turns the kiosk on after ClubShell is installed: kiosk profile, auto-logon, agent restart (pilot support).
rem fltmc needs administrator rights and, unlike "net session", does not depend on the Server service.
fltmc >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0enable-kiosk-autologon.ps1"
pause
