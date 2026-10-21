# Wrapper used by package.cmd: run package.ps1, keep output live, also write scripts\package.log,
# and return a real exit code (1 on failure) so double-click failures are detectable.
# IMPORTANT: only pass -Version / -OutDir when the caller actually supplied them, otherwise
# package.ps1 must fall back to Directory.Build.props <Version>.
param(
    [string]$Version = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Continue"
$log = Join-Path $PSScriptRoot "package.log"
$inner = Join-Path $PSScriptRoot "package.ps1"
$code = 0

$callArgs = @{}
if (-not [string]::IsNullOrWhiteSpace($Version)) { $callArgs["Version"] = $Version }
if (-not [string]::IsNullOrWhiteSpace($OutDir))  { $callArgs["OutDir"]  = $OutDir }

try { Start-Transcript -Path $log -Force | Out-Null } catch { }

try {
    if ($callArgs.Count -gt 0) { & $inner @callArgs } else { & $inner }
    if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { $code = $LASTEXITCODE }
} catch {
    Write-Host ""
    Write-Host ("[PACKAGE FAILED] " + $_.Exception.Message)
    $code = 1
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}

if ($code -ne 0) {
    Write-Host ""
    Write-Host ("Packaging failed (exit " + $code + "). See " + $log)
}
exit $code
