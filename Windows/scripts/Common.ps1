# Shared by the installer, uninstaller and scheduling tests.
Set-StrictMode -Version Latest

function Get-WallpaperRoot {
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrEmpty($local)) { throw 'Cannot locate LocalAppData.' }
    return [IO.Path]::GetFullPath((Join-Path $local 'daily-wallpaper'))
}

function Get-WallpaperTaskName {
    return 'DailyWallpaper-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}

function Get-WallpaperScheduler {
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    return $scheduler
}

function Get-WallpaperErrorCode {
    param([Exception] $Exception)
    while ($null -ne $Exception.InnerException) { $Exception = $Exception.InnerException }
    return ($Exception.HResult -band 0xffff)
}

function Get-WallpaperTask {
    param($Folder, [string] $Name)
    try { return $Folder.GetTask($Name) }
    catch {
        if ((Get-WallpaperErrorCode $_.Exception) -ne 2) { throw }
        return $null
    }
}

function Stop-WallpaperTask {
    param($Task)
    try { $Task.Stop(0) }
    catch {
        # SCHED_E_TASK_NOT_RUNNING is normal for a program that exits quickly.
        if ((Get-WallpaperErrorCode $_.Exception) -ne 0x130b) { throw }
    }
}

function New-WallpaperTaskDefinition {
    param(
        [Parameter(Mandatory = $true)] $Scheduler,
        [Parameter(Mandatory = $true)] [string] $Executable,
        [ValidatePattern('^([01][0-9]|2[0-3]):[0-5][0-9]$')] [string] $Time = '08:00',
        [string] $Arguments = 'update --quiet'
    )
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $definition = $Scheduler.NewTask(0)
    $definition.RegistrationInfo.Description = 'Fetch and apply the Bing wallpaper daily, then exit.'
    $definition.Principal.UserId = $userSid
    $definition.Principal.LogonType = 3 # TASK_LOGON_INTERACTIVE_TOKEN; no password or elevation.
    $definition.Principal.RunLevel = 0
    $settings = $definition.Settings
    $settings.Enabled = $true
    $settings.StartWhenAvailable = $true
    $settings.DisallowStartIfOnBatteries = $false
    $settings.StopIfGoingOnBatteries = $false
    $settings.RunOnlyIfIdle = $false
    $settings.WakeToRun = $false
    $settings.MultipleInstances = 2 # Ignore a new scheduled run if one is still running.
    $settings.ExecutionTimeLimit = 'PT15M'
    # Allow over 41 days of hourly retries per failed run. Daily and login
    # triggers continue independently; a successful run stops failure retries.
    $settings.RestartCount = 999
    $settings.RestartInterval = 'PT1H'

    $daily = $definition.Triggers.Create(2) # TASK_TRIGGER_DAILY
    $daily.StartBoundary = [DateTime]::Today.ToString('yyyy-MM-dd') + 'T' + $Time + ':00'
    $daily.DaysInterval = 1
    $daily.Enabled = $true
    $login = $definition.Triggers.Create(9) # TASK_TRIGGER_LOGON
    $login.UserId = $userSid
    $login.Delay = 'PT30S'
    $login.Enabled = $true

    $action = $definition.Actions.Create(0) # TASK_ACTION_EXEC
    $action.Path = [IO.Path]::GetFullPath($Executable)
    $action.Arguments = $Arguments
    $action.WorkingDirectory = [IO.Path]::GetDirectoryName($action.Path)
    return $definition
}

function Assert-WallpaperChildPath {
    param([string] $Path, [string] $Root)
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside ${resolvedRoot}: $resolvedPath"
    }
    # A junction/symlink could make a lexically safe path point elsewhere.
    $cursor = $resolvedPath
    while ($cursor.Length -ge $resolvedRoot.Length) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to modify a linked path: $cursor"
            }
        }
        if ($cursor -eq $resolvedRoot) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Remove-WallpaperTree {
    param([string] $Path, [string] $Root)
    Assert-WallpaperChildPath -Path $Path -Root $Root
    if (Test-Path -LiteralPath $Path) {
        # Check descendants before recursing, too; do not traverse junctions.
        $links = @(Get-ChildItem -LiteralPath $Path -Force -Recurse | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        })
        if ($links.Count -gt 0) { throw "Refusing to remove a tree containing linked paths: $Path" }
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}
