#!/usr/bin/env bash
#
# Register TeleTasks as a systemd *user* service so it starts on boot,
# auto-restarts on failure, and survives logout.
#
# Why a user unit (not a system unit)?
#   - No root needed for the bot itself; everything lives under $HOME.
#   - The bot reads its config from ~/.config/teletasks anyway —
#     a system-wide service would need extra plumbing to use the
#     same config path.
#   - Enabling user-linger (one sudo call) lets the unit run even
#     when you're not logged in, which is the only thing a system
#     unit actually buys you here.
#
# Usage:
#   scripts/install-systemd.sh                 # install + start
#   scripts/install-systemd.sh --no-publish    # skip dotnet publish (use existing $INSTALL_DIR)
#   scripts/install-systemd.sh --no-linger     # skip the sudo enable-linger step
#
# Re-running is safe: the script writes the unit file fresh and
# restarts the service. Pre-existing user edits to the unit file
# are overwritten.

set -euo pipefail

PUBLISH=1
ENABLE_LINGER=1
for arg in "$@"; do
    case "$arg" in
        --no-publish) PUBLISH=0 ;;
        --no-linger)  ENABLE_LINGER=0 ;;
        -h|--help)
            sed -n '2,/^$/p' "$0" | sed 's/^#\s\?//'
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg" >&2
            echo "Try --help." >&2
            exit 2
            ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/src/TeleTasks/TeleTasks.csproj"
INSTALL_DIR="${TELETASKS_INSTALL_DIR:-$HOME/.local/share/teletasks}"
UNIT_PATH="$HOME/.config/systemd/user/teletasks.service"
CONFIG_DIR="${TELETASKS_CONFIG_DIR:-$HOME/.config/teletasks}"

# ─── sanity checks ───────────────────────────────────────────────────

if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: 'dotnet' not found on PATH." >&2
    echo "       Install the .NET SDK first (https://dot.net/sdk)." >&2
    exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "error: 'systemctl' not found — this isn't a systemd-based system." >&2
    echo "       TeleTasks runs fine on its own; just 'dotnet TeleTasks.dll'." >&2
    exit 1
fi

if [ ! -f "$PROJECT" ]; then
    echo "error: TeleTasks project not found at $PROJECT" >&2
    echo "       Run this script from the repository root or its scripts/ dir." >&2
    exit 1
fi

# Resolve the absolute path of `dotnet` for the unit file. systemd doesn't
# inherit the user's PATH by default, so the unit must reference the
# binary directly.
DOTNET_BIN="$(command -v dotnet)"
DOTNET_BIN="$(readlink -f "$DOTNET_BIN" 2>/dev/null || echo "$DOTNET_BIN")"

# ─── publish ─────────────────────────────────────────────────────────

if [ "$PUBLISH" -eq 1 ]; then
    echo "==> publishing TeleTasks (Release) → $INSTALL_DIR"
    mkdir -p "$INSTALL_DIR"
    dotnet publish "$PROJECT" -c Release -o "$INSTALL_DIR" --nologo
else
    if [ ! -f "$INSTALL_DIR/TeleTasks.dll" ]; then
        echo "error: --no-publish requires an existing build at $INSTALL_DIR" >&2
        exit 1
    fi
    echo "==> using existing publish at $INSTALL_DIR (--no-publish)"
fi

# ─── first-run setup check ──────────────────────────────────────────

if [ ! -f "$CONFIG_DIR/appsettings.Local.json" ] && [ ! -f "$INSTALL_DIR/appsettings.Local.json" ]; then
    cat <<EOF

⚠️  No appsettings.Local.json found.

systemd can't run the interactive setup wizard (no terminal). Run it
once now to capture your bot token, allowed user ID, and Ollama
endpoint, then re-run this script:

    dotnet "$INSTALL_DIR/TeleTasks.dll" setup

Aborting install.
EOF
    exit 1
fi

# ─── unit file ───────────────────────────────────────────────────────

echo "==> writing systemd unit → $UNIT_PATH"
mkdir -p "$(dirname "$UNIT_PATH")"
cat > "$UNIT_PATH" <<EOF
[Unit]
Description=TeleTasks — chat → local Linux task bridge
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$INSTALL_DIR
ExecStart=$DOTNET_BIN $INSTALL_DIR/TeleTasks.dll
Restart=on-failure
RestartSec=5s

# Telemetry / globalization knobs that have stung dotnet-on-systemd
# users in the past:
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=DOTNET_NOLOGO=1

# Honour TELETASKS_CONFIG_DIR if you set it in your shell, but never
# point systemd at a path under /run or /tmp by default — those don't
# survive reboots.

[Install]
WantedBy=default.target
EOF

# ─── linger ──────────────────────────────────────────────────────────

if [ "$ENABLE_LINGER" -eq 1 ]; then
    if loginctl show-user "$USER" -p Linger 2>/dev/null | grep -q "Linger=yes"; then
        echo "==> linger already enabled for $USER"
    else
        echo "==> enabling user-linger (so the service runs without a login session)"
        echo "    (sudo password may be required)"
        sudo loginctl enable-linger "$USER"
    fi
else
    echo "==> skipping enable-linger (--no-linger). Service will only run while you're logged in."
fi

# ─── activate ────────────────────────────────────────────────────────

echo "==> reloading systemd, enabling, (re)starting"
systemctl --user daemon-reload
systemctl --user enable teletasks.service >/dev/null
systemctl --user restart teletasks.service

sleep 1
if systemctl --user is-active --quiet teletasks.service; then
    echo "==> teletasks.service is running"
else
    echo "==> teletasks.service failed to start — see logs:"
    echo "    journalctl --user -u teletasks -n 50 --no-pager"
    exit 1
fi

cat <<EOF

Installed. Useful commands:

  systemctl --user status teletasks
  journalctl --user -u teletasks -f      # follow logs
  systemctl --user restart teletasks
  systemctl --user stop teletasks

Config:    $CONFIG_DIR
Binary:    $INSTALL_DIR
Unit file: $UNIT_PATH

To uninstall: scripts/uninstall-systemd.sh
EOF
