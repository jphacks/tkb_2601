@echo off
rem Robomori core: open the screen again. Makes a new one-time password and signs in with it in the browser.
rem Start the core first with core\start.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0host\otp.ps1"
if errorlevel 1 pause
