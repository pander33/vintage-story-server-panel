# Наша панель 0.2.0: возможности, доказательность и позиционирование

## Что уже есть и для кого это продукт?

### Takeaway
Это специализированная веб-панель для владельца одного хоста с одним или несколькими серверами Vintage Story, на Linux либо Windows. Конкурировать разумно удобством игровых сценариев, а не универсальностью хостинговой платформы.

### Cited Findings
- Один хост, несколько игровых серверов; EN/RU интерфейс — [README](/D:/Programs/vintage-story-panel-net/README.md:12).
- Создание сервера скачивает официальный пакет и проверяет контрольную сумму; игроки, каталог консольных команд, аудит свободной консоли — [README](/D:/Programs/vintage-story-panel-net/README.md:46).
- Миры: создание/загрузка/выгрузка/копирование/переключение, архив и корзина. Моды: включение/отключение/откат; зависимости и ModDB с проверкой совместимости при показе и скачивании — [README](/D:/Programs/vintage-story-panel-net/README.md:60).
- Резервные копии по требованию/расписанию, полное либо мировое восстановление, ротация; обслуживание, watchdog, обновления игры со снимком и откатом — [README](/D:/Programs/vintage-story-panel-net/README.md:68).
- serverconfig как форма с предварительным diff/откатом, JSON редактор, настройки мира/модов, Discord/Telegram/webhook, аккаунты и TOTP — [README](/D:/Programs/vintage-story-panel-net/README.md:78).
- Viewer/Operator/Administrator; скрытие IP для Viewer, фраза подтверждения необратимого действия — [README](/D:/Programs/vintage-story-panel-net/README.md:86).
- Обмен сборками — опись модов и настройки; сами моды не упакованы, устанавливаются из ModDB по одному с подтверждением. Не обещать клиентский лаунчер/готовый ZIP всех модов — [findings](/D:/Programs/vintage-story-panel-net/docs/findings.md:1776).
- Подтверждены HTTP-маршруты export/inspect/apply modpack и редактора конфигурации — [ModEndpoints](/D:/Programs/vintage-story-panel-net/src/Panel.Web/Endpoints/ModEndpoints.cs:241), [ConfigEndpoints](/D:/Programs/vintage-story-panel-net/src/Panel.Web/Endpoints/ConfigEndpoints.cs:29).
- Стандартная MIT: права использования, изменения, распространения, сублицензирования и продажи при сохранении copyright/license — [LICENSE](/D:/Programs/vintage-story-panel-net/LICENSE:5).
- Linux x64/ARM64 с systemd, Windows x64; пакет self-contained панели, отдельно инсталлятор готовит игровой runtime — [релиз](/D:/Programs/vintage-story-panel-net/docs/releases/0.2.0.md:40), [README](/D:/Programs/vintage-story-panel-net/README.md:110).

### Inferences
- Целевая ниша: администратор сообщества/домашнего сервера/VPS, которому нужны миры, моды, обслуживание и распределение прав из браузера.
- Основные отличительные акценты для сравнения: ModDB плюс совместимость/зависимости, конфигурация мира и serverconfig с diff, игровая телеметрия/диагностика, несколько серверов на обычном Windows либо Linux.
- Не называем ничего уникальным без сопоставления с актуальными конкурентами.
- MIT — аргумент свободы эксплуатации и развития после открытия исходников. Временная приватность мешает именно доступности/внешнему аудиту, а не меняет текст лицензии.

### Gaps
- Не исследовали реальное число пользователей, нагрузку, экономию времени, UX на телефоне и доступность для людей с ограничениями.
- Нет подтверждённого предложения удалённых узлов, биллинга, коммерческого SLA, универсальной поддержки других игр или клиентского лаунчера. README заявляет только несколько серверов на одной машине; полнотекстовый поиск в src не выявил SFTP/Docker/OAuth/SSO реализаций. Это отсутствие найденного подтверждения, не формальное доказательство невозможности.

## Чем сильны реализация и релиз, и где нельзя обещать зрелость?

### Takeaway
Объём реализации уже большой. Главный риск для публичного сравнения — перепутать наличие кода и unit tests с проверенной установкой и работой на настоящем сервере.

