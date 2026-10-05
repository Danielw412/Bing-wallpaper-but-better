# Run the real install/upgrade/uninstall scripts against an isolated copy.
# Only its path/task-name helpers are substituted, and its task is disabled.
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $windowsRoot 'scripts\Common.ps1')
$scratch = Join-Path $windowsRoot ('build\installer-' + [Guid]::NewGuid().ToString('N') + " spaces & 'quotes'")
$installRoot = Join-Path $scratch 'installed'
$taskName = 'DailyWallpaper-InstallerTest-' + [Guid]::NewGuid().ToString('N')
Assert-WallpaperChildPath -Path $scratch -Root $windowsRoot
New-Item -ItemType Directory -Path $scratch | Out-Null
$service = Get-WallpaperScheduler
$folder = $service.GetFolder('\')
try {
    foreach ($item in @('src', 'dist', 'scripts', 'build.ps1', 'install.ps1', 'uninstall.ps1')) {
        Copy-Item -LiteralPath (Join-Path $windowsRoot $item) -Destination $scratch -Recurse
    }
    $common = Join-Path $scratch 'scripts\Common.ps1'
    $text = [IO.File]::ReadAllText($common)
    $text = $text.Replace("return [IO.Path]::GetFullPath((Join-Path `$local 'daily-wallpaper'))", "return '" + $installRoot.Replace("'", "''") + "'")
    $text = $text.Replace("return 'DailyWallpaper-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value", "return '$taskName'")
    $text = $text.Replace('$settings.Enabled = $true', '$settings.Enabled = $false')
    [IO.File]::WriteAllText($common, $text)
    & (Join-Path $scratch 'install.ps1') -SkipInitialUpdate
    if (-not (Test-Path -LiteralPath (Join-Path $installRoot 'bin\daily-wallpaper.exe'))) { throw 'Installer did not copy the binary.' }
    if ($folder.GetTask($taskName).Enabled) { throw 'Test task must remain disabled.' }
    $config = Join-Path $installRoot 'config.json'
    $custom = '{"market":"en-GB","cache":{"keep":3},"wallpaper":{"mode":"fit"}}'
    [IO.File]::WriteAllText($config, $custom)
    $data = Join-Path $installRoot 'data'
    New-Item -ItemType Directory -Path $data | Out-Null
    [IO.File]::WriteAllText((Join-Path $data 'keep.txt'), 'preserved user data')
    & (Join-Path $scratch 'install.ps1') -Time '10:30' -SkipInitialUpdate
    if ([IO.File]::ReadAllText($config) -ne $custom) { throw 'Upgrade replaced user config.' }
    if (-not $folder.GetTask($taskName).Definition.Triggers.Item(1).StartBoundary.EndsWith('T10:30:00')) { throw 'Upgrade did not change schedule.' }
    & (Join-Path $scratch 'uninstall.ps1')
    if ($null -ne (Get-WallpaperTask -Folder $folder -Name $taskName)) { throw 'Uninstall left the task registered.' }
    if ((Test-Path -LiteralPath (Join-Path $installRoot 'bin')) -or -not (Test-Path -LiteralPath $config) -or -not (Test-Path -LiteralPath $data)) {
        throw 'Uninstall did not preserve config/data or remove binaries.'
    }
    & (Join-Path $scratch 'install.ps1') -SkipInitialUpdate
    & (Join-Path $scratch 'uninstall.ps1') -Purge
    if ((Test-Path -LiteralPath $config) -or (Test-Path -LiteralPath $data) -or (Test-Path -LiteralPath (Join-Path $installRoot 'bin'))) {
        throw 'Purge left installed files.'
    }
    Write-Host 'PASS isolated install, upgrade, schedule change, uninstall and purge'
}
finally {
    # Reload the original helpers after the copied scripts dot-sourced theirs.
    . (Join-Path $windowsRoot 'scripts\Common.ps1')
    $task = Get-WallpaperTask -Folder $folder -Name $taskName
    if ($null -ne $task) { Stop-WallpaperTask -Task $task; $folder.DeleteTask($taskName, 0) }
    Remove-WallpaperTree -Path $scratch -Root $windowsRoot
}
