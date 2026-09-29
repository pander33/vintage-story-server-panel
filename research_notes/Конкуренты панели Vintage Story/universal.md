# Универсальные панели и менеджеры серверов

## Какие альтернативы реально поддерживают Vintage Story?

### Takeaway
На 2026-09-29 подтверждены AMP, Pterodactyl, Pelican, PufferPanel и LinuxGSM; WindowsGSM поддерживает игру через сторонний плагин. Основной коммерческий соперник — AMP; основной бесплатный веб-соперник — Pterodactyl/Pelican; PufferPanel конкурирует простотой развёртывания, LinuxGSM — практичным CLI.

### Cited Findings
- AMP имеет отдельный современный шаблон `Vintage Story 1.18.8+`: Windows/Linux, native aarch64, Docker необязателен, консоль чтения/записи, готовность по regex строки запуска, события подключения/выхода/чата; флаг `SupportsLiveSettingsChanges=False`. Не путать с `vintage-story.kvp`, который явно Legacy ≤1.18.7. — [Современный шаблон](https://raw.githubusercontent.com/CubeCoders/AMPTemplates/main/vintage-story-new.kvp).
- AMP-обновление само скачивает .NET SDK и сервер с выбором stable/unstable/pre, проверяет версию уже установленного сервера. Linux ARM64 собирается из основного пакета и экспериментального ARM64 сервера; предусмотрены ветки 1.18–1.22. — [Сценарии установки/обновления](https://raw.githubusercontent.com/CubeCoders/AMPTemplates/main/vintage-story-newupdates.json).
- AMP-форма специально знает имя/приветствие/лимит игроков/PvP/UPnP/публикацию и WhitelistMode 1.20+, выбор канала/версии и источника скачивания. Это уже игровая интеграция, а не только универсальные кнопки процесса. — [Настройки шаблона](https://raw.githubusercontent.com/CubeCoders/AMPTemplates/main/vintage-story-newconfig.json).
- Pterodactyl поддерживает Vintage Story в официальной организации game-eggs. Текущий egg экспортирован 2026-03-03: образы .NET8/9/10, запуск с dataPath, портом, MaxClients; ready по строке запуска; stop `/stop`; установщик получает latest через API либо конкретную версию, stable/unstable/pre. Поле `config.files` пустое: специализированная форма всей конфигурации здесь не объявлена. Переменная SQLITE_TMPDIR учтена для `/db vacuum`. — [Egg](https://raw.githubusercontent.com/pterodactyl/game-eggs/main/vintage_story/egg-vintage-story.json).
- Pelican имеет собственный egg Vintage Story в официально связанной коллекции `pelican-eggs/games-standalone`; экспорт PLCN_v3 от 2026-02-10. README указывает поддержку stable/unstable/pre и что сервер поддерживает моды (это не заявление о ModDB-менеджере). — [Egg](https://raw.githubusercontent.com/pelican-eggs/games-standalone/main/vintage_story/egg-vintage-story.json), [README](https://raw.githubusercontent.com/pelican-eggs/games-standalone/main/vintage_story/README.md).
- PufferPanel v3 имеет официальный шаблон Vintage Story Linux amd64, host/Docker, по умолчанию игра1.20.10 и .NET8.0.15. Устанавливает runtime для host, скачивает версию по прямой stable URL, принудительно записывает runtimeconfig для net8.0, запускает с портом/лимитом игроков. Пользователь выбирает версию, но runtime скрыт от userEdit. — [Шаблон](https://raw.githubusercontent.com/pufferpanel/templates/v3/vintage-story/vintage-story.json), [Описание](https://github.com/pufferpanel/templates/tree/v3/vintage-story).
- LinuxGSM имеет официальный `vintsserver` с командами start/stop/restart/console/update/backup/monitor, логами и автоматизацией через cron. — [Vintage Story](https://linuxgsm.com/servers/vintsserver/).
- WindowsGSM.VintageStory — сторонний MIT-плагин Soulflare3, требует WindowsGSM≥1.21.0, устанавливается импортом plugin ZIP/папки и Reload Plugins. — [Плагин](https://github.com/Soulflare3/WindowsGSM.VintageStory).

### Inferences
- Нашу панель нельзя позиционировать как единственный способ запустить Vintage Story в веб-интерфейсе: это уже умеют несколько решений.
- Главная потенциальная специализация — цельный игровой процесс ModDB/совместимость/миры/живые настройки/телеметрия/диагностика/безопасное обновление и откат; сравнивать надо конкретные операции, а не только количество пунктов интерфейса.
- PufferPanel шаблон выглядит требующим обновления для .NET10-игры; это риск шаблона, не доказательство невозможности настроить современный сервер вручную.

### Gaps
- Ни одно конкурентное решение не запускалось. Поддержка по шаблону не равна подтверждённой чистой установке или совместимости каждого мода.
- Полное содержимое Pelican egg не отображается веб-инструментом, хотя existence/format/export подтверждены; подробные runtime версии не утверждать.
- Код WindowsGSM плагина не удалось прочитать, поэтому актуальная совместимость с1.22 не установлена; наличие плагина не доказывает её.
- Отсутствие специализированной функции в проверенном egg не означает её отсутствия в сторонних расширениях.

## Цена, платформы и нагрузка установки

### Takeaway
Бесплатность и наличие резервных копий не уникальны. AMP близок к нашей панели по удобству Windows/Linux и имеет дешёвую пожизненную лицензию; бесплатные Docker-панели требуют дополнительного обслуживания инфраструктуры, но Pelican упрощает установку относительно Pterodactyl.

### Cited Findings
- Официальная индексируемая страница AMP указывает lifetime Starter $10 (5 instances/3 panel users), Professional $20 (15/unlimited users/multi-system), Advanced $40 (50/OIDC/branding), Enterprise от$20/месяц для resale. Цены USD приблизительные и зависят от региона/оплаты/курса; текущая динамическая страница отдаёт Loading, поэтому перед цитированием точной цены желательно проверить checkout. — [Издания AMP](https://www.cubecoders.com/AMP/TQ).
- Старый официальный comparison2022 называет Professional $30, что конфликтует с индексируемой коммерческой страницей $20; не переносить старую таблицу в текущий отчёт как актуальную цену. — [Старая таблица](https://discourse.cubecoders.com/t/editions-comparison-sheet/2247).
- AMP self-hosted Windows/Linux, можно установить и изучить интерфейс/поддерживаемые приложения без лицензии до покупки. — [Описание/издания](https://www.cubecoders.com/AMP/TQ).
- Pterodactyl бесплатен, MIT, игры запускает в Docker с изоляцией и лимитами ресурсов. — [Официальный сайт](https://pterodactyl.io/).
- Pterodactyl прямо предупреждает, что это сложная система с несколькими зависимостями, требует root и знакомства с документацией; установка включает PHP/Composer, базу данных, Redis, webserver, queue worker/cron и отдельный Wings/Docker. — [Установка Panel](https://pterodactyl.io/panel/1.0/getting_started.html), [Wings](https://pterodactyl.io/wings/1.0/installing.html).
- Pelican бесплатный open-source, текущий репозиторий AGPL-3.0 (не MIT, хотя старые сведения в сети могут говорить иначе). Panel Laravel+Docker/Wings; допускает SQLite, webinstaller, Docker install, OAuth, roles/admin permissions и plugin system. — [Репозиторий](https://github.com/pelican/panel), [Собственное сравнение](https://pelican.dev/docs/comparison/).
- Pelican документация всё ещё явно предупреждает Beta; требует базовых знаний Linux, PHP8.3–8.5 с расширениями, Composer и webserver; SQLite избавляет от отдельного DB-сервера. Wings только Linux, требует Docker CE; официально Windows не работает. — [Panel](https://pelican.dev/docs/panel/getting-started/), [Wings](https://pelican.dev/docs/wings/install/).
- PufferPanel бесплатен Apache-2.0, multiple servers/users и выдача доступа другим. Linux-пакет/Docker, поддержка panel AMD64/ARM64; у самого VS-шаблона ограничение amd64. Установка apt/yum, создание админа CLI, systemctl. Документация настоятельно рекомендует Docker и называет host/standard небезопасным для production. — [Репозиторий](https://github.com/pufferpanel/pufferpanel), [Установка](https://docs.pufferpanel.com/en/3.x/installing.html).
- WindowsGSM — MIT desktop GUI Windows, установка/import/start/stop/update нескольких серверов; LinuxGSM — отдельный CLI-продукт, а не Linux-версия той же веб-панели. — [WindowsGSM](https://github.com/windowsgsm/windowsgsm), [Документация](https://docs.windowsgsm.com/), [LinuxGSM](https://github.com/GameServerManagers/LinuxGSM).

### Inferences
- Для одного VS-сервера наше отсутствие PHP/Redis/Composer/Docker может быть сильным преимуществом по обслуживанию — при доказанной установке get.sh/get.ps1 на чистой машине.
- Для хостера с несколькими играми/нодами Pterodactyl/Pelican/AMP дают иную, более широкую ценность; разумнее не конкурировать за универсальный hosting control plane.
- Заявление «поддерживаем ARM64» само по себе не преимущество против AMP: там есть специальная поддержка экспериментального VSARM64.

### Gaps
- Текущий checkout AMP, региональная доступность покупки и налог не подтверждены. Есть отдельная официальная страница regional availability, но её текст не извлечён: https://discourse.cubecoders.com/t/regional-availability/21718 .
- Лицензию LinuxGSM в извлечённом фрагменте не увидел; не приписывать MIT только по памяти.

## Копии, моды, доступ и позиционирование

### Takeaway
Резервные копии, консоль, автоперезапуск и многопользовательский доступ — базовые конкурентные функции. Отличие следует подтверждать глубиной именно Vintage Story: знанием состояния мира, процесса, применения настроек и зависимостей ModDB.

### Cited Findings
- AMP имеет fixed/event scheduler, nightly backups и webhooks (Professional+). Рекламирует мод-установку/обновление Modrinth/Hangar и других sources, при этом CurseForge/SteamWorkshop отмечены coming/in development; Vintage Story ModDB на проверенной странице не заявлен. Не делать вывод, что любых VS-модов нет. — [Возможности AMP](https://www.cubecoders.com/AMP/WAN).
- AMP игровая интеграция уже понимает joins/leaves/chat и готовность по regex; поэтому «у конкурентов только start/stop» неверно. Однако `Console.MetricsRegex` пустое и live settings False в конкретном VS-шаблоне. — [Шаблон](https://raw.githubusercontent.com/CubeCoders/AMPTemplates/main/vintage-story-new.kvp).
- Pterodactyl backups local Wings и S3-compatible remote storage, с настройкой соответствующего driver. — [Backups](https://pterodactyl.io/panel/1.0/additional_configuration.html).
- PufferPanel3 config имеет server backup folder, SQLite/MySQL/PostgreSQL/SQLServer, remote node credentials и SFTP. — [Configuration](https://docs.pufferpanel.com/en/3.x/guides/config.html).
- WindowsGSM заявляет one-click backup/restore, crash autorestart, autoupdate каждые30мин, restart crontab, Mod Manager и remote control через Discord. Это общие функции продукта: работа конкретно с VS зависит от плагина. — [README features](https://github.com/windowsgsm/windowsgsm).
- LinuxGSM VS backup создаёт полный tar.bzip2 серверного дерева; monitor проверяет процесс/query, перезапускает и уведомляет, cron автоматизирует действия. — [VS usage](https://linuxgsm.com/servers/vintsserver/).
- Pelican собственная таблица предупреждает, что сведения о конкурентах не постоянно обновляются. Она ошибочно/неоднозначно могла бы создать впечатление отсутствия доступа в Pterodactyl/PufferPanel: «Roles & Permissions» там следует понимать как собственную admin-role систему, а не отсутствие любых прав на сервер. Использовать эту таблицу для отрицательных утверждений о конкурентах нельзя. — [Оговорка таблицы](https://pelican.dev/docs/comparison/).

### Inferences
- Конкурентное сообщение: «Специализированное управление Vintage Story без развёртывания универсальной хостинговой платформы» убедительнее «бесплатный AMP».
- Самая сильная демонстрация: установить мод с зависимостями и проверкой совместимости; увидеть свежую игровую телеметрию; изменить настройку с показом необходимости перезапуска; обновить игру со снимком/восстановлением. Сравнивать прохождение сценария и количество ручных шагов.
- Наше обещание более безопасного восстановления/целостности требует собственных integration доказательств; unit tests и архитектура не доказывают эксплуатационную надёжность против зрелых конкурентов.

### Gaps
- SQLite-consistent hot backup, integrated VS ModDB dependency resolution, world schema/diff/application tracker и telemetry mod у конкурентов не подтверждены в проверенных официальных источниках; маркировать «не подтверждено», не «нет».
- Не исследованы addons marketplace глубоко; нельзя утверждать абсолютную уникальность функций.
