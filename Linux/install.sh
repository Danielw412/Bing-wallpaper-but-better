#!/usr/bin/env bash
# Build daily-wallpaper and install it for the current user:
#   ~/.local/bin/daily-wallpaper
#   ~/.config/daily-wallpaper/config.toml           (only if not already present)
#   ~/.config/systemd/user/daily-wallpaper.{service,timer}
# then enable and start the daily timer.
set -euo pipefail

if [[ $EUID -eq 0 ]]; then
    echo "install.sh: run this as your normal user, not root" >&2
    exit 1
fi

cd "$(dirname "$(readlink -f "$0")")"

config_home="${XDG_CONFIG_HOME:-$HOME/.config}"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
bin_dir="$HOME/.local/bin"
config_dir="$config_home/daily-wallpaper"
data_dir="$data_home/daily-wallpaper"
unit_dir="$config_home/systemd/user"

cargo="$(command -v cargo || echo "$HOME/.cargo/bin/cargo")"
if [[ ! -x $cargo ]]; then
    echo "install.sh: cargo not found; install Rust from https://rustup.rs or with 'sudo apt install cargo'" >&2
    exit 1
fi

echo "==> Building release binary"
"$cargo" build --release --locked

echo "==> Installing files"
install -d -m 755 "$bin_dir" "$config_dir" "$data_dir" "$unit_dir"
install -m 755 target/release/daily-wallpaper "$bin_dir/daily-wallpaper"
if [[ -e $config_dir/config.toml ]]; then
    echo "    keeping existing $config_dir/config.toml"
else
    install -m 644 dist/config.toml "$config_dir/config.toml"
fi
install -m 644 dist/daily-wallpaper.service dist/daily-wallpaper.timer "$unit_dir/"

echo "==> Enabling timer"
systemctl --user daemon-reload
systemctl --user enable --now daily-wallpaper.timer
systemctl --user list-timers daily-wallpaper.timer --no-pager

echo
echo "Installed. To apply today's wallpaper right away, run:"
case ":$PATH:" in
    *":$bin_dir:"*) echo "    daily-wallpaper update" ;;
    *) echo "    $bin_dir/daily-wallpaper update    ($bin_dir is not on your PATH yet; log out and in)" ;;
esac
