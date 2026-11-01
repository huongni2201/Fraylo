param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$distIndex = Join-Path $repoRoot 'src/Fraylo.Web/dist/index.html'
$index = Join-Path $OutputDirectory 'wwwroot/index.html'
if (!(Test-Path -LiteralPath $index)) { throw "Không tìm thấy $index" }
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $outputHash = [Convert]::ToBase64String($sha256.ComputeHash([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $index).Path)))
    $distHash = [Convert]::ToBase64String($sha256.ComputeHash([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $distIndex).Path)))
} finally {
    $sha256.Dispose()
}
if ($outputHash -ne $distHash) {
    throw 'Giao diện trong thư mục đầu ra không khớp với bản build hiện tại.'
}
$html = Get-Content -Raw -LiteralPath $index
$references = [regex]::Matches($html, '(?:src|href)="\./([^"?#]+)"')
if ($references.Count -eq 0) { throw 'Không tìm thấy các tệp giao diện được tham chiếu.' }
foreach ($reference in $references) {
    $path = Join-Path (Split-Path $index) $reference.Groups[1].Value
    if (!(Test-Path -LiteralPath $path)) { throw "Không tìm thấy tệp giao diện $path" }
}
Write-Host 'PASS: giao diện hiện tại và các tệp được tham chiếu đã được đóng gói.'
