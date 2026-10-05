# daily-wallpaper for Windows

A small daily Bing wallpaper program for Windows 10 (with .NET Framework 4.8)
and Windows 11, following the Linux version's run-once design. It downloads the
image, applies it to every desktop monitor using Windows' native wallpaper API,
and exits. Task Scheduler invokes it daily and after login. There is no tray
icon, service, or resident process; it uses no memory between runs.

The two executables are about 36 KiB each. They use the .NET Framework already
included with recent Windows versions; no Rust, Visual Studio, .NET SDK, NuGet
packages, or third-party libraries are needed.

## Install

Open **PowerShell as your normal Windows user**, go to the repository folder,
and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\install.ps1
```

The script builds the executables with Windows' included C# compiler, installs
them for your account, registers the daily task, and applies today's wallpaper.
`ExecutionPolicy Bypass` applies only to this PowerShell process.

To install without immediately changing the wallpaper:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\install.ps1 -SkipInitialUpdate
```

To choose another daily time (24-hour local time):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\install.ps1 -Time 10:30
```

Run the installer again to upgrade or change the schedule. It preserves an
existing config and cached images. It does not modify your PATH.

| What | Location |
| --- | --- |
| CLI | `%LOCALAPPDATA%\daily-wallpaper\bin\daily-wallpaper.exe` |
| Windowless scheduler executable | `%LOCALAPPDATA%\daily-wallpaper\bin\daily-wallpaper-background.exe` |
| Config | `%LOCALAPPDATA%\daily-wallpaper\config.json` |
| Images and metadata | `%LOCALAPPDATA%\daily-wallpaper\data\` |
| Last scheduled run log | `%LOCALAPPDATA%\daily-wallpaper\data\last-run.log` |
| Scheduled task | `DailyWallpaper-<your Windows user SID>` |

If the initial download fails, installation remains in place. The task retries
on its schedule; you can also run `update` yourself.

## Commands

In PowerShell:

```powershell
$wallpaper = "$env:LOCALAPPDATA\daily-wallpaper\bin\daily-wallpaper.exe"
& $wallpaper update
& $wallpaper info
& $wallpaper previous
& $wallpaper next
& $wallpaper update --force
```

`previous` and `next` cycle through cached images, wrap at either end, and work
offline. `info` shows the image title, attribution, date, source URL, file and
cache position.

Like Linux, an ordinary `update` leaves your choice of an older cached wallpaper
in place if today's image has already been downloaded. `update --force` reapplies
today's image and the configured wallpaper mode without downloading it again.
Changing the resolution or market takes effect on the next update if it selects
a different image or resolution. Deleted cached images are downloaded again.

## Configuration

Edit `%LOCALAPPDATA%\daily-wallpaper\config.json`:

```json
{
  "provider": "bing",
  "market": "en-US",
  "resolution": "UHD",
  "cache": { "keep": 7 },
  "wallpaper": { "mode": "fill" }
}
```

Every key is optional. A missing file or `{}` uses these defaults. Use valid JSON
(double quotes, no comments or trailing commas). Unknown keys, incorrect types,
and invalid values are rejected.

- `market`: Bing region/language, for example `en-GB`, `de-DE`, or `ja-JP`.
- `resolution`: `UHD`, or a size such as `1920x1080`.
- `cache.keep`: positive integer; keeps the most recent images while retaining
  the image currently selected by this program.
- `wallpaper.mode`: `fill`, `fit`, `center`, `stretch`, `span`, or `tile`.
  Linux names `zoom`, `scaled`, `centered`, `stretched`, `spanned`, and `wallpaper`
  are accepted as aliases, respectively.

`fill` fills each monitor, cropping when necessary. `fit` preserves the full
image with borders if needed. `span` stretches one image across all monitors.
These are Windows desktop wallpaper settings; lock-screen images are separate.

After changing the mode, run `update --force` to apply it immediately.

## Scheduling and logs

The task runs at **08:00 local time** by default and **30 seconds after login**.
It runs in your interactive user session without storing a password. It also
allows missed scheduled runs to catch up when available. It works on battery
power and does not wake a sleeping computer. Failed scheduled runs get two
additional attempts, 15 minutes apart.

The scheduler invokes the windowless executable directly. There is no console
flash and no persistent PowerShell process. View the task in **Task Scheduler**,
or inspect it from PowerShell:

```powershell
$taskName = 'DailyWallpaper-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Get-ScheduledTaskInfo -TaskName $taskName
Start-ScheduledTask -TaskName $taskName
Get-Content "$env:LOCALAPPDATA\daily-wallpaper\data\last-run.log"
```

The log is replaced on each scheduled invocation. Manual commands print their
results in the terminal; `update --quiet` writes the same log instead.

If an organization has disabled wallpaper changes or task registration, those
Windows policies still apply. Run installation in the account whose wallpaper
you want to update.

## Reliability

Metadata and downloads use HTTPS and Windows' certificate validation. Connection
failures, timeouts, HTTP 429/5xx and interrupted bodies are retried four times
after 5, 15, 30 and 60 seconds. Each metadata request has a 30-second timeout;
each image request has a 120-second timeout. Downloads are limited to 50 MiB.

The updater checks image Content-Type, byte count, JPEG/PNG signatures and end
markers before applying an image. Temporary downloads are cleaned up on failure.
Images are closed before being renamed, and metadata is atomically replaced with
Windows' file replacement API. An exclusive file lock protects local changes,
with a 15-second wait; downloads happen outside the lock so cycling stays usable.

The native wallpaper implementation snapshots the previous position and each
monitor's image before applying changes. If application or metadata saving fails,
it attempts to restore that snapshot. Cache pruning happens only after metadata
is saved, and keeps the current image. Abandoned temporary downloads are swept
after an hour.

| Exit code | Meaning |
| --- | --- |
| 0 | Success, including already up to date |
| 2 | Invalid command or arguments |
| 3 | Invalid/unreadable config |
| 4 | Network/provider error after retries |
| 5 | Invalid, incomplete or oversized image |
| 6 | Windows could not apply the wallpaper |
| 7 | Cache, metadata or local file error |
| 8 | Another process held the cache lock too long |

## Uninstall

From the repository folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\uninstall.ps1
```

This removes the task and executables and preserves the config, cache and
wallpaper. To remove the config and cache too:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\uninstall.ps1 -Purge
```

Before purging, any cached images currently used by Windows are copied into
Pictures and their per-monitor wallpaper paths updated. This preserves your
visible wallpaper without leaving Windows pointing at deleted cache files.
An independently chosen wallpaper is left in place. `detach` is the CLI command
used for this step. If retention fails, the config and cache are kept.

## Development

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Windows\test.ps1 -Online -Scheduler -Installer
```

Builds go in the ignored `Windows\build` folder. One executable provides console
output, and the other uses the same source compiled as a Windows application
for silent scheduling. Both target .NET Framework 4.8 and AnyCPU.

Tests cover config, provider parsing, image validation, cache cycling/pruning,
atomic replacement, locking, downloads, retries, concurrent updates, and failed
apply/save recovery. The wallpaper tests use a fake desktop; the native API probe
only reads monitor state. `-Online` also downloads and validates today's real
Bing image. `-Scheduler` validates the task definition and temporarily registers
a task to run `--version`, then deletes it. `-Installer` runs install, upgrade,
uninstall and purge against an isolated copy with a disabled task. Tests do not
change your desktop wallpaper or install the app into your real user profile.

Microsoft references: [Windows wallpaper API](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-idesktopwallpaper),
[Task Scheduler](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskfolder-registertaskdefinition),
[included .NET Framework versions](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server),
[TLS defaults](https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls).
