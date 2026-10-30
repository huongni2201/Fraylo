# Đóng gói TiToAI: publish app + launcher, tạo zip cập nhật + manifest.json, và bộ cài Setup.exe (nếu có Inno Setup).
# Dùng: scripts\package-release.ps1 [-Version 1.0.1] [-SkipInstaller]
param(
    [string]$Version = "",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
}
Write-Host "== TiToAI $Version ==" -ForegroundColor Cyan

$dist = Join-Path $root "dist"
$appDir = Join-Path $dist "app"
$launcherDir = Join-Path $dist "launcher"
$releases = Join-Path $dist "releases"
Get-Process -Name "TiToAI.Desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $releases | Out-Null

Write-Host "[1/4] Publish app (kèm build giao diện web)..." -ForegroundColor Yellow
dotnet publish "$root\src\TiToAI.Desktop\TiToAI.Desktop.csproj" -c Release -r win-x64 --self-contained -p:Version=$Version -o $appDir
if ($LASTEXITCODE) { throw "publish app thất bại" }

Write-Host "[2/4] Publish launcher/updater..." -ForegroundColor Yellow
dotnet publish "$root\src\TiToAI.Launcher\TiToAI.Launcher.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o $launcherDir
if ($LASTEXITCODE) { throw "publish launcher thất bại" }

# Không bao giờ để dữ liệu người dùng lọt vào gói phát hành.
Get-ChildItem $appDir -Recurse -Include *.db, *.db-wal, *.db-shm, *.sqlite* | Remove-Item -Force

Write-Host "[3/4] Tạo zip cập nhật + manifest.json..." -ForegroundColor Yellow
$zipName = "TiToAI-$Version-win-x64.zip"
$zip = Join-Path $releases $zipName
Compress-Archive -Path "$appDir\*" -DestinationPath $zip
@{
    app_id  = "titoai"
    version = $Version
    asset   = @{ name = $zipName; sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash; size_bytes = (Get-Item $zip).Length }
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $releases "manifest.json") -Encoding UTF8
Copy-Item "$launcherDir\TiToAILauncher.exe" $releases

if ($SkipInstaller) { return }

Write-Host "[4/4] Biên dịch bộ cài Inno Setup..." -ForegroundColor Yellow
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Chưa có Inno Setup. Cài bằng: winget install JRSoftware.InnoSetup (hoặc dùng -SkipInstaller)" }
& $iscc "/DMyAppVersion=$Version" "$root\scripts\installer.iss"
if ($LASTEXITCODE) { throw "ISCC thất bại" }

Write-Host "Xong. File phát hành nằm ở $releases" -ForegroundColor Green
Write-Host "Gửi khách: TiToAI-Setup-v$Version.exe. Phát hành cập nhật: upload zip + manifest.json lên GitHub Release tag v$Version."
