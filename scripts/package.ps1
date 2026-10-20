# 一键打包 Windows + Linux 发布包（在 Windows 上运行）
# 用法：
#   powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
#   powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1 -Version 1.0.1 -OutDir E:\build\mcsp
param(
    [string]$Version = "1.0.0",
    [string]$OutDir = "E:\build\mcsp-release"
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "MCServerPanel\MCServerPanel.csproj"
Set-Location $repo

$common = @(
    "-c", "Release",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-p:Version=$Version"
)

Write-Host "==> out dir: $OutDir" -ForegroundColor Cyan
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

foreach ($rid in @("win-x64", "linux-x64")) {
    $dest = (Join-Path $OutDir $rid).Replace('\', '/') + "/"
    Write-Host "==> publish $rid -> $dest" -ForegroundColor Cyan
    & dotnet publish $proj @common "-r" $rid "-p:PublishDir=$dest"
    if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }
    if (-not (Test-Path (Join-Path $OutDir "$rid\wwwroot\index.html"))) {
        throw "$rid output has no wwwroot, aborting"
    }
}

Write-Host "==> zip win-x64" -ForegroundColor Cyan
$zip = Join-Path $OutDir "MCServerPanel-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $OutDir "win-x64\*") -DestinationPath $zip -Force
if (-not (Test-Path $zip)) { throw "zip failed" }

Write-Host "==> tar.gz linux-x64" -ForegroundColor Cyan
$tgz = Join-Path $OutDir "MCServerPanel-$Version-linux-x64.tar.gz"
$pkgDir = Join-Path $OutDir "linux-x64"
$python = Get-Command python -ErrorAction SilentlyContinue
$execBitOk = $false
if ($python) {
    # 优先用 python 打包，能把文件权限写成 0755，Linux 解压后直接可执行
    & python (Join-Path $PSScriptRoot "make_tar.py") $pkgDir $tgz
    if ($LASTEXITCODE -ne 0) { throw "make_tar.py failed" }
    $execBitOk = $true
} else {
    $tarExe = Join-Path $env:SystemRoot "System32\tar.exe"
    if (-not (Test-Path $tarExe)) { $tarExe = "tar" }
    & $tarExe -czf $tgz -C $pkgDir .
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
}
if (-not (Test-Path $tgz)) { throw "tar.gz not created" }

Write-Host "==> sha256" -ForegroundColor Cyan
$lines = Get-ChildItem $OutDir -Filter "MCServerPanel-$Version-*" |
    Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "{0}  {1}" -f $_.Hash.ToLower(), (Split-Path $_.Path -Leaf) }
$lines | Set-Content -Path (Join-Path $OutDir "SHA256SUMS.txt") -Encoding ascii

Write-Host ""
Write-Host "done: $OutDir" -ForegroundColor Green
if ($execBitOk) {
    Write-Host "note: Linux tar.gz already has 0755 permission bits, no chmod needed." -ForegroundColor DarkGray
} else {
    Write-Host "note: python not found, tar.gz built with system tar; Linux users must run 'chmod +x MCServerPanel'." -ForegroundColor Yellow
}
Get-ChildItem $OutDir -File |
    Where-Object { $_.Name -like "MCServerPanel-$Version-*" -or $_.Name -eq "SHA256SUMS.txt" } |
    Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