### Cited Findings
- Политика readiness использует свежую телеметрию текущего процесса, отвергает чужую; при отсутствии телеметрии запрещает автоматическое восстановление — [ReadinessPolicy](/D:/Programs/vintage-story-panel-net/src/Panel.Runtime/Domain/ReadinessPolicy.cs:38), [проверка unavailable](/D:/Programs/vintage-story-panel-net/src/Panel.Runtime/Domain/ReadinessPolicy.cs:53).
- В пакетах поставляется telemetry mod 0.4.0 — [релиз](/D:/Programs/vintage-story-panel-net/docs/releases/0.2.0.md:16).
- CI включает Linux и Windows tests на manual/tag, три RID, smoke CLI version, верификацию пакетов — [workflow](/D:/Programs/vintage-story-panel-net/.github/workflows/ci.yml:27), [пакеты](/D:/Programs/vintage-story-panel-net/.github/workflows/ci.yml:94), [validator](/D:/Programs/vintage-story-panel-net/.github/workflows/ci.yml:153).
- Release notes прямо фиксируют непроверенные clean Windows install, Linux systemd/permissions/game/archive интеграции; unit tests не сертифицируют их — [ограничения](/D:/Programs/vintage-story-panel-net/docs/releases/0.2.0.md:53).
- У modpack не проверены браузерный import диалог, реальная установка ModDB и запись настроек демоном — [findings](/D:/Programs/vintage-story-panel-net/docs/findings.md:1827).
- Панель по умолчанию HTTP; HTTPS требует отдельного nginx/Caddy и secure cookie. README содержит короткую Caddy инструкцию — [HTTPS](/D:/Programs/vintage-story-panel-net/README.md:271).
- Windows службы и игры работают LocalSystem, каталоги доступны только SYSTEM/admin; это явно раскрыто — [Windows модель](/D:/Programs/vintage-story-panel-net/README.md:199).
- Readme сообщает early release с возможными изменениями форматов/CLI — [README](/D:/Programs/vintage-story-panel-net/README.md:19).

### Inferences
- Хороший технический акцент: панель различает «процесс жив» и «игровой сервер отвечает»; это полезнее общих обещаний watchdog.
- Нельзя маркировать как production proven либо гарантированно безопасную только по архитектуре и тестам.
- Количество 1624 tests и успешные оба CI run — данные завершённой сессии координатора, здесь тесты повторно не запускались.
- Временная приватность repo и недоступные без токена get scripts — состояние, сообщённое пользователем/проверенное предыдущей сессией; локальная документация может по-прежнему показывать публичные URL.

### Gaps
- Независимый pentest/security review отсутствует в просмотренных материалах.
- Не доказаны свежими целевыми интеграционными проверками восстановление после аварии, реальные обновление/rollback, длительная работа, службы/ACL/firewall и поддержанные версии игры.

## Какие приоритеты дадут конкурентный эффект?

### Takeaway
Сначала доказать путь установки и восстановления, затем сделать преимущества видимыми. Список функций уже достаточен для первого набора администраторов.

### Cited Findings
- Существующие команды install/update/rollback/repair/doctor и диагностика описаны — [CLI](/D:/Programs/vintage-story-panel-net/README.md:338), [support](/D:/Programs/vintage-story-panel-net/README.md:399).
- One-command GitHub bootstrap с SHA256 уже включён — [релиз](/D:/Programs/vintage-story-panel-net/docs/releases/0.2.0.md:15).
- Встроенный TLS не заявлен; есть proxy guidance — [README](/D:/Programs/vintage-story-panel-net/README.md:271).
- Уже присутствуют screenshots функций — [README](/D:/Programs/vintage-story-panel-net/README.md:97).

### Inferences
1. P0: вернуть публичную доступность только по решению владельца; проверить anonymous latest/pinned installers. Не переписывать get scripts на токены ради временной приватности без запроса.
2. P0: clean machine matrix Windows10/11/Server и актуальные Debian/Ubuntu x64, Linux ARM64. Install→setup→create→ModDB→backup→restore→game update/rollback→panel update/rollback→reboot; особенно права, службы, firewall, отказ скачивания. Выполнять только на разрешённых целевых машинах, не на машине разработки.
3. P0: показать результаты этих сценариев, поддерживаемые ОС/версии игры, известных ошибок и ограничения. Первые 3–5 внешних администраторов важнее ещё десятка функций.
4. P1: короткое видео «с нуля до сервера» плюс демонстрация полезного сценария модов/backup/restore, отдельная таблица «для кого».
5. P1: облегчить безопасный доступ через HTTPS, например проверенная инструкция/опциональный companion setup; Caddy docs уже существуют, не объявлять их отсутствующими. Учесть пользователей без своего домена.
6. P1: следовать сигналам первых пользователей по UX/миграции существующего сервера/диагностике; не предполагать, что импорт существующих инсталляций сейчас полностью автоматизирован.
7. P2: Docker/SFTP/много удалённых узлов/SSO только при подтверждённом спросе. Это расширение аудитории и сложности, не необходимое условие конкуренции среди владельцев небольшого VS сообщества.

### Gaps
- Окончательный порядок P1/P2 требует данных о конкурентах и интервью; исследование нашей реализации не доказывает востребованность каждого расширения.

