#!/usr/bin/env bash
# Проверка SO_PEERCRED на Unix-сокете (ADR-02). Долгоживущий root-демон допустим только
# с работающей проверкой пира: без неё root получает любой локальный процесс.
#
# Запускать после сборки: scripts/publish.sh linux-x64
set -u

AGENT="${1:-/opt/vs-panel/vs-panel-agent}"
SOCK="${2:-/run/vs-panel-spike.sock}"
PANEL_USER="${3:-vs-panel}"

echo "== uid панели =="
id -u "$PANEL_USER"

echo
echo "== демон спайка (от root, в фоне) =="
sudo "$AGENT" probe-peer --socket "$SOCK" &
DAEMON=$!
sleep 1

echo
echo "== клиент от $PANEL_USER: ожидаем его uid, НЕ нулевой =="
sudo -u "$PANEL_USER" "$AGENT" probe-peer-client --socket "$SOCK" || true

echo
echo "== клиент от nobody: ожидаем его uid, отличный от предыдущего =="
sudo -u nobody "$AGENT" probe-peer-client --socket "$SOCK" || true

echo
echo "== владелец и права сокета =="
ls -l "$SOCK"

kill "$DAEMON" 2>/dev/null || true
echo
echo "Вывод: если оба клиента дали СВОИ uid, проверка пира работает и ADR-02 подтверждён."
echo "Если оба дали uid демона (0) — вариант B отменяется, смотри вариант A."
