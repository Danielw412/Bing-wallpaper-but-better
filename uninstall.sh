#!/usr/bin/env bash
# Remove daily-wallpaper for the current user.
#   ./uninstall.sh           keep the config file and cached wallpapers
#   ./uninstall.sh --purge   delete them too
set -euo pipefail

purge=false
case "${1:-}" in
    "") ;;
    --purge) purge=true ;;
    *) echo "usage: $0 [--purge]" >&2; exit 2 ;;
esac

config_home="${XDG_CONFIG_HOME:-$HOME/.config}"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
unit_dir="$config_home/systemd/user"

systemctl --user disable --now daily-wallpaper.timer 2>/dev/null || true
rm -f "$unit_dir/daily-wallpaper.service" "$unit_dir/daily-wallpaper.timer"
systemctl --user daemon-reload
systemctl --user reset-failed daily-wallpaper.service 2>/dev/null || true
rm -f "$HOME/.local/bin/daily-wallpaper" "$data_home/systemd/timers/stamp-daily-wallpaper.timer"

if ! $purge; then
    echo "Removed daily-wallpaper. Kept $config_home/daily-wallpaper and $data_home/daily-wallpaper"
    echo "(run '$0 --purge' to delete them as well)."
    exit 0
fi

# Don't leave GNOME pointing at an image we are about to delete.
schema=org.gnome.desktop.background
for key in picture-uri picture-uri-dark; do
    if gsettings get "$schema" "$key" 2>/dev/null | grep -q '/daily-wallpaper/images/'; then
        gsettings reset "$schema" "$key"
        echo "Reset $key to the GNOME default."
    fi
done
rm -rf "$config_home/daily-wallpaper" "$data_home/daily-wallpaper"
echo "Removed daily-wallpaper, its config and its cached wallpapers."
