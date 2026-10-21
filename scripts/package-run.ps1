# Wrapper used by package.cmd: run package.ps1, keep output live, also write scripts\package.log,
# and return a real exit code (1 on failure) so double-click failures are detectable.
param(
    [string]$Version = "1.0.0",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Continue"
$log = Join-Path $PSScriptRoot "package.log"
$inner = Join-Path $PSScriptRoot "package.ps1"
$code = 0

try { Start-Transcript -Path $log -Force | Out-Null } catch { }

try {
    & $inner -Version $Version -OutDir $OutDir
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
