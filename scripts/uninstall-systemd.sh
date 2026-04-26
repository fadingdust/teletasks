#!/usr/bin/env bash
#
# Tear down the TeleTasks systemd user service. Stops, disables,
# removes the unit file. By default leaves the publish dir
# ($HOME/.local/share/teletasks) AND the config dir
# ($HOME/.config/teletasks) alone — pass --purge to wipe the
# binary, --purge-config to wipe the config (which destroys your
# appsettings.Local.json and tasks.json — irreversible).

set -euo pipefail

PURGE_BINARY=0
PURGE_CONFIG=0
DISABLE_LINGER=0

for arg in "$@"; do
    case "$arg" in
        --purge)         PURGE_BINARY=1 ;;
        --purge-config)  PURGE_CONFIG=1 ;;
        --disable-linger) DISABLE_LINGER=1 ;;
        -h|--help)
            sed -n '2,/^$/p' "$0" | sed 's/^#\s\?//'
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg" >&2
            exit 2
            ;;
    esac
done

INSTALL_DIR="${TELETASKS_INSTALL_DIR:-$HOME/.local/share/teletasks}"
UNIT_PATH="$HOME/.config/systemd/user/teletasks.service"
CONFIG_DIR="${TELETASKS_CONFIG_DIR:-$HOME/.config/teletasks}"

if ! command -v systemctl >/dev/null 2>&1; then
    echo "no systemctl on PATH; nothing to do."
    exit 0
fi

if systemctl --user list-unit-files teletasks.service --quiet 2>/dev/null \
   || [ -f "$UNIT_PATH" ]; then
    echo "==> stopping + disabling teletasks.service"
    systemctl --user stop teletasks.service 2>/dev/null || true
    systemctl --user disable teletasks.service 2>/dev/null || true
fi

if [ -f "$UNIT_PATH" ]; then
    echo "==> removing $UNIT_PATH"
    rm -f "$UNIT_PATH"
    systemctl --user daemon-reload
fi

if [ "$PURGE_BINARY" -eq 1 ] && [ -d "$INSTALL_DIR" ]; then
    echo "==> removing $INSTALL_DIR"
    rm -rf "$INSTALL_DIR"
fi

if [ "$PURGE_CONFIG" -eq 1 ] && [ -d "$CONFIG_DIR" ]; then
    echo "==> removing $CONFIG_DIR (config + tasks.json + run-logs)"
    rm -rf "$CONFIG_DIR"
fi

if [ "$DISABLE_LINGER" -eq 1 ]; then
    if loginctl show-user "$USER" -p Linger 2>/dev/null | grep -q "Linger=yes"; then
        echo "==> disabling user-linger"
        sudo loginctl disable-linger "$USER" || true
    fi
fi

echo "Done."
[ "$PURGE_BINARY" -eq 0 ] && echo "  Binary kept at  $INSTALL_DIR (re-install with scripts/install-systemd.sh --no-publish)"
[ "$PURGE_CONFIG" -eq 0 ] && echo "  Config kept at  $CONFIG_DIR"
