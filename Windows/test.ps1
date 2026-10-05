[CmdletBinding()]
param([switch] $Online, [switch] $Scheduler, [switch] $Installer)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')
& (Join-Path $PSScriptRoot 'build.ps1') -Tests
$testArgs = @()
if ($Online) { $testArgs += '--online' }
& (Join-Path $PSScriptRoot 'build\tests.exe') @testArgs
if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)." }

# Parse every script, including branches not used by this test run.
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -Recurse | ForEach-Object {
    $tokens = $null
    $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref] $tokens, [ref] $errors) | Out-Null
    if ($errors.Count -gt 0) { throw ($errors | Out-String) }
}
if ($Installer) { & (Join-Path $PSScriptRoot 'tests\InstallSmoke.ps1') }
Write-Host 'PASS PowerShell syntax'

if ($Scheduler) {
    $service = Get-WallpaperScheduler
    $folder = $service.GetFolder('\')
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $executable = Join-Path $PSScriptRoot 'build\daily-wallpaper-background.exe'
    $definition = New-WallpaperTaskDefinition -Scheduler $service -Executable $executable -Arguments '--version'
    $name = 'DailyWallpaper-Test-' + [Guid]::NewGuid().ToString('N')
    # Validation does not create a task or run the updater.
    $folder.RegisterTaskDefinition($name, $definition, 1, $sid, $null, 3, $null) | Out-Null
    Write-Host 'PASS task definition validation (daily, login, interactive user, battery, retries)'
    $definition.Triggers.Clear() # The temporary task can run only when requested below.
    $task = $null
    try {
        $task = $folder.RegisterTaskDefinition($name, $definition, 2, $sid, $null, 3, $null)
        $started = [DateTime]::Now.AddSeconds(-1)
        $task.Run($null) | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 200
            $task = $folder.GetTask($name)
        } while (($task.State -eq 2 -or $task.State -eq 4 -or $task.LastRunTime -lt $started) -and [DateTime]::UtcNow -lt $deadline)
        if ($task.State -eq 2 -or $task.State -eq 4 -or $task.LastRunTime -lt $started -or $task.LastTaskResult -ne 0) {
            throw "Scheduled executable failed or timed out (result $($task.LastTaskResult))."
        }
        Write-Host 'PASS real Task Scheduler launch of the windowless executable'
    }
    finally {
        if ($null -ne $task) { Stop-WallpaperTask -Task $task; $folder.DeleteTask($name, 0) }
    }
}
