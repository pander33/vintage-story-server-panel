'use strict';

// Обзор: серверы, состояние выбранного, показатели, ресурсы, здоровье, события, расписание,
// а также создание, переименование, «убрать из панели» и удаление серверов.

(() => {
	const {$, $$, esc, fmtBytes, fmtDuration, fmtDateTime, fmtShortTime, fmtDay, toDate, state, api, toast, fail, busy, dialog} = VS;

	const PLAYSTYLES = {surviveandbuild: 'Выживание и строительство', exploration: 'Исследование', wildernesssurvival: 'Суровое выживание', homosapiens: 'Homo Sapiens', creativebuilding: 'Творчество'};
	const LANGUAGES = {ru: 'Русский', en: 'English', de: 'Deutsch', fr: 'Français', es: 'Español', zh: '中文'};
	const STEPS = {catalogue: 'Версия найдена в каталоге', download: 'Пакет скачан и сверен по md5', create: 'Каталоги и настройки созданы', service: 'Служба установлена', start: 'Сервер запущен', ready: 'Игра ответила телеметрией', rollback: 'Откат'};
	const WEEKDAYS = ['Понедельник', 'Вторник', 'Среда', 'Четверг', 'Пятница', 'Суббота', 'Воскресенье'];

	let metricsCurrent = null;

	// ---------- серверы ----------

	function badgeFor(item) {
		if (item.valid === false) return `<span class="badge error">Ошибка записи</span>`;
		if (item.busy) return `<span class="badge warn"><span class="pulse-dot warn"></span> Обслуживание</span>`;
		const health = item.watchdog?.state;
		if (health === 'ready') return `<span class="badge ok"><span class="pulse-dot ok"></span> Готов</span>`;
		if (!item.active && (health === 'stopped' || !health)) return `<span class="badge off">Остановлен</span>`;
		const level = VS.healthLevel(health || 'unknown');
		return `<span class="badge ${level}"><span class="pulse-dot ${level}"></span> ${esc(VS.healthTitles[health] || (item.active ? "Работает" : "Остановлен"))}</span>`;
	}

	function serverCard(item) {
		const selected = item.id === state.current;
		const invalid = item.valid === false;
		const players = item.players === null || item.players === undefined ? '—' : item.players;
		const name = item.name || item.id;
		return `<button type="button" class="server-card${selected ? " selected" : ""}${invalid ? " invalid" : ""}" data-server="${esc(item.id)}" aria-pressed="${selected}" ${invalid ? "disabled" : ""}>
			<span class="server-card-top"><span class="server-icon">${esc(name.slice(0, 1))}</span>${badgeFor(item)}</span>
			<strong>${esc(name)}</strong><small class="server-id">${esc(item.id)} · ${esc(item.game_version ? `Vintage Story ${item.game_version}` : "версия неизвестна")}</small>
			<span class="server-card-rule"></span>
			<span class="server-card-stats"><span><b>${esc(players)}</b><small>игроков</small></span><span><b>${item.world_size ? esc(fmtBytes(item.world_size)) : "—"}</b><small>миры</small></span><span><b>${esc(item.port || "—")}</b><small>порт</small></span></span>
			${invalid ? `<span class="server-card-note">${esc(item.detail || "Некорректная запись реестра")}</span>` : (item.watchdog?.detail ? `<span class="server-card-note">${esc(item.watchdog.detail)}</span>` : "")}
			<span class="selected-label">${selected ? "ВЫБРАННЫЙ СЕРВЕР" : "ВЫБРАТЬ СЕРВЕР"} <svg><use href="#i-arrow"/></svg></span>
		</button>`;
	}

	function renderServers() {
		const items = state.instances;
		$('#servers-count').textContent = String(items.length).padStart(2, '0');
		$('#server-grid').innerHTML = items.map(serverCard).join('') || '<div class="empty">Ни одного сервера не зарегистрировано. Создайте первый.</div>';
		const warning = $('#servers-warning');
		warning.textContent = state.instancesDetail ? `Реестр серверов недоступен: ${state.instancesDetail}` : '';
		warning.classList.toggle('hidden', !state.instancesDetail);
		renderSelected();
	}

	function renderSelected() {
		const server = VS.currentServer();
		$('#selected-name').textContent = server ? (server.name || server.id) : (state.current || 'Сервер не выбран');
		$('#selected-address').textContent = server?.port ? `${location.hostname}:${server.port}` : '—';
		const locked = !server || server.id === state.panelInstance;
		$('#server-forget').disabled = locked;
		$('#server-delete').disabled = locked;
		const reason = locked ? 'Сервер, к которому привязана панель, нельзя убрать или удалить' : '';
		$('#server-forget').title = reason;
		$('#server-delete').title = reason;
		$('#server-rename').disabled = !server;
	}

	$('#server-grid').addEventListener('click', event => {
		const card = event.target.closest('[data-server]');
		if (card && !card.disabled && card.dataset.server !== state.current) VS.selectInstance(card.dataset.server).catch(fail);
	});

	$('#copy-address').addEventListener('click', async () => {
		const text = $('#selected-address').textContent;
		if (text === '—') return;
		try { await navigator.clipboard.writeText(text); toast('Адрес скопирован', 'ok'); }
		catch { toast(`Адрес: ${text}`); }
	});

	$('#server-rename').addEventListener('click', async () => {
		const server = VS.currentServer();
		if (!server) return;
		const result = await dialog({eyebrow: 'НАЗВАНИЕ В ПАНЕЛИ', title: 'Переименовать сервер', text: 'Идентификатор, файлы и служба останутся прежними.', fields: [{label: 'Новое название', value: server.name || server.id}], confirm: 'Сохранить'});
		if (!result) return;
		try { await api('/api/instances/rename', {method: 'POST', body: {instance: server.id, name: result.values[0].trim()}}); toast('Название изменено', 'ok'); await VS.loadInstances(); }
		catch (error) { fail(error); }
	});

	$('#server-forget').addEventListener('click', async () => {
		const server = VS.currentServer();
		if (!server) return;
		const phrase = `ЗАБЫТЬ ${server.id}`;
		const result = await dialog({eyebrow: 'УБРАТЬ ИЗ ПАНЕЛИ', title: 'Перестать управлять сервером?', text: 'Запись исчезнет из панели. Файлы, мир, служба и резервные копии останутся на диске.', phrase, confirm: 'Убрать из панели', danger: true});
		if (!result) return;
		try { await api('/api/instances/forget', {method: 'POST', body: {instance: server.id, confirmation: result.phrase}}); toast(`Сервер ${server.id} снят с управления`, 'ok'); location.reload(); }
		catch (error) { fail(error); }
	});

	$('#server-delete').addEventListener('click', async () => {
		const server = VS.currentServer();
		if (!server) return;
		const phrase = `УДАЛИТЬ ${server.id}`;
		const result = await dialog({eyebrow: 'ОПАСНОЕ ДЕЙСТВИЕ', title: 'Удалить сервер с диска?', text: `Сервер «${server.name || server.id}» будет остановлен, его служба удалена, а каталог стёрт вместе с мирами, модами и резервными копиями. Восстановить будет нечего.`, phrase, confirm: 'Удалить с диска', danger: true});
		if (!result) return;
		try { await api('/api/instances/delete', {method: 'POST', body: {instance: server.id, confirmation: result.phrase}}); toast(`Сервер ${server.id} удалён`, 'ok'); location.reload(); }
		catch (error) { fail(error); }
	});

	// ---------- служба ----------

	const serviceLabels = {start: 'Запуск', restart: 'Перезапуск', stop: 'Остановка'};
	$$('[data-service]').forEach(button => button.addEventListener('click', async () => {
		const action = button.dataset.service;
		if (action !== 'start' && !await VS.confirmAction(action === 'stop' ? 'Остановить сервер?' : 'Перезапустить сервер?', 'Игроки будут отключены. Панель дождётся ответа службы.', action === 'stop' ? 'Остановить' : 'Перезапустить', action === 'stop')) return;
		try {
			await busy(button, () => api('/api/service', {method: 'POST', body: {action}}));
			toast(`${serviceLabels[action]}: команда выполнена`, 'ok');
			setTimeout(VS.refreshStatus, 800);
		} catch (error) { fail(error); }
	}));

	function renderStatus(status) {
		const active = !!status.active;
		$$('[data-service]').forEach(button => {
			if (button.dataset.roleLocked) return;
			button.disabled = button.dataset.service === 'start' ? active : !active;
		});
		const health = status.health || {};
		const healthState = health.state || 'unknown';
		$('#selected-description').textContent = active
			? `Работает ${fmtDuration(status.uptime)}${status.started ? ` · запущен ${status.started}` : ""}`
			: `Служба ${status.state || "неактивна"} · ${status.substate || "—"}`;

		const banner = $('#status-banner');
		const level = VS.healthLevel(healthState);
		banner.classList.toggle('stopped', !active);
		banner.classList.toggle('problem', active && level === 'error');
		$('#status-emblem').textContent = !active ? '■' : (level === 'ok' ? '✳' : (level === 'warn' ? '◐' : '!'));
		$('#banner-title').textContent = !active ? 'Сервер остановлен' : ({ready: 'Мир работает стабильно', starting: 'Сервер загружается', degraded: 'Ограниченная проверка', unresponsive: 'Сервер не отвечает', hung: 'Зависание подтверждено', recovering: 'Идёт восстановление', disabled: 'Наблюдение выключено'}[healthState] || 'Состояние неизвестно');
		$('#banner-description').textContent = health.detail || (active ? 'Панель ждёт первой проверки наблюдателя.' : 'Запустите сервер, чтобы игроки могли подключиться.');
		$('#banner-dot').className = `pulse-dot ${active ? level : "off"}`;
		$('#banner-state').textContent = !active ? 'Остановлен' : (level === 'ok' ? 'Всё хорошо' : (VS.healthTitles[healthState] || 'Проверить'));

		$('#tech-pid').textContent = status.pid || '—';
		$('#tech-restarts').textContent = status.restarts ?? '—';
		$('#tech-uptime').textContent = active ? fmtDuration(status.uptime) : '—';
		$('#metric-memory').textContent = status.memory ? fmtBytes(status.memory) : '—';
		renderHealth();
		stamp();
	}

	// ---------- телеметрия ----------

	function renderTelemetry(data) {
		const available = data && data.available && !data.stale;
		const players = available ? (data.players || []) : [];
		$('#metric-players').textContent = available ? players.length : '—';
		$('#metric-slots').textContent = available && data.max_players ? `/ ${data.max_players}` : '';
		const foot = $('#metric-players-foot');
		foot.className = `metric-foot ${available ? "good" : "warn"}`;
		foot.textContent = available ? (players.length ? `● ${players.map(player => player.name).slice(0, 3).join(", ")}${players.length > 3 ? "…" : ""}` : '● Сервер пуст') : (data?.detail || 'Телеметрия недоступна');
		const target = data?.target_tick_ms ? 1000 / Number(data.target_tick_ms) : 0;
		$('#metric-tps').textContent = available ? Number(data.observed_tps || 0).toFixed(1) : '—';
		$('#metric-tps-target').textContent = target ? `/ ${target.toFixed(0)}` : '';
		$('#metric-tick').textContent = available ? `${Number(data.average_tick_gap_ms || 0).toFixed(1)} мс` : '—';
		$('#tech-date').textContent = available && data.world ? (data.world.pretty_date || '—') : '—';
		$('#tech-traffic').textContent = available ? `↓ ${fmtBytes(data.total_received_bytes)} · ↑ ${fmtBytes(data.total_sent_bytes)}` : '—';
		const nav = $('#nav-players');
		nav.hidden = !available;
		nav.textContent = players.length;
		renderHealth();
	}

	// ---------- метрики ----------

	async function loadMetrics() {
		const data = await api(`/api/metrics?hours=${encodeURIComponent($("#metrics-hours").value)}`);
		metricsCurrent = data.current || null;
		const current = metricsCurrent;
		if (current) {
			const memoryPercent = Number(current.memory_percent || 0);
			$('#memory-percent').textContent = `${memoryPercent.toFixed(0)}%`;
			$('#memory-bar').style.width = `${Math.min(100, memoryPercent)}%`;
			if (current.memory) $('#metric-memory').textContent = fmtBytes(current.memory);
			$('#legend-cpu').textContent = `${Number(current.cpu_percent || 0).toFixed(0)}%`;
			$('#legend-ram').textContent = `${memoryPercent.toFixed(0)}%`;
			$('#legend-disk').textContent = `${Number(current.disk_percent || 0).toFixed(0)}%`;
			$('#tech-world').textContent = fmtBytes(current.world_size);
			$('#tech-save').textContent = current.current_world?.name || current.current_world?.save || '—';
			$('#tech-cpu').textContent = `${Number(current.cpu_percent || 0).toFixed(1)}%`;
			const backup = current.last_backup;
			const when = backup ? toDate(backup.modified) : null;
			$('#metric-backup').textContent = when ? fmtShortTime(backup.modified) : 'Нет';
			$('#metric-backup-day').textContent = when ? fmtDay(backup.modified) : '';
			const age = when ? (Date.now() - when.getTime()) / 3600000 : Infinity;
			const foot = $('#metric-backup-foot');
			foot.className = `metric-foot ${age < 30 ? "good" : "warn"}`;
			foot.textContent = !when ? 'Копий ещё нет — создайте первую' : (age < 30 ? `● ${fmtBytes(backup.size || 0)}` : '● Копия устарела');
		}
		const points = data.points || [];
		VS.lineChart($('#metrics-chart'), {
			series: [
				{values: points.map(point => Number(point.cpu_percent) || 0), cls: 'cpu', area: true},
				{values: points.map(point => Number(point.memory_percent) || 0), cls: 'ram'},
				{values: points.map(point => Number(point.disk_percent) || 0), cls: 'disk'},
			],
			max: Math.max(100, ...points.map(point => Number(point.cpu_percent) || 0)),
			unit: '%',
			labels: VS.timeLabels(points.map(point => point.ts)),
			empty: 'История появится после первых измерений (раз в минуту).',
		});
		renderHealth();
	}

	// ---------- здоровье ----------

	let recentProblems = [];

	function healthRow(level, icon, title, detail, stateText) {
		return `<div><span class="health-icon ${level}">${esc(icon)}</span><span><strong>${esc(title)}</strong><small>${esc(detail)}</small></span><b class="health-state ${level}">${esc(stateText)}</b></div>`;
	}

	function renderHealth() {
		const status = state.status, telemetry = state.telemetry, metrics = metricsCurrent;
		const rows = [];
		let score = 0, total = 0;
		const add = (level, ...rest) => { rows.push(healthRow(level, ...rest)); total += 1; score += level === 'ok' ? 1 : (level === 'warn' ? .5 : 0); };
		if (status) {
			add(status.active ? 'ok' : 'warn', status.active ? '✓' : '■', 'Игровая служба', status.active ? `Процесс работает · PID ${status.pid || "—"}` : `${status.state || "—"} · ${status.substate || "—"}`, status.active ? 'В норме' : 'Остановлена');
			const health = status.health?.state || 'unknown';
			const level = VS.healthLevel(health);
			add(level, level === 'ok' ? '✓' : '!', 'Наблюдатель', status.health?.detail || 'Проверка ожидается', VS.healthTitles[health] || health);
		}
		if (telemetry) {
			const fresh = telemetry.available && !telemetry.stale;
			add(fresh ? 'ok' : 'warn', fresh ? '✓' : '!', 'Телеметрия игры', fresh ? `Получена ${telemetry.age_seconds != null ? `${telemetry.age_seconds} с назад` : "только что"}` : (telemetry.detail || 'Мод телеметрии не отвечает'), fresh ? 'Свежая' : 'Нет данных');
		}
		if (metrics) {
			add(metrics.disk_warning ? 'error' : 'ok', metrics.disk_warning ? '!' : '✓', 'Свободное место', `${fmtBytes(metrics.disk_free)}${metrics.disk_total ? ` из ${fmtBytes(metrics.disk_total)}` : ""}`, metrics.disk_warning ? 'Мало' : 'Достаточно');
		}
		const problems = recentProblems.length;
		add(problems ? 'warn' : 'ok', problems ? '!' : '✓', 'Последние события', problems ? `${problems} предупреждений и ошибок за сутки` : 'Предупреждений за сутки нет', problems ? 'Проверить' : 'Спокойно');
		$('#health-list').innerHTML = rows.join('');
		const percent = total ? Math.round(score / total * 100) : 0;
		const chip = $('#health-chip');
		chip.textContent = total ? `${percent} / 100` : '—';
		chip.className = `status-chip ${percent >= 90 ? "ok" : (percent >= 60 ? "warn" : "error")}`;
	}

	// ---------- события и расписание ----------

	async function loadActivity() {
		const data = await api('/api/events?limit=60&severity=all');
		const events = data.events || [];
		const dayAgo = Date.now() / 1000 - 86400;
		recentProblems = events.filter(item => ['warning', 'error', 'critical'].includes(item.severity) && (item.ts || 0) > dayAgo);
		$('#activity-list').innerHTML = events.slice(0, 6).map(item => `<div><span class="activity-dot ${esc(item.severity || "info")}"></span><span><strong>${esc(item.title || item.kind)}</strong><small>${esc(item.detail || item.source || "")}</small></span><time>${esc(fmtDay(item.ts) === "сегодня" ? fmtShortTime(item.ts) : fmtDateTime(item.ts))}</time></div>`).join('') || '<div class="empty">Событий пока нет.</div>';
		renderHealth();
	}

	async function loadNextMaintenance() {
		const data = await api('/api/automation');
		const jobs = (data.jobs || []).filter(job => job.enabled && job.next_run).sort((a, b) => a.next_run - b.next_run);
		const box = $('#next-maintenance');
		const running = data.running;
		$('#nav-maintenance').hidden = !running;
		if (running) {
			box.innerHTML = `<div class="notice warn">Сейчас выполняется: <strong>${esc(running.name)}</strong>, начато ${esc(fmtDateTime(running.started_at))}.</div>`;
			return;
		}
		const job = jobs[0];
		if (!job) { box.innerHTML = '<div class="empty">Запланированных заданий нет. Настройте ежедневную копию и перезапуск в разделе «Обслуживание».</div>'; return; }
		const when = toDate(job.next_run);
		const steps = [job.warning_minutes?.length ? 'Предупреждение' : '', job.backup ? 'Резервная копия' : '', job.restart ? 'Перезапуск' : '', 'Проверка готовности'].filter(Boolean);
		box.innerHTML = `<div class="next-date"><b>${when.getDate()}</b><span>${esc(when.toLocaleDateString("ru-RU", {month: "long"}))}<br><strong>${esc(WEEKDAYS[(when.getDay() + 6) % 7])}, ${esc(fmtShortTime(job.next_run))}</strong></span></div>
			<div class="next-detail">${esc(job.name)} · ${steps.map(esc).join(' <span class="accent">→</span> ')}</div>
			<div class="next-note">${job.warning_minutes?.length ? `Игроки получат предупреждение за ${esc(job.warning_minutes.join(", "))} мин.` : "Без предупреждений игрокам."}</div>`;
	}

	function stamp() { $('#overview-updated').textContent = `● Данные обновлены ${VS.fmtTime(Date.now() / 1000)}`; }

	function greet() {
		const hour = new Date().getHours();
		const part = hour < 5 ? 'Доброй ночи' : hour < 12 ? 'Доброе утро' : hour < 18 ? 'Добрый день' : 'Добрый вечер';
		$('#greeting').innerHTML = `${esc(part)}, ${esc(state.user)} <span class="heading-spark">✳</span>`;
	}

	// ---------- создание сервера ----------

	function fill(select, values) { select.innerHTML = Object.entries(values).map(([value, title]) => `<option value="${esc(value)}">${esc(title)}</option>`).join(''); }

	$('#server-create-open').addEventListener('click', async () => {
		const box = $('#create-dialog');
		fill($('#new-playstyle'), PLAYSTYLES);
		fill($('#new-language'), LANGUAGES);
		const ids = new Set(state.instances.map(item => item.id)), ports = new Set(state.instances.map(item => Number(item.port) || 0));
		let id = 'main', index = 2;
		while (ids.has(id)) { id = `server${index}`; index += 1; }
		let port = 42420;
		while (ports.has(port)) port += 1;
		$('#new-id').value = id;
		$('#new-port').value = port;
		$('#create-error').textContent = '';
		$('#create-preflight').innerHTML = '';
		$('#create-location').textContent = '';
		box.showModal();
		api('/api/settings/servers-root').then(data => { serversRoot = data.instances_root || ''; showLocation(); }).catch(() => {});
		try {
			const data = await api('/api/catalogue');
			const stable = (data.versions || []).filter(item => item.md5 && item.channel === 'stable').slice(0, 15);
			$('#new-version').innerHTML = stable.map(item => `<option value="${esc(item.version)}">${esc(item.version)}${item.latest ? " · последняя" : ""} · ${esc(item.size_text || "")}</option>`).join('') || '<option value="">Каталог версий пуст</option>';
			if (data.stale) toast('Список версий взят из кэша: официальный сервер недоступен');
		} catch (error) { $('#create-error').textContent = `Каталог версий: ${error.message}`; }
	});
	let serversRoot = '';
	/** Где окажется сервер: каталог серверов и идентификатор — так же выводит путь демон. */
	function showLocation() {
		if (!serversRoot) return;
		const separator = /^[A-Za-z]:\\/.test(serversRoot) ? '\\' : '/';
		$('#create-location').textContent = `Каталог сервера: ${serversRoot}${separator}${$("#new-id").value.trim() || "…"}. Сменить каталог серверов — «Настройки» → «Панель».`;
	}
	$('#new-id').addEventListener('input', showLocation);
	$$('#create-dialog [data-close]').forEach(button => button.addEventListener('click', () => $('#create-dialog').close()));

	function renderPreflight(report) {
		const rows = [];
		const runtime = report.runtime || {};
		rows.push(`<div class="notice ${runtime.dotnet ? "ok" : "error"}"><strong>.NET для игры:</strong> ${esc(runtime.dotnet || "не найден")}</div>`);
		if (report.free !== undefined) rows.push(`<div class="notice ${report.free >= (report.required_free || 0) ? "ok" : "error"}"><strong>Место:</strong> свободно ${esc(fmtBytes(report.free))}, нужно ${esc(fmtBytes(report.required_free))}</div>`);
		(report.blockers || []).forEach(item => rows.push(`<div class="notice error">${esc(item)}</div>`));
		(report.warnings || []).forEach(item => rows.push(`<div class="notice warn">${esc(item)}</div>`));
		if (report.ok) rows.push('<div class="notice ok">Машина готова к созданию сервера.</div>');
		$('#create-preflight').innerHTML = rows.join('');
	}

	$('#create-preflight-run').addEventListener('click', async event => {
		try { renderPreflight(await busy(event.currentTarget, () => api('/api/provision/preflight', {method: 'POST', body: {version: $('#new-version').value}}))); }
		catch (error) { $('#create-error').textContent = error.message; }
	});

	$('#create-form').addEventListener('submit', async event => {
		event.preventDefault();
		$('#create-error').textContent = '';
		const id = $('#new-id').value.trim(), title = $('#new-title').value.trim();
		try {
			await busy(event.submitter, () => api('/api/provision/create', {method: 'POST', body: {
				id, name: title || id, version: $('#new-version').value, port: Number($('#new-port').value),
				server_name: $('#new-server-name').value.trim() || title || id, description: '',
				max_players: Number($('#new-players').value), whitelist: $('#new-whitelist').checked, pvp: $('#new-pvp').checked,
				language: $('#new-language').value, world_name: $('#new-world').value.trim() || 'Новый мир', seed: $('#new-seed').value.trim(), playstyle: $('#new-playstyle').value,
			}}));
			$('#create-dialog').close();
			toast('Создание сервера началось', 'ok');
			watchProvision();
		} catch (error) { $('#create-error').textContent = error.message; }
	});

	// Скрытая запись помнится в браузере: это удобство одного зрителя, а не состояние панели.
	const PROVISION_DISMISSED_KEY = 'vs-provision-dismissed';
	const dismissedProvision = () => { try { return localStorage.getItem(PROVISION_DISMISSED_KEY) || ''; } catch { return ''; } };
	let shownProvision = '';

	function renderProvision(record, running) {
		const box = $('#provision-progress');
		if (!record) { box.classList.add('hidden'); return; }
		box.classList.remove('hidden');
		shownProvision = record.id || '';
		$('#provision-dismiss').classList.toggle('hidden', Boolean(running));
		$('#provision-title').textContent = record.instance ? `Сервер «${record.instance}»${record.target_version ? ` · ${record.target_version}` : ""}` : (record.title || 'Создание сервера');
		$('#provision-detail').textContent = record.detail || (running ? 'Выполняется…' : '');
		const chip = $('#provision-state');
		const status = running ? 'running' : record.status;
		chip.textContent = {running: 'Выполняется', success: 'Готово', failed: 'Ошибка', interrupted: 'Прервано'}[status] || status || '—';
		chip.className = `status-chip ${status === "success" ? "ok" : (status === "running" ? "warn" : "error")}`;
		const order = ['catalogue', 'download', 'create', 'service', 'start', 'ready'];
		$('#provision-steps').innerHTML = order.map(action => {
			const step = (record.steps || []).find(item => item.action === action);
			const cls = step ? (step.status === 'ok' ? 'done' : step.status) : (running && !(record.steps || []).length && action === 'catalogue' ? 'running' : '');
			return `<li class="${esc(cls)}"><span>${esc(STEPS[action])}${step?.detail ? `<small>${esc(step.detail)}</small>` : ""}</span></li>`;
		}).join('');
	}

	let provisionShownAt = 0;
	async function loadProvision() {
		if (!VS.can('admin')) return {running: false};
		const data = await api('/api/provision');
		const record = data.current || (data.history || [])[0];
		// Завершённую запись показываем, пока её не скрыли: успешную — час, неудачную — сутки.
		// Дольше она только занимает место на обзоре.
		const finished = record && !data.running ? toDate(record.finished_at || record.started_at) : null;
		const keep = record && record.status === 'success' ? 3600000 : 86400000;
		const dismissed = record && !data.running && record.id && record.id === dismissedProvision();
		const fresh = !dismissed && (data.running || (finished && Date.now() - finished.getTime() < keep) || provisionShownAt);
		renderProvision(fresh ? record : null, data.running);
		return data;
	}

	$('#provision-dismiss').addEventListener('click', () => {
		try { localStorage.setItem(PROVISION_DISMISSED_KEY, shownProvision); } catch { /* приватный режим */ }
		provisionShownAt = 0;
		renderProvision(null, false);
	});

	function watchProvision() {
		provisionShownAt = Date.now();
		VS.every('provision', 3000, async () => {
			const data = await loadProvision();
			if (!data.running) { VS.stopTimer('provision'); await VS.loadInstances(); }
		}, {page: 'overview'});
	}

	// ---------- раздел ----------

	async function refreshAll() {
		await Promise.allSettled([VS.loadInstances(), VS.refreshStatus(), loadMetrics(), loadActivity(), loadNextMaintenance(), loadProvision().then(data => { if (data.running) watchProvision(); })]);
		stamp();
	}

	$('#overview-refresh').addEventListener('click', event => busy(event.currentTarget, refreshAll));
	$('#metrics-hours').addEventListener('change', () => loadMetrics().catch(fail));

	VS.on('instances', renderServers);
	VS.on('status', renderStatus);
	VS.on('telemetry', renderTelemetry);
	VS.on('session', greet);

	VS.pages.overview = {
		enter() {
			greet();
			renderServers();
			if (state.status) renderStatus(state.status);
			if (state.telemetry) renderTelemetry(state.telemetry);
			refreshAll();
			VS.every('overview-metrics', 60000, loadMetrics, {page: 'overview', now: false});
			VS.every('overview-activity', 30000, () => Promise.all([loadActivity(), loadNextMaintenance()]), {page: 'overview', now: false});
		},
	};
})();
