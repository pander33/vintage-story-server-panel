# Прямые конкуренты панели Vintage Story

## Какие специализированные менеджеры наиболее релевантны?

### Takeaway
На 2026-09-29 специализированные конкуренты уже закрывают моды, копии, конфигурацию и запуск. Основное различие — desktop для домашнего хостинга против постоянно работающей web-панели; наличие похожих кнопок не доказывает одинаковую надёжность.

### Cited Findings
- **Shulius Vintage Server Manager**: Windows desktop, NSIS installer, React/Rust/Tauri. Локальный режим работает offline без аккаунта: start/stop, console/logs, CPU/RAM history, backup/restore/pruning, перенос singleplayer/server saves, ModDB install/search и compatibility, modpack import/export, несколько переключаемых профилей, полный serverconfig editor, port test/public IP, player management, scheduled autosave/backup. Optional PocketBase backend позволяет группам передавать hosting общего мира: download перед сессией, upload после, lock предотвращает параллельные host, сохраняются последние 5 версий. Нет обязательного центрального сервера; backend ставится Docker/Coolify/binary. Автор прямо ограничивает cloud-сценарий группой друзей и hosting по очереди, не always-on public server. AGPL-3.0; app uses signed Tauri updater, forks generate own signing keys. HTTPS proxy рекомендован; instance admin trusted; большой save требует передачи по сети. — [README](https://github.com/ExLizer/Shulius-Vintage-Server-Manager).
- Shulius release page при проверке показывает **v0.2.20** как Latest, много предыдущих 0.2.x. Страница tag отдельно недоступна через fetch; дата свежего релиза не подтверждена. — [Releases](https://github.com/ExLizer/Shulius-Vintage-Server-Manager/releases).
- **WindowsGSH**: Windows 10+, self-contained release, desktop multigame hub. Заявлены install/update/verify, несколько серверов, console, players/CPU/RAM/history, backups/schedules/crash recovery/automatic updates, firewall/UPnP, optional browser remote, Discord status/alerts/control, import WindowsGSM. Главный продукт beta, интерфейсы могут меняться до stable. Source-available, не OSI open-source: private study/modification permitted, redistribution/rebranding/modified builds/commercial use требуют письменного разрешения. Modules исполняют C# с правами пользователя приложения. Это описанные автором возможности, не результаты независимого тестирования. — [Main README](https://github.com/WindowsGSH/WindowsGSH).
- WindowsGSH organisation подтверждает отдельный **WindowsGSH.VintageStory** module и MIT license этого repo, обновление 2026-08-04. Это подтверждает поддержку игры, но не гарантирует, что каждая общая функция hub поддерживается именно модулем VS. — [Organisation](https://github.com/WindowsGSH).
- WindowsGSH releases показывают **v0.1.0-beta.5** prerelease, self-contained win-x64 ZIP, unsigned beta warning и SHA256 verification. — [Releases](https://github.com/WindowsGSH/WindowsGSH/releases).
- **LauncherGo**: dedicated VS desktop manager, MIT, .NET 10/Avalonia, текущий README версия **2.7.1-pre.2**, Windows x64 installer/portable distribution. Реализованы wizard/server downloads, profiles, config/world rules, saves, console/charts, orphan-process handling, scheduled start/stop/backup/broadcast, mod scanning/enable/disable/dependencies/issues, FRP tunneling, QQ bot, embedded ServerAuth OAuth2/OIDC/Discourse SSO, local Server Bridge. App update SHA256, Chinese+English. Avalonia cross-platform не равно готовому Linux release: author says published workflow mainly Windows x64. — [Repository](https://github.com/vscn-studio/LauncherGo).
- LauncherGo ModDB listing отстаёт от repo: tester ZIP 2.6.1, README listing local development 2.6.7, тогда как main repo 2.7.1-pre.2. Нельзя считать ModDB номер последним release. — [Author listing](https://mods.vintagestory.at/launchergo); [Repository](https://github.com/vscn-studio/LauncherGo).

### Inferences
- Для позиционирования панели владельца разумно демонстрировать browser remote administration постоянно работающих серверов на Linux+Windows, role permissions/TOTP/audit и безопасное обслуживание. Это гипотеза ниши, которую нужно сравнить с реальными установками, не заявление об отсутствии этих функций у всех конкурентов.
- Shulius особенно силён в обмене hosting общего мира между друзьями; копировать этот сценарий не обязательно для панели 24/7.
- WindowsGSH перекрывает convenience/platform integration, LauncherGo — специализированный desktop workflow. Нельзя рекламировать панель как первую UI для VS.

### Gaps
- Не запускал ни один продукт, не измерял UX, resource usage, stability или safety. README claims ≠ verified operation.
- WindowsGSH VS module README/source и Web Access wiki не получены web fetch (cache/internal error). Game-specific matrix, browser permissions/TOTP и конкретная установка модуля не проверены; не считать их отсутствующими.
- Отдельная цена WindowsGSH в открытых прочитанных страницах не указана. Не называть полностью бесплатным для любого применения; commercial restrictions explicit. Shulius/LauncherGo open-source licence permits use, but operational VPS costs separate.
- GitHub stars/число commits не использованы как доля рынка или installed base. Подтверждённых пользователей и downloads для сравнительного ranking нет.

## Какие web/Docker альтернативы существуют?

### Takeaway
Есть более близкие по форме web проекты и множество контейнерных решений. Сведения о маленькой экспериментальной панели должны иметь меньший вес, чем текущие документы доступных проектов.

### Cited Findings
- **rettoph VintageServerManager**: авторская публикация 2025-09-08 описывает Docker web portal, backup creation/restore, mod install/update, game version switching, direct terminal; перед mod install/update автоматическая копия saves/mods/mod configs. Автор отмечал extreme experimental и отсутствие auth, просил держать за firewall; auth, scheduled backups, mod config editor/save management были planned. Есть запрос ARM64 от другого пользователя 2026-02-24, но это не доказательство текущего отсутствия ARM64. Docker command image git.retto.ph/tony/vintage-server-manager:latest. — [Author forum announcement](https://www.vintagestory.at/forums/topic/17313-vintage-story-server-manager/).
- **DarkMatterProductions/vintagestory**: Docker dedicated server, volumes для сохранения данных, env/YAML/serverconfig configuration, mods specified as download paths, optional VintageRCon mod and browser RCON client. Authentication required default; username/password and OAuth settings documented, defaults require changes, OAuth requires external HTTPS reverse proxy. Container lifecycle/log/stat operations documented via docker CLI. Это RCON console plus container delivery, не подтверждённая полнофункциональная многосерверная панель. — [Repository docs](https://github.com/DarkMatterProductions/vintagestory).
- **MErevGe/VintageStory-Server**: MIT Docker image, linux/amd64 only, automatic official server/mod downloads plus dependency resolution, configurable game/.NET tags, env settings, uid/gid, persistent /data, docker attach console, TCP healthcheck. Runtime .NET tags cover VS1.19–1.22+. — [Repository](https://github.com/MErevGe/VintageStory-Server).
- **quartzar/vintage-story-server**: MIT Docker delivery with version env and persistent data, README list содержит web UI/robust backup/webhook/automated restart пункты как checklist. Search text removes checked/unchecked distinction: нельзя утверждать их реализацию без raw inspection. — [Repository](https://github.com/quartzar/vintage-story-server).

### Inferences
- Контейнер конкурирует с get.sh/get.ps1 за простоту первого запуска, но не обязательно с rich management после установки.
- Auth/roles и доступная публичная история разработки могут поддерживать доверие к панели владельца; не выводить превосходство безопасности лишь из старого форумного предупреждения rettoph.

### Gaps
- Git rettoph https://git.retto.ph/tony/VintageServerManager недоступен в web fetch. Текущая лицензия, версия, maintenance и устранение старого auth warning не подтверждены. Использовать как исторический пример, не обвинение современного продукта.
- Docker products не запущены и образ/manifest architectures independently не проверены.

## Какие похожие названия не являются прямыми host-панелями?

### Takeaway
В экосистеме слово panel/server manager неоднозначно: client launchers, in-game UI, server forks и telemetry mods решают соседние задачи. Их полезно учитывать, но не смешивать в одном рейтинге.

### Cited Findings
- **Story Forge** README позиционирует desktop приложение для players: game version install/switch/launch, modpacks import/export, server browser favorites/connect, account switching, auto updater, Windows/macOS/Linux packaging; GPL-3.0. Это primary подтверждённые функции, не удалённая host-панель. — [Repository](https://github.com/LovelessCodes/StoryForge).
- **VS Server Panel** — in-game community information UI: Overview/Home/Announcements/Description/Rules/Mod List/custom tabs. Requires VSUX Framework и мод на сервере и каждом connecting client. Название совпадает с host-панелью, назначение другое. — [Author ModDB listing](https://mods.vintagestory.at/vsserverpanel).
- **Stratum** — server-side fork для performance/stability/gameplay, **Nimbus** proxy, **Pulse** observability mod exposing Prometheus tick/player/chunk/memory metrics, **RiftLauncher** game installations/mods/worlds launcher. — [Organisation](https://github.com/StratumServer).
- **VSMonitor** — Go monitor/remote console with basic browser UI and WebSocket API. Documentation requires manual Go build and copy ui into Monitor directory. — [Repository](https://github.com/milochristiansen/VSMonitor).

### Inferences
- При анонсе лучше явно писать web administration for dedicated Vintage Story servers и показывать browser управление остановленным/работающим сервером, чтобы не путали с in-game VS Server Panel.
- Prometheus/Discord/tunneling/shared-world sync — потенциальные соседние интеграции, не обязательные core функции первого релиза.

### Gaps
- Story Forge release announcements заявляли эволюцию в broader suite; прочитанный README не даёт полной актуальной матрицы dedicated hosting. Не утверждать, что он совсем не умеет запуск сервера.
- VSMonitor maintenance freshness/license не проверены; не включать равноправно в shortlist.
