param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Source = Join-Path $Root "src\WorkspaceLauncher.cs"
$Icon = Join-Path $Root "assets\WorkspaceLauncher.ico"
$Dist = Join-Path $Root "dist"
$Output = Join-Path $Dist "WorkspaceLauncher.exe"
$Csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $Csc)) {
    $Csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path -LiteralPath $Csc)) {
    throw "Could not find csc.exe from .NET Framework 4.x."
}

New-Item -ItemType Directory -Force -Path $Dist | Out-Null

& $Csc `
    /nologo `
    /target:winexe `
    /out:$Output `
    /win32icon:$Icon `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Web.dll `
    $Source

Write-Host "Built $Output"
