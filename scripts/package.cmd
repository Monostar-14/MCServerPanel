@echo off
rem ===========================================================================
rem  Double-click to package (Windows).
rem    uncompressed : scripts\dist\win-x64\   and  scripts\dist\linux-x64\
rem    archives     : scripts\dist\MCServerPanel-<ver>-win-x64.zip
rem                   scripts\dist\MCServerPanel-<ver>-linux-x64.tar.gz
rem    checksums    : scripts\dist\SHA256SUMS.txt
rem  Output is shown live; the window closes when done and nothing else happens.
rem  Full log: scripts\package.log (overwritten each run).
rem  Optional version:  scripts\package.cmd 1.0.1
rem  NOTE: keep this file ASCII-only with CRLF endings, otherwise cmd.exe
rem        garbles it on Chinese Windows (GBK console codepage).
rem ===========================================================================
setlocal
cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package-run.ps1" %*

endlocal
exit /b %ERRORLEVEL%
