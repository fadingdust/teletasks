#!/usr/bin/env bash
#
# Re-publish TeleTasks and restart the systemd user service. Run after
# a code change to push it through to the running bot.
#
# What it does, in order:
#   1. git pull --ff-only           (skip with --no-pull)
#   2. dotnet publish -c Release    → $INSTALL_DIR
#   3. systemctl --user restart teletasks.service
#                                   (skip with --no-restart, also
#                                    skipped if the unit isn't
#                                    installed yet)
#   4. tail the last few log lines so you can verify it came up
#
# Usage:
#   scripts/republish.sh                  # pull, publish, restart
#   scripts/republish.sh --no-pull        # skip git pull (publish local changes only)
#   scripts/republish.sh --no-restart     # publish but don't touch the service
#
# Failures stop the pipeline:
#   - git pull --ff-only refuses to auto-merge → you fix the divergence
#     manually before retrying. We never publish a half-merged tree.
#   - publish errors → no restart, the running service keeps serving
#     the previous good build.
#   - service fails to start after restart → the script tails the
#     journal for you and exits non-zero so CI / cron notices.

set -euo pipefail

PULL=1
RESTART=1
for arg in "$@"; do
    case "$arg" in
        --no-pull)    PULL=0 ;;
        --no-restart) RESTART=0 ;;
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

# ─── sanity ──────────────────────────────────────────────────────────

if [ ! -f "$PROJECT" ]; then
    echo "error: TeleTasks project not found at $PROJECT" >&2
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: 'dotnet' not found on PATH." >&2
    exit 1
fi

# ─── git pull ────────────────────────────────────────────────────────

if [ "$PULL" -eq 1 ]; then
    if [ ! -d "$REPO_ROOT/.git" ]; then
        echo "==> not a git checkout, skipping pull"
    else
        echo "==> git pull --ff-only (in $REPO_ROOT)"
        # --ff-only refuses to merge or rebase. If there's a divergence,
        # bail out so the user can resolve it deliberately rather than
        # letting the script auto-merge into a possibly-broken state.
        if ! git -C "$REPO_ROOT" pull --ff-only; then
            echo "error: git pull --ff-only failed (divergent branches?)." >&2
            echo "       Resolve manually, then re-run with --no-pull." >&2
            exit 1
        fi
    fi
fi

# ─── publish ─────────────────────────────────────────────────────────

echo "==> publishing (Release) → $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
dotnet publish "$PROJECT" -c Release -o "$INSTALL_DIR" --nologo

# ─── restart ─────────────────────────────────────────────────────────

if [ "$RESTART" -eq 0 ]; then
    echo "==> --no-restart: leaving service alone."
    echo "    Run 'systemctl --user restart teletasks' to pick up the new build."
    exit 0
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "==> systemctl not found; nothing to restart."
    exit 0
fi

if [ ! -f "$UNIT_PATH" ]; then
    echo "==> no systemd unit at $UNIT_PATH; skipping restart."
    echo "    (run scripts/install-systemd.sh to register the service.)"
    exit 0
fi

echo "==> restarting teletasks.service"
systemctl --user restart teletasks.service

# Brief grace period before we check is-active — systemd reports the
# unit as activating for a tick before it settles.
sleep 1

if systemctl --user is-active --quiet teletasks.service; then
    echo "==> teletasks.service is running"
    echo
    echo "Recent logs (last 10 lines):"
    journalctl --user -u teletasks.service -n 10 --no-pager
else
    echo "==> teletasks.service did NOT come up cleanly. Recent logs:"
    journalctl --user -u teletasks.service -n 30 --no-pager
    exit 1
fi

echo
echo "Done. Follow logs: journalctl --user -u teletasks -f"
