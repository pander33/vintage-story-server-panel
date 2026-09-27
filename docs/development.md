# Разработка

Технические заметки о Vintage Story Panel. Для пользователей — [README](../README.md).
Правила работы в репозитории — [CLAUDE.md](../CLAUDE.md). Найденное при реализации,
включая опровергнутое, — [findings.md](findings.md).

## Сборка

```bash
dotnet build
dotnet test                         # 1467 тестов, ни одному не нужен сервер
```

Нужен .NET SDK 10. Стиль — `.editorconfig`; проверка:
`dotnet format whitespace --verify-no-changes`.

### Поставка

На Windows публикуйте из PowerShell в корне репозитория; Bash и WSL не нужны:

```powershell
.\scripts\publish.ps1 win-x64
```

Из `cmd.exe` запускайте тот же сценарий через штатный Windows PowerShell:

```cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 win-x64
```

Нужен .NET SDK 10. Если PowerShell запрещает запуск `.ps1`, используйте приведённую
команду с `-ExecutionPolicy Bypass` и в PowerShell. Чтобы включить мод телеметрии,
перед публикацией задайте `VS_TELEMETRY_DIST` как обычный путь Windows к каталогу с
`vspaneltelemetry_*.zip` (например, `$env:VS_TELEMETRY_DIST = 'D:\telemetry\dist'`).

На Linux используйте Bash:

```bash
bash scripts/publish.sh linux-x64
bash scripts/publish.sh linux-arm64
```

Для Linux мод добавляется так: `VS_TELEMETRY_DIST=/path/to/dist bash scripts/publish.sh linux-x64`.
Цели проекта: `linux-x64`, `linux-arm64`, `win-x64`. Оба сценария создают
self-contained single-file пакет (ADR-09) в `artifacts/<цель>`: три исполняемых файла,
`VERSION`, `RID`, `install.sh` или `install.cmd` и, если задан каталог, `telemetry/`.
Публикация заменяет только пакет своей цели; запуск `dotnet build` и `dotnet test`
пакет в `artifacts` не обновляет. Перед установкой проверьте `artifacts/<цель>/VERSION`
и время изменения исполняемых файлов.

Без мода телеметрии (`VS_TELEMETRY_DIST`) панель ставится, но готовность серверов видит
ограниченно: наблюдатель и автоперезапуск работают хуже.

### Значок

Исходник — `scripts/icons.ps1`: геометрия прежнего `brand-mark` и контур буквы из Georgia.
На Windows команда `pwsh scripts/icons.ps1` (System.Drawing) пересобирает все форматы:
- `Static/favicon.svg` — знак в панели, мастере установки, README и браузере;
- `Static/favicon.ico` — стандартный `/favicon.ico` для браузеров, которые запрашивают
  значок сайта без учёта ссылок на странице;
- `assets/vs-panel.ico` — значок `vs-panel.exe`, `vs-panel-web.exe` и `vs-panel-agent.exe`
  (`ApplicationIcon` в `Directory.Build.props`) и ярлыков «Vintage Story Panel»;
- `Static/favicon-32.png` и `Static/apple-touch-icon.png` — для браузеров без SVG и для
  ярлыка на телефоне.

Сгенерированные файлы хранятся в репозитории. При смене знака правьте только скрипт.

## Два процесса, две роли

**`Panel.Agent`** — привилегированный демон.
- **Linux:** работает от root, слушает Unix-сокет, проверяет uid подключившегося через
  `SO_PEERCRED` и отвечает одной строкой JSON.
- **Windows:** служба от LocalSystem. Сам держит игровые серверы дочерними процессами:
  их stdin — консоль, вывод — `run\<id>.console.log`. Слушает именованный канал, открытый
  только своей учётной записи, администраторам и тем, кто назван в `--allow-sid`.
- Пути выводит сам из реестра: панель присылает только идентификатор сервера.

**`Panel.Web`** — веб-процесс.
- На Linux работает от непривилегированного `vs-panel` и доступа к игровым каталогам не
  имеет. Sudo ему не нужен, поэтому его юнит можно ужесточить (`deploy/vs-panel.service`).
- Интерфейс встроен в сборку ресурсами (`src/Panel.Web/Static`): ванильный JS без сборки,
  CSP без встроенных скриптов и стилей.

**`Panel.Cli`** — установщик `vs-panel`.
- Linux: root и systemd.
- Windows: администратор, службы через диспетчер служб.

Содержимое `PANEL_STATE_DIR` он не трогает сам, а зовёт `vs-panel-web manage`
(findings §67, §69).

