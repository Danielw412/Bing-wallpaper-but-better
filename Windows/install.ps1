[CmdletBinding()]
param(
    [ValidatePattern('^([01][0-9]|2[0-3]):[0-5][0-9]$')] [string] $Time = '08:00',
    [switch] $SkipInitialUpdate
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')

& (Join-Path $PSScriptRoot 'build.ps1')
$root = Get-WallpaperRoot
$bin = Join-Path $root 'bin'
$config = Join-Path $root 'config.json'
Assert-WallpaperChildPath -Path $bin -Root $root
Assert-WallpaperChildPath -Path $config -Root $root
New-Item -ItemType Directory -Force -Path $bin | Out-Null

$scheduler = Get-WallpaperScheduler
$folder = $scheduler.GetFolder('\')
$taskName = Get-WallpaperTaskName
$previousTask = Get-WallpaperTask -Folder $folder -Name $taskName
if ($null -ne $previousTask) {
    $previousTask.Enabled = $false
    Stop-WallpaperTask -Task $previousTask
}
try {
    foreach ($name in @('daily-wallpaper.exe', 'daily-wallpaper.exe.config', 'daily-wallpaper-background.exe', 'daily-wallpaper-background.exe.config')) {
        $destination = Join-Path $bin $name
        Assert-WallpaperChildPath -Path $destination -Root $root
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('build\' + $name)) -Destination $destination -Force
    }
    if (-not (Test-Path -LiteralPath $config)) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dist\config.json') -Destination $config
    }
    $definition = New-WallpaperTaskDefinition -Scheduler $scheduler -Executable (Join-Path $bin 'daily-wallpaper-background.exe') -Time $Time
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $folder.RegisterTaskDefinition($taskName, $definition, 6, $userSid, $null, 3, $null) | Out-Null
}
catch {
    if ($null -ne $previousTask) { $previousTask.Enabled = $true }
    throw
}
Write-Host "Installed for the current user. Runs daily at $Time and 30 seconds after login."
Write-Host "Task: $taskName"
Write-Host "Config: $config"
Write-Host "Commands: & '$bin\daily-wallpaper.exe' info (or update, previous, next)"
if (-not $SkipInitialUpdate) {
    & (Join-Path $bin 'daily-wallpaper.exe') update
    if ($LASTEXITCODE -ne 0) { Write-Warning "Installed, but the initial update failed (exit $LASTEXITCODE). The scheduled task will retry." }
}
