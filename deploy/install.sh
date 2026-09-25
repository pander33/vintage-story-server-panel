#!/bin/sh
# Первый шаг установки панели 2.0 — то, что выполняется, пока бинарника на месте ещё нет:
# проверить root и архитектуру и передать управление установщику из этой же сборки.
# Всё остальное делает `vs-panel install` (Panel.Cli).
#
#   sudo sh install.sh                      установить или обновить
#   sudo VS_PANEL_CREATE_SERVER=0 sh install.sh    не создавать первый сервер
#   sudo sh install.sh --servers-dir /data/vs-servers   каталог игровых серверов
set -eu

fail() {
    printf '%s\n' "$1" >&2
    exit 1
}

[ "$(id -u)" -eq 0 ] || fail 'Запустите от root: sudo sh install.sh'
dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)

case $(uname -m) in
    x86_64 | amd64) machine=linux-x64 ;;
    aarch64 | arm64) machine=linux-arm64 ;;
    *) fail "Архитектура $(uname -m) не поддерживается" ;;
esac

[ -r "$dir/RID" ] || fail "В $dir нет файла RID: это не каталог сборки панели (scripts/publish.sh)"
built=$(cat "$dir/RID")
[ "$built" = "$machine" ] || fail "Сборка для $built, а эта машина — $machine: bash scripts/publish.sh $machine"
[ -f "$dir/Panel.Cli" ] || fail "В $dir нет Panel.Cli"

chmod 0755 "$dir/Panel.Cli"
exec "$dir/Panel.Cli" install --from "$dir" "$@"
