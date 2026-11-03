# Đóng gói Fraylo: publish app + launcher, tạo zip cập nhật + manifest.json, và bộ cài Setup.exe (nếu có Inno Setup).
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
Write-Host "== Fraylo $Version ==" -ForegroundColor Cyan

# Nạp biến từ .env (nếu có), không ghi đè biến đã đặt sẵn (CI truyền qua env).
$envFile = Join-Path $root ".env"
if (Test-Path $envFile) {
    foreach ($line in Get-Content $envFile) {
        if ($line -match '^\s*([A-Z0-9_]+)=(.*)$' -and -not [Environment]::GetEnvironmentVariable($Matches[1])) {
            [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2].Trim())
        }
    }
}
if (-not $env:UPDATE_BASE_URL) { throw "Thiếu UPDATE_BASE_URL (gốc URL R2, xem .env.example)." }

$dist = Join-Path $root "dist"
$appDir = Join-Path $dist "app"
$launcherDir = Join-Path $dist "launcher"
$releases = Join-Path $dist "releases"
Get-Process -Name "Fraylo" -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $releases | Out-Null

Write-Host "[1/4] Publish app (kèm build giao diện web)..." -ForegroundColor Yellow
# --force: build Debug/IDE có thể đã ghi đè project.assets.json bằng bản thiếu win-x64.
dotnet restore "$root\src\Fraylo.Desktop\Fraylo.Desktop.csproj" -r win-x64 --force
if ($LASTEXITCODE) { throw "restore app thất bại" }
dotnet publish "$root\src\Fraylo.Desktop\Fraylo.Desktop.csproj" -c Release --no-restore -r win-x64 --self-contained -p:Version=$Version -o $appDir
if ($LASTEXITCODE) { throw "publish app thất bại" }

Write-Host "[2/4] Publish launcher/updater..." -ForegroundColor Yellow
dotnet publish "$root\src\Fraylo.Launcher\Fraylo.Launcher.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o $launcherDir
if ($LASTEXITCODE) { throw "publish launcher thất bại" }

# Không bao giờ để dữ liệu người dùng lọt vào gói phát hành.
Get-ChildItem $appDir -Recurse -File -Include *.db, *.db-wal, *.db-shm, *.sqlite, *.sqlite-wal, *.sqlite-shm, *.sqlite3, *.sqlite3-wal, *.sqlite3-shm | Remove-Item -Force

# Bộ lọc dữ liệu phải giữ nguyên thư viện SQLite dùng khi khởi động.
foreach ($requiredFile in @("Microsoft.EntityFrameworkCore.Sqlite.dll", "Microsoft.Data.Sqlite.dll")) {
    if (-not (Test-Path (Join-Path $appDir $requiredFile) -PathType Leaf)) {
        throw "Gói phát hành thiếu thư viện bắt buộc: $requiredFile"
    }
}

Write-Host "[3/4] Tạo zip cập nhật + manifest.json..." -ForegroundColor Yellow
$zipName = "Fraylo-$Version-win-x64.zip"
$zip = Join-Path $releases $zipName
Compress-Archive -Path "$appDir\*" -DestinationPath $zip

# Ký "{version}|{sha256}" bằng khóa riêng ECDSA P-256; app chỉ nhận cập nhật có chữ ký khớp khóa công khai nhúng trong AppUpdateService.
# Khóa nằm ngoài repo; mất khóa thì mọi bản đã cài không còn tự cập nhật được nữa (phải cài lại bằng Setup.exe), nên hãy sao lưu.
$keyFile = Join-Path $env:USERPROFILE "Fraylo-keys\release-signing.key"
if (-not (Test-Path $keyFile)) { throw "Thiếu khóa ký $keyFile (xem AppUpdateService.PublicKey)." }
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
$ec = [Security.Cryptography.ECDsa]::Create()
$ec.ImportPkcs8PrivateKey([Convert]::FromBase64String((Get-Content $keyFile -Raw).Trim()), [ref]$null)
$signature = [Convert]::ToBase64String($ec.SignData([Text.Encoding]::UTF8.GetBytes("$Version|$sha"), [Security.Cryptography.HashAlgorithmName]::SHA256))
@{
    app_id    = "Fraylo"
    version   = $Version
    asset     = @{ name = $zipName; sha256 = $sha; size_bytes = (Get-Item $zip).Length }
    signature = $signature
    notes     = @($env:RELEASE_NOTES -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $releases "manifest.json") -Encoding UTF8
Copy-Item "$launcherDir\FrayloLauncher.exe" $releases

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
Write-Host "Gửi khách: Fraylo-Setup-v$Version.exe. Phát hành cập nhật: upload zip + Setup.exe rồi manifest.json (cuối cùng) lên bucket R2 (workflow release.yml làm tự động)."
