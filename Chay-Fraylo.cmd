@echo off
setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -Command "if (Get-Process -Name 'Fraylo' -ErrorAction SilentlyContinue) { exit 1 }"
if errorlevel 1 (
    echo Hay dong Fraylo dang chay truoc khi mo ban moi.
    pause
    exit /b 1
)

echo Dang build Fraylo va giao dien moi nhat...
dotnet build "src\Fraylo.Desktop\Fraylo.Desktop.csproj" -m:1
if errorlevel 1 (
    echo Build that bai. Fraylo chua duoc khoi chay.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "tests\Fraylo.Desktop.SmokeChecks\VerifyWebUiPackaging.ps1" -OutputDirectory "src\Fraylo.Desktop\bin\Debug\net10.0-windows"
if errorlevel 1 (
    echo Kiem tra giao dien that bai. Fraylo chua duoc khoi chay.
    pause
    exit /b 1
)

start "" "src\Fraylo.Desktop\bin\Debug\net10.0-windows\Fraylo.exe"
exit /b 0
