'use strict';

// Обзор: серверы, машина панели, состояние выбранного, показатели, ресурсы, здоровье, события, расписание,
// а также создание, переименование, «убрать из панели» и удаление серверов.

(() => {
	const {$, $$, esc, fmtBytes, fmtDuration, fmtDateTime, fmtShortTime, fmtDay, toDate, state, api, toast, fail, busy, dialog} = VS;

	const PLAYSTYLES = {surviveandbuild: I18N.t('overviewDynamic.o001'), exploration: I18N.t('overviewDynamic.o002'), wildernesssurvival: I18N.t('overviewDynamic.o003'), homosapiens: 'Homo Sapiens', creativebuilding: I18N.t('overviewDynamic.o004')};
	const LANGUAGES = {ru: I18N.t('overviewDynamic.o005'), en: 'English', de: 'Deutsch', fr: 'Français', es: 'Español', zh: '中文'};
	const STEPS = {catalogue: I18N.t('overviewDynamic.o006'), download: I18N.t('overviewDynamic.o007'), create: I18N.t('overviewDynamic.o008'), service: I18N.t('overviewDynamic.o009'), start: I18N.t('overviewDynamic.o010'), ready: I18N.t('overviewDynamic.o011'), rollback: I18N.t('overviewDynamic.o012')};
	const WEEKDAYS = [I18N.t('overviewDynamic.o013'), I18N.t('overviewDynamic.o014'), I18N.t('overviewDynamic.o015'), I18N.t('overviewDynamic.o016'), I18N.t('overviewDynamic.o017'), I18N.t('overviewDynamic.o018'), I18N.t('overviewDynamic.o019')];

	let metricsCurrent = null;

	// ---------- серверы ----------

	function badgeFor(item) {
		if (item.valid === false) return `${I18N.t('overviewDynamic.o020')}`;
		if (item.busy) return `${I18N.t('overviewDynamic.o021')}`;
		const health = item.watchdog?.state;
		if (health === 'ready') return `${I18N.t('overviewDynamic.o022')}`;
		if (!item.active && (health === 'stopped' || !health)) return `${I18N.t('overviewDynamic.o023')}`;
		const level = VS.healthLevel(health || 'unknown');
		return `<span class="badge ${level}"><span class="pulse-dot ${level}"></span> ${esc(VS.healthTitles[health] || (item.active ? I18N.t('overviewDynamic.o024') : I18N.t('overviewDynamic.o025')))}</span>`;
	}

	function serverCard(item) {
		const selected = item.id === state.current;
		const invalid = item.valid === false;
		const players = item.players === null || item.players === undefined ? '—' : item.players;
		const name = item.name || item.id;
		return `<button type="button" class="server-card${selected ? ' selected' : ''}${invalid ? ' invalid' : ''}" data-server="${esc(item.id)}" aria-pressed="${selected}" ${invalid ? 'disabled' : ''}>
			<span class="server-card-top"><span class="server-icon">${esc(name.slice(0, 1))}</span>${badgeFor(item)}</span>
			<strong>${esc(name)}</strong><small class="server-id">${esc(item.id)} · ${esc(item.game_version ? `Vintage Story ${item.game_version}` : I18N.t('overviewDynamic.o026'))}</small>
			<span class="server-card-rule"></span>
			<span class="server-card-stats"><span><b>${esc(players)}${I18N.t('overviewDynamic.o027')}${item.world_size ? esc(fmtBytes(item.world_size)) : '—'}${I18N.t('overviewDynamic.o028')}${esc(item.port || '—')}${I18N.t('overviewDynamic.o029')}${invalid ? `<span class="server-card-note">${esc(item.detail || I18N.t('overviewDynamic.o030'))}</span>` : (item.watchdog?.detail ? `<span class="server-card-note">${esc(item.watchdog.detail)}</span>` : '')}
			<span class="selected-label">${selected ? I18N.t('overviewDynamic.o031') : I18N.t('overviewDynamic.o032')} <svg><use href="#i-arrow"/></svg></span>
		</button>`;
	}

	function renderServers() {
		const items = state.instances;
		$('#servers-count').textContent = String(items.length).padStart(2, '0');
		$('#server-grid').innerHTML = items.map(serverCard).join('') || I18N.t('overviewDynamic.o033');
		const warning = $('#servers-warning');
		warning.textContent = state.instancesDetail ? `${I18N.t('overviewDynamic.o034')}${state.instancesDetail}` : '';
		warning.classList.toggle('hidden', !state.instancesDetail);
		renderSelected();
	}

	function renderSelected() {
		const server = VS.currentServer();
		$('#selected-name').textContent = server ? (server.name || server.id) : (state.current || I18N.t('overviewDynamic.o035'));
		$('#selected-address').textContent = server?.port ? `${location.hostname}:${server.port}` : '—';
		// Ответ проверки относится к серверу, для которого её запускали.
		if ($('#reachability-result').dataset.server !== (server?.id || '')) $('#reachability-result').classList.add('hidden');
		const locked = !server || state.instances.length < 2;
		$('#server-forget').disabled = locked;
		$('#server-delete').disabled = locked;
		const reason = locked ? I18N.t('overviewDynamic.o036') : '';
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
		try { await navigator.clipboard.writeText(text); toast(I18N.t('overviewDynamic.o037'), 'ok'); }
		catch { toast(`${I18N.t('overviewDynamic.o038')}${text}`); }
	});

	// Проверка из интернета: внешний сервис подключается к внешнему адресу машины по TCP.
	$('#check-reachability').addEventListener('click', async event => {
		const box = $('#reachability-result');
		try {
			const data = await busy(event.currentTarget, () => api('/api/reachability', {method: 'POST'}));
			const address = `${data.public_address}:${data.port}`;
			box.className = `notice ${data.reachable ? 'ok' : 'warn'}`;
			box.innerHTML = data.reachable
				? `<strong>${esc(I18N.t('overview.reachableTitle', {address}))}</strong><p>${esc(I18N.t('overview.reachableText'))}</p>`
				: `<strong>${esc(I18N.t('overview.unreachableTitle', {address}))}</strong><p>${esc(I18N.t('overview.unreachableText', {port: data.port}))}</p>`;
			box.dataset.server = VS.currentServer()?.id || '';
		} catch (error) {
			box.classList.add('hidden');
			fail(error);
		}
	});

	$('#server-rename').addEventListener('click', async () => {
		const server = VS.currentServer();
		if (!server) return;
		const result = await dialog({eyebrow: I18N.t('overviewDynamic.o039'), title: I18N.t('overviewDynamic.o040'), text: I18N.t('overviewDynamic.o041'), fields: [{label: I18N.t('overviewDynamic.o042'), value: server.name || server.id}], confirm: I18N.t('overviewDynamic.o043')});
		if (!result) return;
		try { await api('/api/instances/rename', {method: 'POST', body: {instance: server.id, name: result.values[0].trim()}}); toast(I18N.t('overviewDynamic.o044'), 'ok'); await VS.loadInstances(); }
		catch (error) { fail(error); }
	});

	$('#server-forget').addEventListener('click', async () => {
		const server = VS.currentServer();
		if (!server) return;
		const phrase = I18N.t('core.phraseForget', {name: server.id});
		const result = await dialog({eyebrow: I18N.t('overviewDynamic.o046'), title: I18N.t('overviewDynamic.o047'), text: I18N.t('overviewDynamic.o048'), phrase, confirm: I18N.t('overviewDynamic.o049'), danger: true});
		if (!result) return;
		try { await api('/api/instances/forget', {method: 'POST', body: {instance: server.id, confirmation: result.phrase}}); toast(`${I18N.t('overviewDynamic.o050')}${server.id}${I18N.t('overviewDynamic.o051')}`, 'ok'); location.reload(); }
		catch (error) { fail(error); }
	});

	$('#server-delete').addEventListener('click', async () => {
		const server = VS.currentServer();
		if (!server) return;
		const phrase = I18N.t('core.phraseDelete', {name: server.id});
		const result = await dialog({eyebrow: I18N.t('overviewDynamic.o053'), title: I18N.t('overviewDynamic.o054'), text: `${I18N.t('overviewDynamic.o055')}${server.name || server.id}${I18N.t('overviewDynamic.o056')}`, phrase, confirm: I18N.t('overviewDynamic.o057'), danger: true});
		if (!result) return;
		try { await api('/api/instances/delete', {method: 'POST', body: {instance: server.id, confirmation: result.phrase}}); toast(`${I18N.t('overviewDynamic.o058')}${server.id}${I18N.t('overviewDynamic.o059')}`, 'ok'); location.reload(); }
		catch (error) { fail(error); }
	});

	// ---------- служба ----------

	const serviceLabels = {start: I18N.t('overviewDynamic.o060'), restart: I18N.t('overviewDynamic.o061'), stop: I18N.t('overviewDynamic.o062')};
	$$('[data-service]').forEach(button => button.addEventListener('click', async () => {
		const action = button.dataset.service;
		if (action !== 'start' && !await VS.confirmAction(action === 'stop' ? I18N.t('overviewDynamic.o063') : I18N.t('overviewDynamic.o064'), I18N.t('overviewDynamic.o065'), action === 'stop' ? I18N.t('overviewDynamic.o066') : I18N.t('overviewDynamic.o067'), action === 'stop')) return;
		try {
			await busy(button, () => api('/api/service', {method: 'POST', body: {action}}));
			toast(`${serviceLabels[action]}${I18N.t('overviewDynamic.o068')}`, 'ok');
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
			? `${I18N.t('overviewDynamic.o069')}${fmtDuration(status.uptime)}${status.started ? `${I18N.t('overviewDynamic.o070')}${status.started}` : ''}`
			: `${I18N.t('overviewDynamic.o071')}${status.state || I18N.t('overviewDynamic.o072')} · ${status.substate || '—'}`;

		const banner = $('#status-banner');
		const level = VS.healthLevel(healthState);
		banner.classList.toggle('stopped', !active);
		banner.classList.toggle('problem', active && level === 'error');
		$('#status-emblem').textContent = !active ? '■' : (level === 'ok' ? '✳' : (level === 'warn' ? '◐' : '!'));
		$('#banner-title').textContent = !active ? I18N.t('overviewDynamic.o073') : ({ready: I18N.t('overviewDynamic.o074'), starting: I18N.t('overviewDynamic.o075'), degraded: I18N.t('overviewDynamic.o076'), unresponsive: I18N.t('overviewDynamic.o077'), hung: I18N.t('overviewDynamic.o078'), recovering: I18N.t('overviewDynamic.o079'), disabled: I18N.t('overviewDynamic.o080')}[healthState] || I18N.t('overviewDynamic.o081'));
		$('#banner-description').textContent = health.detail || (active ? I18N.t('overviewDynamic.o082') : I18N.t('overviewDynamic.o083'));
		$('#banner-dot').className = `pulse-dot ${active ? level : 'off'}`;
		$('#banner-state').textContent = !active ? I18N.t('overviewDynamic.o084') : (level === 'ok' ? I18N.t('overviewDynamic.o085') : (VS.healthTitles[healthState] || I18N.t('overviewDynamic.o086')));

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
		foot.className = `metric-foot ${available ? 'good' : 'warn'}`;
		foot.textContent = available ? (players.length ? `● ${players.map(player => player.name).slice(0, 3).join(', ')}${players.length > 3 ? '…' : ''}` : I18N.t('overviewDynamic.o087')) : (data?.detail || I18N.t('overviewDynamic.o088'));
		const target = data?.target_tick_ms ? 1000 / Number(data.target_tick_ms) : 0;
		$('#metric-tps').textContent = available ? Number(data.observed_tps || 0).toFixed(1) : '—';
		$('#metric-tps-target').textContent = target ? `/ ${target.toFixed(0)}` : '';
		$('#metric-tick').textContent = available ? `${Number(data.average_tick_gap_ms || 0).toFixed(1)}${I18N.t('overviewDynamic.o089')}` : '—';
		$('#tech-traffic').textContent = available ? `↓ ${fmtBytes(data.total_received_bytes)} · ↑ ${fmtBytes(data.total_sent_bytes)}` : '—';
		const nav = $('#nav-players');
		nav.hidden = !available;
		nav.textContent = players.length;
		renderWorld(available ? data : null);
		renderHealth();
	}

	// ---------- мир ----------

	// Календарь и живой World.Config из телеметрии мода; подписи настроек — из схемы мира, без неё код как есть.
	const WORLD_SETTINGS = [['gameMode', 'playerlives'], ['temporalStorms', 'worldClimate'], ['daysPerMonth', 'deathPunishment']];
	let worldSchema = null;

	const WORLD_PERIODS = {night: 'overview.worldNight', morning: 'overview.worldMorning', day: 'overview.worldDaytime', evening: 'overview.worldEvening'};
	const worldPeriod = hour => hour < 5 || hour >= 21 ? 'night' : (hour < 11 ? 'morning' : (hour < 17 ? 'day' : 'evening'));

	function worldSetting(configuration, code) {
		const key = Object.keys(configuration).find(name => name.toLowerCase() === code.toLowerCase());
		if (key == null) return null;
		const value = configuration[key];
		const attribute = (worldSchema?.attributes || []).find(item => item.code.toLowerCase() === code.toLowerCase());
		const text = typeof value === 'string' ? value : JSON.stringify(value);
		return {label: attribute?.label || key, value: attribute?.options?.find(option => option.value === String(value))?.label || text};
	}

	function renderWorld(data) {
		const world = data?.world;
		const chip = $('#world-chip');
		if (!world) {
			$('#world-date').textContent = data === null ? (state.telemetry?.detail || I18N.t('common.awaitingTelemetry')) : I18N.t('overview.worldUnknown');
			$('#world-grid').innerHTML = '';
			chip.textContent = '—';
			chip.className = 'status-chip';
			return;
		}
		const tiles = [];
		const hour = Number(world.hour_of_day);
		if (world.hour_of_day != null && Number.isFinite(hour)) {
			const minutes = Math.floor(hour * 60) % 1440;
			const time = `${String(Math.floor(minutes / 60)).padStart(2, '0')}:${String(minutes % 60).padStart(2, '0')}`;
			const period = worldPeriod(minutes / 60);
			tiles.push(hostTile(I18N.t('overview.worldTime'), time, '', minutes / 14.4, I18N.t(WORLD_PERIODS[period]), 'ok'));
			chip.textContent = `${period === 'night' ? '☾' : '☀'} ${I18N.t(WORLD_PERIODS[period])}`;
			chip.className = `status-chip ${period === 'night' ? '' : 'ok'}`;
		} else {
			chip.textContent = '—';
			chip.className = 'status-chip';
		}
		if (world.total_days != null) {
			const year = world.year == null ? '' : I18N.t('overview.worldYear', {year: world.year});
			tiles.push(hostTile(I18N.t('overview.worldDay'), String(Math.floor(Number(world.total_days)) + 1), '', null, year));
		}
		const configuration = world.configuration || {};
		for (const [main, extra] of WORLD_SETTINGS) {
			const setting = worldSetting(configuration, main);
			if (!setting) continue;
			const more = worldSetting(configuration, extra);
			tiles.push(hostTile(setting.label, setting.value, '', null, more ? `${more.label}: ${more.value}` : ''));
		}
		$('#world-date').textContent = world.pretty_date || I18N.t('overview.worldUnknown');
		$('#world-grid').innerHTML = tiles.join('');
		$$('#world-grid [data-width]').forEach(bar => { bar.style.width = `${bar.dataset.width}%`; });
	}

	// ---------- метрики ----------

	async function loadMetrics() {
		const data = await api(`/api/metrics?hours=${encodeURIComponent($('#metrics-hours').value)}`);
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
			$('#world-name').textContent = current.current_world?.name || current.current_world?.save || '—';
			$('#tech-cpu').textContent = `${Number(current.cpu_percent || 0).toFixed(1)}%`;
			const backup = current.last_backup;
			const when = backup ? toDate(backup.modified) : null;
			$('#metric-backup').textContent = when ? fmtShortTime(backup.modified) : I18N.t('overviewDynamic.o090');
			$('#metric-backup-day').textContent = when ? fmtDay(backup.modified) : '';
			const age = when ? (Date.now() - when.getTime()) / 3600000 : Infinity;
			const foot = $('#metric-backup-foot');
			foot.className = `metric-foot ${age < 30 ? 'good' : 'warn'}`;
			foot.textContent = !when ? I18N.t('overviewDynamic.o091') : (age < 30 ? `● ${fmtBytes(backup.size || 0)}` : I18N.t('overviewDynamic.o092'));
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
			empty: I18N.t('overviewDynamic.o093'),
		});
		renderHealth();
	}

	// ---------- машина ----------

	// Машина одна на все серверы: карточка не зависит от выбранного. Цвет — по занятой доле;
	// процессор в оценку карточки не входит, его всплеск за секунду замера тревогой не считается.
	const HOST_USES = {
		panel: () => I18N.t('overview.hostUsePanel'),
		data: name => I18N.t('overview.hostUseData', {name}),
		backups: name => I18N.t('overview.hostUseBackups', {name}),
	};
	const hostLevel = percent => percent >= 90 ? 'error' : (percent >= 75 ? 'warn' : 'ok');

	function hostTile(label, value, sub, percent, foot, level) {
		const bar = percent == null ? '' : `<div class="host-track"><span class="${level || hostLevel(percent)}" data-width="${Math.min(100, Math.max(0, percent)).toFixed(1)}"></span></div>`;
		return `<div class="host-tile"><div class="host-tile-label" title="${esc(label)}">${esc(label)}</div><div class="host-tile-value">${esc(value)}${sub ? ` <small>${esc(sub)}</small>` : ''}</div>${bar}<div class="host-tile-foot">${esc(foot || '')}</div></div>`;
	}

	async function loadHost() {
		const data = await api('/api/host');
		const tiles = [], levels = [];
		const cpu = data.cpu_percent;
		const load = Array.isArray(data.load) ? I18N.t('overview.hostLoad', {load: data.load.map(value => Number(value).toFixed(2)).join(' · ')}) : '';
		tiles.push(hostTile('CPU', cpu == null ? '—' : `${Number(cpu).toFixed(0)}%`, I18N.t('overview.hostCores', {count: data.cpus}), cpu, load));
		if (data.memory_total) {
			const used = data.memory_total - data.memory_available;
			const percent = used / data.memory_total * 100;
			levels.push(hostLevel(percent));
			tiles.push(hostTile(I18N.t('common.memory'), fmtBytes(used), `/ ${fmtBytes(data.memory_total)}`, percent, I18N.t('overview.hostAvailable', {size: fmtBytes(data.memory_available)})));
		}
		if (data.swap_total) {
			tiles.push(hostTile(I18N.t('overview.hostSwap'), fmtBytes(data.swap_used), `/ ${fmtBytes(data.swap_total)}`, data.swap_used / data.swap_total * 100, ''));
		}
		for (const volume of data.volumes || []) {
			const percent = volume.total ? (volume.total - volume.free) / volume.total * 100 : 0;
			levels.push(hostLevel(percent));
			const uses = (volume.uses || []).map(use => HOST_USES[use.kind]?.(use.server) || use.kind).join(', ');
			tiles.push(hostTile(I18N.t('overview.hostDisk', {mount: volume.mount}), fmtBytes(volume.free), I18N.t('overview.hostFreeOf', {size: fmtBytes(volume.total)}), percent, uses));
		}
		$('#host-grid').innerHTML = tiles.join('');
		// Ширину задаёт скрипт: атрибут style запрещён политикой CSP.
		$$('#host-grid [data-width]').forEach(bar => { bar.style.width = `${bar.dataset.width}%`; });

		$('#host-name').textContent = data.hostname || I18N.t('overview.hostTitle');
		$('#host-detail').textContent = [data.platform, I18N.t('overview.hostUptime', {time: fmtDuration(data.uptime)}), I18N.t('overview.hostPanelMemory', {size: fmtBytes(data.panel_memory)})].filter(Boolean).join(' · ');
		const worst = levels.includes('error') ? 'error' : (levels.includes('warn') ? 'warn' : 'ok');
		const chip = $('#host-chip');
		chip.textContent = I18N.t({ok: 'overview.hostOk', warn: 'overview.hostWarn', error: 'overview.hostError'}[worst]);
		chip.className = `status-chip ${worst}`;
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
			add(status.active ? 'ok' : 'warn', status.active ? '✓' : '■', I18N.t('overviewDynamic.o094'), status.active ? `${I18N.t('overviewDynamic.o095')}${status.pid || '—'}` : `${status.state || '—'} · ${status.substate || '—'}`, status.active ? I18N.t('overviewDynamic.o096') : I18N.t('overviewDynamic.o097'));
			const health = status.health?.state || 'unknown';
			const level = VS.healthLevel(health);
			add(level, level === 'ok' ? '✓' : '!', I18N.t('overviewDynamic.o098'), status.health?.detail || I18N.t('overviewDynamic.o099'), VS.healthTitles[health] || health);
		}
		if (telemetry) {
			const fresh = telemetry.available && !telemetry.stale;
			add(fresh ? 'ok' : 'warn', fresh ? '✓' : '!', I18N.t('overviewDynamic.o100'), fresh ? `${I18N.t('overviewDynamic.o101')}${telemetry.age_seconds != null ? `${telemetry.age_seconds}${I18N.t('overviewDynamic.o102')}` : I18N.t('overviewDynamic.o103')}` : (telemetry.detail || I18N.t('overviewDynamic.o104')), fresh ? I18N.t('overviewDynamic.o105') : I18N.t('overviewDynamic.o106'));
		}
		if (metrics) {
			add(metrics.disk_warning ? 'error' : 'ok', metrics.disk_warning ? '!' : '✓', I18N.t('overviewDynamic.o107'), `${fmtBytes(metrics.disk_free)}${metrics.disk_total ? `${I18N.t('overviewDynamic.o108')}${fmtBytes(metrics.disk_total)}` : ''}`, metrics.disk_warning ? I18N.t('overviewDynamic.o109') : I18N.t('overviewDynamic.o110'));
		}
		const problems = recentProblems.length;
		add(problems ? 'warn' : 'ok', problems ? '!' : '✓', I18N.t('overviewDynamic.o111'), problems ? `${problems}${I18N.t('overviewDynamic.o112')}` : I18N.t('overviewDynamic.o113'), problems ? I18N.t('overviewDynamic.o114') : I18N.t('overviewDynamic.o115'));
		$('#health-list').innerHTML = rows.join('');
		const percent = total ? Math.round(score / total * 100) : 0;
		const chip = $('#health-chip');
		chip.textContent = total ? `${percent} / 100` : '—';
		chip.className = `status-chip ${percent >= 90 ? 'ok' : (percent >= 60 ? 'warn' : 'error')}`;
	}

	// ---------- живая лента ----------

	// Раздел по началу вида события: подпись строки и куда ведёт щелчок. Пустой адрес — события
	// некуда вести, строка не кликается.
	const FEED_GROUPS = {
		player: [I18N.t('overviewDynamic.o116'), 'players:activity'],
		server: [I18N.t('overviewDynamic.o117'), 'logs:stream'],
		watchdog: [I18N.t('overviewDynamic.o118'), 'maintenance:watchdog'],
		automation: [I18N.t('overviewDynamic.o119'), 'maintenance:schedule'],
		update: [I18N.t('overviewDynamic.o120'), 'maintenance:updates'],
		provision: [I18N.t('overviewDynamic.o121'), ''],
		admin: [I18N.t('overviewDynamic.o122'), 'logs:events'],
		config: [I18N.t('overviewDynamic.o123'), 'settings:server'],
		world: [I18N.t('overviewDynamic.o124'), 'settings:world'],
		backup: [I18N.t('overviewDynamic.o125'), 'backups:list'],
		disk: [I18N.t('overviewDynamic.o126'), ''],
		notification: [I18N.t('overviewDynamic.o127'), 'settings:notifications'],
		history: [I18N.t('overviewDynamic.o128'), ''],
	};
	const FEED_EXACT = {'server.long_tick': 'logs:performance', 'player.ping_critical': 'players:quality'};
	const FEED_IMPORTANT = ['warning', 'error', 'critical'];
	const FEED_FILTERS = {
		all: () => true,
		important: item => FEED_IMPORTANT.includes(item.severity),
		players: item => String(item.kind || '').startsWith('player.'),
		system: item => !String(item.kind || '').startsWith('player.'),
	};
	const FEED_SHORT = 6, FEED_LONG = 20;
	const FEED_FILTER_KEY = 'vs-feed-filter';

	let feedEvents = [];
	let feedFilter = (() => { try { return FEED_FILTERS[localStorage.getItem(FEED_FILTER_KEY)] ? localStorage.getItem(FEED_FILTER_KEY) : 'all'; } catch { return 'all'; } })();
	let feedExpanded = false;
	// Ключи уже показанных событий: новое подсвечивается, первая отрисовка — нет.
	let feedSeen = null;

	const feedGroup = kind => FEED_GROUPS[String(kind || '').split('.')[0]] || [I18N.t('overviewDynamic.o129'), ''];
	const feedKey = item => item.id || `${item.ts}|${item.kind}|${item.title}`;

	/** Подряд идущие одинаковые события — одна строка со счётчиком: десять входов одного игрока не вытесняют остальное. */
	function collapse(items) {
		const rows = [];
		for (const item of items) {
			const last = rows[rows.length - 1];
			if (last && last.kind === item.kind && last.title === item.title && last.severity === item.severity) {
				last.count += 1;
				last.keys.push(feedKey(item));
				continue;
			}
			rows.push({...item, count: 1, keys: [feedKey(item)]});
		}
		return rows;
	}

	function feedTime(ts) {
		const day = fmtDay(ts);
		if (day === I18N.t('overviewDynamic.o130')) return VS.fmtAgo(ts);
		return day === I18N.t('overviewDynamic.o131') ? `${I18N.t('overviewDynamic.o132')}${fmtShortTime(ts)}` : fmtDateTime(ts);
	}

	function feedRow(row, fresh) {
		const [group, fallback] = feedGroup(row.kind);
		const target = FEED_EXACT[row.kind] ?? fallback;
		const tag = target ? 'button' : 'div';
		const attributes = target ? ` type="button" data-goto="${esc(target)}${I18N.t('overviewDynamic.o133')}${esc(group)}"` : '';
		const repeat = row.count > 1 ? ` <b class="feed-repeat">×${row.count}</b>` : '';
		const detail = row.detail ? `<small>${esc(row.detail)}</small>` : '';
		return `<${tag} class="feed-row${fresh ? ' fresh' : ''}"${attributes}><span class="activity-dot ${esc(row.severity || 'info')}"></span><span><strong>${esc(row.title || row.kind)}${repeat}</strong>${detail}<em class="feed-group">${esc(group)}</em></span><time datetime="${esc(VS.toDate(row.ts)?.toISOString() || '')}" title="${esc(fmtDateTime(row.ts))}">${esc(feedTime(row.ts))}</time></${tag}>`;
	}

	function renderFeed() {
		const important = feedEvents.filter(FEED_FILTERS.important).filter(item => (item.ts || 0) > Date.now() / 1000 - 86400).length;
		const count = $('#feed-important-count');
		count.hidden = !important;
		count.textContent = important > 9 ? '9+' : String(important);
		$$('#feed-filters [data-feed]').forEach(button => button.classList.toggle('active', button.dataset.feed === feedFilter));

		const rows = collapse(feedEvents.filter(FEED_FILTERS[feedFilter]));
		const shown = rows.slice(0, feedExpanded ? FEED_LONG : FEED_SHORT);
		const known = feedSeen;
		$('#activity-list').innerHTML = shown.map(row => feedRow(row, known !== null && row.keys.some(key => !known.has(key)))).join('')
			|| `<div class="empty">${feedEvents.length ? I18N.t('overviewDynamic.o134') : I18N.t('overviewDynamic.o135')}</div>`;
		feedSeen = new Set(feedEvents.map(feedKey));

		const more = $('#feed-more');
		more.hidden = rows.length <= FEED_SHORT;
		more.textContent = feedExpanded ? I18N.t('overviewDynamic.o136') : `${I18N.t('overviewDynamic.o137')}${Math.min(rows.length, FEED_LONG) - FEED_SHORT}`;
	}

	function takeEvents(events) {
		feedEvents = Array.isArray(events) ? events : [];
		const dayAgo = Date.now() / 1000 - 86400;
		recentProblems = feedEvents.filter(item => FEED_IMPORTANT.includes(item.severity) && (item.ts || 0) > dayAgo);
		renderFeed();
		renderHealth();
	}

	async function loadActivity() {
		const data = await api('/api/events?limit=50&severity=all');
		takeEvents(data.events || []);
	}

	/** Поток отдаёт последние 50 событий целиком, когда лента изменилась. */
	function watchFeed() {
		const live = $('#feed-live');
		VS.stream('overview-feed', '/api/events/stream', {
			page: 'overview',
			onMessage: takeEvents,
			onOpen: () => { live.hidden = false; },
			onError: () => { live.hidden = true; },
		});
	}

	$('#feed-filters').addEventListener('click', event => {
		const button = event.target.closest('[data-feed]');
		if (!button || button.dataset.feed === feedFilter) return;
		feedFilter = button.dataset.feed;
		feedExpanded = false;
		try { localStorage.setItem(FEED_FILTER_KEY, feedFilter); } catch { /* приватный режим */ }
		renderFeed();
	});

	$('#feed-more').addEventListener('click', () => {
		feedExpanded = !feedExpanded;
		renderFeed();
	});

	// ---------- расписание ----------

	async function loadNextMaintenance() {
		const data = await api('/api/automation');
		const jobs = (data.jobs || []).filter(job => job.enabled && job.next_run).sort((a, b) => a.next_run - b.next_run);
		const box = $('#next-maintenance');
		const running = data.running;
		if (running) {
			box.innerHTML = `${I18N.t('overviewDynamic.o138')}${esc(running.name)}${I18N.t('overviewDynamic.o139')}${esc(fmtDateTime(running.started_at))}.</div>`;
			return;
		}
		const job = jobs[0];
		if (!job) { box.innerHTML = I18N.t('overviewDynamic.o140'); return; }
		const when = toDate(job.next_run);
		const steps = [job.warning_minutes?.length ? I18N.t('overviewDynamic.o141') : '', job.backup ? I18N.t('overviewDynamic.o142') : '', job.restart ? I18N.t('overviewDynamic.o143') : '', I18N.t('overviewDynamic.o144')].filter(Boolean);
		box.innerHTML = `<div class="next-date"><b>${when.getDate()}</b><span>${esc(when.toLocaleDateString('ru-RU', {month: 'long'}))}<br><strong>${esc(WEEKDAYS[(when.getDay() + 6) % 7])}, ${esc(fmtShortTime(job.next_run))}</strong></span></div>
			<div class="next-detail">${esc(job.name)} · ${steps.map(esc).join(' <span class="accent">→</span> ')}</div>
			<div class="next-note">${job.warning_minutes?.length ? `${I18N.t('overviewDynamic.o145')}${esc(job.warning_minutes.join(', '))}${I18N.t('overviewDynamic.o146')}` : I18N.t('overviewDynamic.o147')}</div>`;
	}

	function stamp() { $('#overview-updated').textContent = `${I18N.t('overviewDynamic.o148')}${VS.fmtTime(Date.now() / 1000)}`; }

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
			$('#new-version').innerHTML = stable.map(item => `<option value="${esc(item.version)}">${esc(item.version)}${item.latest ? I18N.t('overviewDynamic.o149') : ''} · ${esc(item.size_text || '')}</option>`).join('') || I18N.t('overviewDynamic.o150');
			if (data.stale) toast(I18N.t('overviewDynamic.o151'));
		} catch (error) { $('#create-error').textContent = `${I18N.t('overviewDynamic.o152')}${error.message}`; }
	});
	let serversRoot = '';
	/** Где окажется сервер: каталог серверов и идентификатор — так же выводит путь демон. */
	function showLocation() {
		if (!serversRoot) return;
		const separator = /^[A-Za-z]:\\/.test(serversRoot) ? '\\' : '/';
		$('#create-location').textContent = `${I18N.t('overviewDynamic.o153')}${serversRoot}${separator}${$('#new-id').value.trim() || '…'}${I18N.t('overviewDynamic.o154')}`;
	}
	$('#new-id').addEventListener('input', showLocation);
	$$('#create-dialog [data-close]').forEach(button => button.addEventListener('click', () => $('#create-dialog').close()));

	function renderPreflight(report) {
		const rows = [];
		const runtime = report.runtime || {};
		rows.push(`<div class="notice ${runtime.dotnet ? 'ok' : 'error'}${I18N.t('overviewDynamic.o155')}${esc(runtime.dotnet || I18N.t('overviewDynamic.o156'))}</div>`);
		if (report.free !== undefined) rows.push(`<div class="notice ${report.free >= (report.required_free || 0) ? 'ok' : 'error'}${I18N.t('overviewDynamic.o157')}${esc(fmtBytes(report.free))}${I18N.t('overviewDynamic.o158')}${esc(fmtBytes(report.required_free))}</div>`);
		(report.blockers || []).forEach(item => rows.push(`<div class="notice error">${esc(item)}</div>`));
		(report.warnings || []).forEach(item => rows.push(`<div class="notice warn">${esc(item)}</div>`));
		if (report.ok) rows.push(I18N.t('overviewDynamic.o159'));
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
				language: $('#new-language').value, world_name: $('#new-world').value.trim() || I18N.t('overviewDynamic.o160'), seed: $('#new-seed').value.trim(), playstyle: $('#new-playstyle').value,
			}}));
			$('#create-dialog').close();
			toast(I18N.t('overviewDynamic.o161'), 'ok');
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
		$('#provision-title').textContent = record.instance ? `${I18N.t('overviewDynamic.o162')}${record.instance}»${record.target_version ? ` · ${record.target_version}` : ''}` : (record.title || I18N.t('overviewDynamic.o163'));
		$('#provision-detail').textContent = record.detail || (running ? I18N.t('overviewDynamic.o164') : '');
		const chip = $('#provision-state');
		const status = running ? 'running' : record.status;
		chip.textContent = {running: I18N.t('overviewDynamic.o165'), success: I18N.t('overviewDynamic.o166'), failed: I18N.t('overviewDynamic.o167'), interrupted: I18N.t('overviewDynamic.o168')}[status] || status || '—';
		chip.className = `status-chip ${status === 'success' ? 'ok' : (status === 'running' ? 'warn' : 'error')}`;
		const order = ['catalogue', 'download', 'create', 'service', 'start', 'ready'];
		$('#provision-steps').innerHTML = order.map(action => {
			const step = (record.steps || []).find(item => item.action === action);
			const cls = step ? (step.status === 'ok' ? 'done' : step.status) : (running && !(record.steps || []).length && action === 'catalogue' ? 'running' : '');
			return `<li class="${esc(cls)}"><span>${esc(STEPS[action])}${step?.detail ? `<small>${esc(step.detail)}</small>` : ''}</span></li>`;
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
		await Promise.allSettled([VS.loadInstances(), VS.refreshStatus(), loadMetrics(), loadHost(), loadActivity(), loadNextMaintenance(), loadProvision().then(data => { if (data.running) watchProvision(); })]);
		stamp();
	}

	$('#overview-refresh').addEventListener('click', event => busy(event.currentTarget, refreshAll));
	$('#metrics-hours').addEventListener('change', () => loadMetrics().catch(fail));

	VS.on('instances', renderServers);
	VS.on('status', renderStatus);
	VS.on('telemetry', renderTelemetry);

	VS.pages.overview = {
		enter() {
			renderServers();
			if (state.status) renderStatus(state.status);
			if (state.telemetry) renderTelemetry(state.telemetry);
			if (!worldSchema) VS.loadWorldSchema().then(schema => { worldSchema = schema; if (state.telemetry) renderTelemetry(state.telemetry); }).catch(() => {});
			refreshAll();
			VS.every('overview-metrics', 60000, loadMetrics, {page: 'overview', now: false});
			VS.every('overview-host', 15000, loadHost, {page: 'overview', now: false});
			watchFeed();
			// Относительное время («5 мин назад») стареет и без новых событий.
			VS.every('overview-feed-clock', 30000, renderFeed, {page: 'overview', now: false});
			VS.every('overview-activity', 60000, loadNextMaintenance, {page: 'overview', now: false});
		},
	};
})();
