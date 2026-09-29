#!/bin/sh
# Install or update the panel with one command: download the package for this machine from
# GitHub Releases, verify its checksum and run install.sh from it.
#
#   curl -fsSL https://raw.githubusercontent.com/ruslan120p/vintage-story-panel-net/master/deploy/get.sh | sudo sh
#   ... | sudo sh -s -- --access internet            install.sh options after "-s --"
#   ... | sudo VS_PANEL_VERSION=0.3.0 sh             a specific release instead of the latest
#
# Everything runs inside main: sh starts it only after the whole script has arrived, so a
# broken download cannot run half of it.
set -eu

repository=ruslan120p/vintage-story-panel-net

fail() {
    printf 'vs-panel: %s\n' "$1" >&2
    exit 1
}

fetch() {
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --retry 3 -o "$2" "$1"
    else
        wget -q -O "$2" "$1"
    fi
}

main() {
    [ "$(id -u)" -eq 0 ] || fail 'Run as root: curl -fsSL <address of get.sh> | sudo sh'
    [ "$(uname -s)" = Linux ] || fail "Unsupported system: $(uname -s). On Windows use get.ps1"
    case $(uname -m) in
        x86_64 | amd64) rid=linux-x64 ;;
        aarch64 | arm64) rid=linux-arm64 ;;
        *) fail "Unsupported architecture: $(uname -m)" ;;
    esac
    command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1 || fail 'curl or wget is required'
    for tool in tar gzip sha256sum; do
        command -v "$tool" >/dev/null 2>&1 || fail "$tool is required"
    done

    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    trap 'exit 130' INT TERM

    # One tag for both files: latest/download could serve them from different releases if a new
    # one is published in between.
    version=${VS_PANEL_VERSION:-}
    if [ -z "$version" ]; then
        fetch "https://api.github.com/repos/$repository/releases/latest" "$work/latest.json" \
            || fail "Could not query the latest release of $repository on GitHub"
        tag=$(sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$work/latest.json" | head -n 1)
        [ -n "$tag" ] || fail "No published release in $repository"
    else
        case $version in
            v*) tag=$version ;;
            *) tag=v$version ;;
        esac
    fi
    case $tag in
        v | *[!0-9A-Za-z._-]* | v[!0-9A-Za-z]*) fail "Invalid release: $tag" ;;
    esac

    package=vs-panel-$rid.tar.gz
    base=https://github.com/$repository/releases/download/$tag
    printf 'Downloading vs-panel %s (%s)\n' "${tag#v}" "$rid"
    fetch "$base/SHA256SUMS" "$work/SHA256SUMS" || fail "Could not download $base/SHA256SUMS"
    fetch "$base/$package" "$work/$package" || fail "Could not download $base/$package"

    expected=$(awk -v name="$package" '$2 == name || $2 == "*" name { print tolower($1); exit }' "$work/SHA256SUMS")
    [ -n "$expected" ] || fail "Release $tag has no checksum for $package"
    actual=$(sha256sum "$work/$package" | cut -d ' ' -f 1)
    [ "$expected" = "$actual" ] || fail "Checksum mismatch for $package: the download is corrupt, try again"

    tar -xzf "$work/$package" -C "$work" --no-same-owner
    directory=$work/vs-panel-$rid
    [ -f "$directory/install.sh" ] || fail "install.sh is missing from $package"
    [ -r "$directory/VERSION" ] && [ "$(cat "$directory/VERSION")" = "${tag#v}" ] \
        || fail "Package version does not match $tag"
    [ -r "$directory/RID" ] && [ "$(cat "$directory/RID")" = "$rid" ] \
        || fail "Package target does not match $rid"

    # With curl | sh standard input is this script: give the installer the terminal so that it
    # can ask where to keep game servers.
    status=0
    if [ ! -t 0 ] && (: </dev/tty) 2>/dev/null; then
        sh "$directory/install.sh" "$@" </dev/tty || status=$?
    else
        sh "$directory/install.sh" "$@" || status=$?
    fi
    exit "$status"
}

main "$@"
