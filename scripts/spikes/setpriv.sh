#!/usr/bin/env bash
# Проверка для ADR-07: можно ли внешней утилитой понизить uid/gid с очисткой
# дополнительных групп. В .NET нет точки между fork и exec, где это можно сделать.
set -u

GAME_USER="${1:-vsgame}"

echo "== есть ли setpriv =="
command -v setpriv && setpriv --version || { echo "НЕТ setpriv — смотри варианты C и D в ADR-07"; exit 1; }

echo
echo "== понижение с очисткой дополнительных групп =="
echo "ожидаем uid=$GAME_USER gid=$GAME_USER и НИ ОДНОЙ лишней группы:"
sudo setpriv --reuid="$GAME_USER" --regid="$GAME_USER" --clear-groups -- id

echo
echo "== привилегии действительно не возвращаются =="
echo "ожидаем Permission denied:"
sudo setpriv --reuid="$GAME_USER" --regid="$GAME_USER" --clear-groups -- \
  sh -c 'cat /etc/shadow 2>&1 | head -1'
