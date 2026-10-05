# Builds using the .NET Framework compiler included with Windows.
[CmdletBinding()]
param([switch] $Tests)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found. Install .NET Framework 4.8.' }
$output = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$flags = @('/nologo', '/optimize+', '/warn:4', '/warnaserror+', '/platform:anycpu',
    ('/reference:' + (Join-Path $framework 'System.Net.Http.dll')),
    ('/reference:' + (Join-Path $framework 'System.Web.Extensions.dll')))

foreach ($name in @('daily-wallpaper', 'daily-wallpaper-background')) {
    $target = if ($name -eq 'daily-wallpaper') { '/target:exe' } else { '/target:winexe' }
    & $compiler @flags $target ('/out:' + (Join-Path $output ($name + '.exe'))) @sources
    if ($LASTEXITCODE -ne 0) { throw "Building $name failed (exit $LASTEXITCODE)." }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dist\daily-wallpaper.exe.config') -Destination (Join-Path $output ($name + '.exe.config'))
}
if ($Tests) {
    $testSource = Join-Path $PSScriptRoot 'tests\Tests.cs'
    & $compiler @flags '/target:exe' '/main:DailyWallpaper.Tests' ('/out:' + (Join-Path $output 'tests.exe')) @sources $testSource
    if ($LASTEXITCODE -ne 0) { throw "Building tests failed (exit $LASTEXITCODE)." }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dist\daily-wallpaper.exe.config') -Destination (Join-Path $output 'tests.exe.config')
}
Write-Host "Built executables in $output"
