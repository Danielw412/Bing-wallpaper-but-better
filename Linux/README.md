# daily-wallpaper

Sets the Bing image of the day as your GNOME wallpaper (light and dark), once per day.

There is no daemon or tray icon. A `systemd --user` timer starts the
`daily-wallpaper update` command once a day; it fetches the image, applies it
and exits. Between runs it uses no memory. `Persistent=true` means a run missed
while the laptop was off happens at the next login.

Targets Debian with GNOME.

## Install

You need Rust 1.89 or newer. Debian 13's packaged `rustc` (1.85) is too old, so use
[rustup](https://rustup.rs). You also need `gsettings`, which comes with GNOME.

```sh
./install.sh
daily-wallpaper update      # optional: apply today's wallpaper now instead of waiting for 08:00
```

`install.sh` builds a release binary and installs:

| What            | Where                                                    |
| --------------- | -------------------------------------------------------- |
| Binary          | `~/.local/bin/daily-wallpaper`                           |
| Config          | `~/.config/daily-wallpaper/config.toml` (existing one kept) |
| Cache/metadata  | `~/.local/share/daily-wallpaper/`                        |
| systemd units   | `~/.config/systemd/user/daily-wallpaper.{service,timer}` |

It then runs `systemctl --user daemon-reload` and `systemctl --user enable --now daily-wallpaper.timer`.
To upgrade, run `./install.sh` again.

## Usage

```text
daily-wallpaper update     Download the current Bing image if it's new, then apply it
daily-wallpaper info       Show the applied wallpaper (title, copyright, file, URL)
daily-wallpaper previous   Apply the previous (older) cached wallpaper
daily-wallpaper next       Apply the next (newer) cached wallpaper
```

`previous` and `next` cycle through the cache and wrap around at either end. They
never use the network.

## Configuration

`~/.config/daily-wallpaper/config.toml`. Every key is optional, and the defaults are:

```toml
provider = "bing"
market = "en-US"        # Bing region, e.g. en-GB, de-DE, ja-JP
resolution = "UHD"      # "UHD" (usually 3840x2160) or e.g. "1920x1080"

[cache]
keep = 7                # how many recent wallpapers to keep

[wallpaper]
mode = "zoom"           # zoom, scaled, centered, stretched, spanned, wallpaper
```

Unknown keys and invalid values are rejected with exit code 3.

## Timer, manual refresh and logs

```sh
systemctl --user list-timers daily-wallpaper.timer   # next/last run
systemctl --user status daily-wallpaper.service      # result of the last run
systemctl --user start daily-wallpaper.service       # refresh now, through systemd
daily-wallpaper update                               # refresh now, in the terminal
journalctl --user -u daily-wallpaper.service         # logs (add -f to follow, -b for this boot)
```

The timer runs at 08:00 local time. Bing publishes the en-US image at 07:00 UTC.
To pick another time, run `systemctl --user edit daily-wallpaper.timer` and add:

```ini
[Timer]
OnCalendar=
OnCalendar=*-*-* 10:00:00
```

## How an update works

1. Fetch Bing's metadata. If that image is already cached, exit.
2. Download the image to a temporary file next to the cache.
3. Check the HTTP status and `Content-Type`, check the JPEG/PNG signature, and check
   for an end-of-image marker so a truncated download is caught.
4. Take the lock, rename the file into the cache atomically, and set
   `picture-uri`, `picture-uri-dark` and `picture-options` with `gsettings`.
5. Write `metadata.json` atomically. Then delete wallpapers beyond `keep`, and any
   image file the metadata doesn't list, such as one left by a failed run.

If any step fails, the metadata and the current wallpaper are left as they were.
Connection errors, timeouts, HTTP 5xx and HTTP 429 are retried 4 times
(after 5, 15, 30 and 60 s), which covers Wi-Fi reconnecting after a resume.
The command then gives up until the next timer run. Each request also has a
timeout: 10 s to connect, 30 s for the metadata and 120 s for the image.

An exclusive `flock` on `~/.local/share/daily-wallpaper/lock` stops simultaneous
runs (for example the timer and a manual `next`) from corrupting state. The lock
is held only for local work, never during downloads.

## Exit codes

| Code | Meaning                                                  |
| ---- | -------------------------------------------------------- |
| 0    | Success, including "already up to date"                  |
| 2    | Bad command-line usage                                   |
| 3    | Invalid or unreadable config                             |
| 4    | Network or provider error (after retries)                |
| 5    | Downloaded file is not a complete JPEG/PNG               |
| 6    | `gsettings` failed to apply the wallpaper                |
| 7    | Cache or metadata error (including nothing to cycle)     |
| 8    | Another instance held the lock for more than 15 s        |

## Uninstall

```sh
./uninstall.sh            # remove the binary and units; keep config and cached images
./uninstall.sh --purge    # also delete config and cache (and reset the GNOME wallpaper
                          # if it points at a deleted image)
```

## Development

```sh
cargo fmt && cargo clippy --all-targets -- -D warnings && cargo test
cargo build --release
systemd-analyze --user verify dist/daily-wallpaper.service dist/daily-wallpaper.timer  # after install: ExecStart must exist
```

Wallpaper sources live in `src/providers/`. A new provider implements the `WallpaperProvider`
trait (`name` and `current`) and gets a variant in `config::ProviderKind`.
