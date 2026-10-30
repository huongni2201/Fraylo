@echo off
chcp 65001 > nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\package-release.ps1" %*
if errorlevel 1 (
    echo.
    echo [LOI] Dong goi that bai.
)
pause
