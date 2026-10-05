[CmdletBinding()]
param([switch] $Purge)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')

$root = Get-WallpaperRoot
$bin = Join-Path $root 'bin'
Assert-WallpaperChildPath -Path $bin -Root $root
$scheduler = Get-WallpaperScheduler
$folder = $scheduler.GetFolder('\')
$taskName = Get-WallpaperTaskName
$task = Get-WallpaperTask -Folder $folder -Name $taskName
if ($null -ne $task) {
    $task.Enabled = $false
    Stop-WallpaperTask -Task $task
    $folder.DeleteTask($taskName, 0)
}
if ($Purge) {
    $data = Join-Path $root 'data'
    Assert-WallpaperChildPath -Path $data -Root $root
    if (Test-Path -LiteralPath (Join-Path $data 'images')) {
        $executable = Join-Path $bin 'daily-wallpaper.exe'
        if (-not (Test-Path -LiteralPath $executable)) {
            & (Join-Path $PSScriptRoot 'build.ps1')
            $executable = Join-Path $PSScriptRoot 'build\daily-wallpaper.exe'
        }
        # Inspect the live per-monitor paths, so an independently chosen
        # wallpaper is preserved even if metadata.current is out of date.
        & $executable detach
        if ($LASTEXITCODE -ne 0) { throw 'Could not retain active wallpapers. Config and cache retained.' }
    }
    Remove-WallpaperTree -Path $data -Root $root
    $config = Join-Path $root 'config.json'
    Assert-WallpaperChildPath -Path $config -Root $root
    if (Test-Path -LiteralPath $config) { Remove-Item -LiteralPath $config -Force }
    Write-Host 'Removed the program, task, config and cache.'
}
else { Write-Host "Removed the program and task. Kept config and cache in $root" }
Remove-WallpaperTree -Path $bin -Root $root
