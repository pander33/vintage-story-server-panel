#!/bin/sh
# Initial setup step before the binary is installed: check privileges and architecture,
# then hand control to `vs-panel install` (Panel.Cli) from this build.
#
#   sudo sh install.sh                      install or update
#   sudo VS_PANEL_CREATE_SERVER=0 sh install.sh    skip the first game server
#   sudo sh install.sh --servers-dir /data/vs-servers   game server directory
#   sudo sh install.sh --access internet    allow internet access (default: lan)
#   sudo sh install.sh --access localhost   local access through an SSH tunnel
#   sudo sh install.sh --port 9000 --secure-cookie true   panel settings in vs-panel.env
set -eu

fail() {
    printf '%s\n' "$1" >&2
    exit 1
}

[ "$(id -u)" -eq 0 ] || fail 'Run as root: sudo sh install.sh'
dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)

case $(uname -m) in
    x86_64 | amd64) machine=linux-x64 ;;
    aarch64 | arm64) machine=linux-arm64 ;;
    *) fail "Unsupported architecture: $(uname -m)" ;;
esac

[ -r "$dir/RID" ] || fail "RID file is missing from $dir: this is not a panel build directory (scripts/publish.sh)"
built=$(cat "$dir/RID")
[ "$built" = "$machine" ] || fail "Build target is $built, but this machine is $machine: bash scripts/publish.sh $machine"
[ -f "$dir/Panel.Cli" ] || fail "Panel.Cli is missing from $dir"

chmod 0755 "$dir/Panel.Cli"
exec "$dir/Panel.Cli" install --from "$dir" "$@"