Устройство по сборкам и правила, которые проверяют архитектурные тесты, — в
[CLAUDE.md](../CLAUDE.md).

## Установщик изнутри

### Linux

`install.sh` проверяет root и архитектуру и передаёт управление `vs-panel install`:
- учётная запись `vs-panel`;
- версия в `/opt/vs-panel/releases/<версия>`, активная — ссылка `current`;
- реестр серверов — только если его нет, с переносом конфигурации 0.9;
- юниты демона и панели, среда .NET для игры;
- каталог серверов: вопрос в терминале, `--servers-dir` или `VS_PANEL_SERVERS_DIR`;
- первый сервер с настройками мастера, если реестр пуст (`VS_PANEL_CREATE_SERVER=0` — нет);
- доступ к панели: заданные строки `vs-panel.env` (`--access`, `--bind`, `--port`, …) и
  правила ufw для порта панели, если ufw активен;
- одноразовая ссылка на мастер — на 127.0.0.1 и по адресам машины в сети.

Поверх 1.2.x удаляются sudo-правило и хелпер.

### Windows

`install.cmd` просит права администратора и передаёт управление `vs-panel install`:
- службы `vs-panel-agent` и `vs-panel` от LocalSystem (решение владельца 2026-09-24,
  findings §69);
- программа в `Program Files\vs-panel\releases\<версия>`, ссылка `current` в PATH;
- данные в `ProgramData\vs-panel`, доступ только SYSTEM и администраторам;
- мод телеметрии — и в уже созданные серверы;
- доступ к панели — как на Linux; правило брандмауэра одно, `Vintage Story Panel`, через `netsh`;
- задачи планировщика 1.2.x удаляются.

Службы останавливаются до копирования версии: каталог с работающим `.exe` не переименовать.

### Доступ к панели и брандмауэр

Поставка слушает `0.0.0.0` с `PANEL_ALLOWED_HOSTS=*` и `PANEL_FIREWALL=lan` (findings §72).
Что открыть и кому, решает `PanelListener` (`Panel.Provisioning`). Его используют и
установщик (`PanelAccess` в `Panel.Cli`), и демон:
- параметры установки — `InstallOptions`: набор `--access localhost|lan|internet` и
  отдельные ключи поверх него; `lan` и `internet` ставят и `PANEL_ALLOWED_HOSTS=*`, иначе
  фильтр Host ответил бы 400 на обращение по адресу машины; значения проверяет `PanelSetting`, как в мастере;
- заданные строки переписываются в `vs-panel.env` на месте при `install`, `update` и
  `repair`; без ключей файл не меняется;
- смена `PANEL_BIND` или `PANEL_PORT` в мастере идёт через `panel-env-set`, и демон
  переставляет правило (`IProvisioningPlatform.SyncPanelFirewallAsync`); неудача
  возвращается аддитивным полем `firewall_warning`, строка всё равно записывается;
- `PANEL_FIREWALL` не входит в `PanelSetting`: из браузера охват не меняется;
- `uninstall` снимает правило панели.

Каталог серверов (findings §70) проверяет и готовит демон, операцией `instances-root-set`.
Требования:
- NTFS, путь без пробелов, родитель существует, сам каталог новый или пустой;
- обычный пользователь не может подменить каталог через его родителя.

Прежний каталог уходит в `former_instances_roots`. Серверы не переезжают.

Первую установку на Windows безопаснее проверять в «Песочнице Windows».

## Совместимость с 1.2.x

Панель читает `/etc/vs-panel.json`, `/etc/vs-panel/instances.json` и `secret.key` в том
же формате, что и 1.2.x (ADR-04, ADR-11, ADR-15). Обе версии сменяют друг друга на одной
машине без преобразования. Граница: после первой завершённой операции обновления в новой панели
откат на 1.2.x теряет историю операций.

HTTP-API 1.2.x заморожен (ADR-16, ADR-17): адреса, коды и поля совпадают, изменения только
аддитивные.

## Спайки

Решения, принятые условно и ждущие проверки на настоящей машине:

```bash
bash scripts/spikes/setpriv.sh    # ADR-07, пятнадцать минут
bash scripts/spikes/peercred.sh   # ADR-02, форма всей привилегированной стороны
```

Пока спайк `SO_PEERCRED` не пройден, долгоживущий root-демон не подтверждён. Код это
учитывает: проверка пира на не-Linux отказывает, а не пропускает.

## Где решения

Проектные решения и их обоснования — в обсидиане, не в git. Начинать с заметок
«ADR к фиксации до начала .NET-миграции» и «Вертикальный срез и спайки».
