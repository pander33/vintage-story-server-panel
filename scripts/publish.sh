#!/usr/bin/env bash
# Поставка по ADR-09: self-contained, single-file, ReadyToRun, без trimming.
#
# IncludeNativeLibrariesForSelfExtract нужен для Microsoft.Data.Sqlite в демоне: без него
# нативная e_sqlite3 не попадает внутрь single-file, и выгрузка мира падает при первом
# обращении.
#
# Каталог artifacts/<RID> копируется на сервер целиком:
#   Panel.Web, Panel.Agent, Panel.Cli   ставятся как vs-panel-web, vs-panel-agent, vs-panel
#   VERSION, RID                        версия панели и платформа сборки
#   install.sh | install.cmd            первый шаг: sudo sh install.sh; на Windows — install.cmd
#   telemetry/                          мод телеметрии из telemetry/ или VS_TELEMETRY_DIST
#
#   bash scripts/publish.sh linux-x64
#   bash scripts/publish.sh win-x64
#   VS_TELEMETRY_DIST=<каталог с vspaneltelemetry_*.zip> bash scripts/publish.sh linux-x64
set -euo pipefail

RID="${1:-linux-x64}"
OUT="artifacts/$RID"

rm -rf "$OUT"
for project in src/Panel.Web src/Panel.Agent src/Panel.Cli; do
  dotnet publish "$project" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishReadyToRun=true \
    -p:PublishTrimmed=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --output "$OUT"
done

# Отладочные символы, настройки разработки и web.config для IIS на сервер не едут: юниты запускают
# бинарник без WorkingDirectory, appsettings рядом с ним никто не читает.
rm -f "$OUT"/*.pdb "$OUT"/appsettings*.json "$OUT"/*.staticwebassets.*.json "$OUT"/web.config

VERSION="$(sed -n 's:.*<PanelVersion>\(.*\)</PanelVersion>.*:\1:p' Directory.Build.props | head -n 1)"
[[ -n "$VERSION" ]] || { echo 'PanelVersion не найден в Directory.Build.props' >&2; exit 1; }
printf '%s\n' "$VERSION" > "$OUT/VERSION"
printf '%s\n' "$RID" > "$OUT/RID"

# Первый шаг установки. Linux — install.sh с LF и исполняемый: его запускает sh на сервере.
# Windows — install.cmd с CRLF: cmd читает пакетный файл построчно и спотыкается на LF.
case "$RID" in
  win-*)
    cp -- deploy/windows/install.cmd "$OUT/install.cmd"
    ;;
  *)
    tr -d '\r' < deploy/install.sh > "$OUT/install.sh"
    chmod 0755 "$OUT/install.sh"
    ;;
esac

# Мод телеметрии лежит в репозитории, в telemetry/; VS_TELEMETRY_DIST подменяет каталог.
TELEMETRY="${VS_TELEMETRY_DIST:-telemetry}"
if [[ -d "$TELEMETRY" ]]; then
  mkdir -p "$OUT/telemetry"
  cp -- "$TELEMETRY"/vspaneltelemetry_*.zip "$OUT/telemetry/"
else
  echo "Мод телеметрии не включён: нет каталога $TELEMETRY" >&2
fi

echo
echo "== сборка $VERSION ($RID) =="
ls -lh "$OUT" | sort -k5 -h
echo
case "$RID" in
  win-*) echo "На машину: скопируйте $OUT целиком и запустите в нём install.cmd (двойным щелчком)" ;;
  *) echo "На сервер: скопируйте $OUT целиком и выполните в нём sudo sh install.sh" ;;
esac
