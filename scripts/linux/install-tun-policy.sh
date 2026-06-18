#!/usr/bin/env bash
# Installs a polkit rule so Invisible Gorilla XRay TUN mode needs one-time root
# setup instead of a password prompt on every connect/disconnect.
set -euo pipefail

SUDO="${SUDO:-sudo}"
[[ "$(id -u)" == "0" ]] && SUDO=""

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
RULE_SRC="$SCRIPT_DIR/50-invisible-gorilla-xray-tun.rules"
RULE_DST="/etc/polkit-1/rules.d/50-invisible-gorilla-xray-tun.rules"

if [[ ! -f "$RULE_SRC" ]]; then
    echo "Rule file not found: $RULE_SRC" >&2
    exit 1
fi

if ! command -v pkexec >/dev/null 2>&1; then
    echo "pkexec not found. Install policykit (polkit package) first." >&2
    exit 1
fi

$SUDO install -Dm644 "$RULE_SRC" "$RULE_DST"
echo "Installed: $RULE_DST"
echo "Log out and back in (or reboot), then TUN connect/disconnect should not ask for root password."
