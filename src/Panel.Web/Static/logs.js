'use strict';

// Журнал и события: живой журнал сервера, журнал службы панели, диагностика причин,
// производительность и лента событий панели.

(() => {
	const {$, esc, fmtDateTime, fmtShortTime, fmtTime, api, fail, busy} = VS;

	let logEvents = [];
	let searchTimer = null;
	let eventTimer = null;

	// ---------- журнал сервера ----------

	const logQuery = () => new URLSearchParams({lines: $('#log-lines').value, category: $('#log-category').value, search: $('#log-search').value.trim()}).toString();

	function renderLog() {
		const box = $('#log-window');
		const bottom = box.scrollHeight - box.scrollTop - box.clientHeight < 60;
		box.textContent = '';
		const fragment = document.createDocumentFragment();
		for (const event of logEvents) {
			const line = document.createElement('div');
			line.className = `log-${event.category || 'system'}`;
			line.textContent = event.line;
			fragment.append(line);
		}
		box.append(fragment);
		if (!logEvents.length) box.innerHTML = `<div class="log-system">${esc(I18N.t('logsDynamic.noLines'))}</div>`;
		if (bottom) box.scrollTop = box.scrollHeight;
	}

	function setLogState(level, text) { $('#log-dot').className = `pulse-dot ${level}`; $('#log-state').textContent = text; }

	async function loadLog() {
		const data = await api(`/api/logs?${logQuery()}`);
		logEvents = data.events || [];
		renderLog();
		const crashes = data.crashes || {};
		const warning = $('#crash-warning');
		warning.classList.toggle('hidden', !crashes.count);
		warning.textContent = crashes.repeated ? I18N.t('logsDynamic.repeatedCrashes', {count: crashes.count}) : (crashes.count ? I18N.t('logsDynamic.crashSign', {detail: crashes.last}) : '');
		const query = new URLSearchParams({category: $('#log-category').value, search: $('#log-search').value.trim()});
		setLogState('warn', I18N.t('logsDynamic.connectingStream'));
		VS.stream('log', `/api/logs/stream?${query}`, {
			page: 'logs',
			onOpen: () => setLogState('ok', I18N.t('logsDynamic.streamConnected')),
			onError: () => setLogState('warn', I18N.t('logsDynamic.reconnecting')),
			onMessage: item => {
				if (item.stream_error) { setLogState('error', I18N.t('logsDynamic.streamError', {detail: item.stream_error})); return; }
				logEvents.push(item);
				const limit = Number($('#log-lines').value);
				if (logEvents.length > limit) logEvents.splice(0, logEvents.length - limit);
				renderLog();
			},
		});
	}

	$('#logs-refresh').addEventListener('click', event => busy(event.currentTarget, loadLog).catch(fail));
	$('#log-category').addEventListener('change', () => loadLog().catch(fail));
	$('#log-lines').addEventListener('change', () => loadLog().catch(fail));
	$('#log-search').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => loadLog().catch(fail), 400); });
	$('#logs-download').addEventListener('click', () => { const link = document.createElement('a'); link.href = `/api/logs/download?${logQuery()}`; link.click(); });

	// ---------- журнал панели ----------

	async function loadPanelLog() {
		const data = await api(`/api/panel/logs?lines=${encodeURIComponent($('#panel-log-lines').value)}`);
		const box = $('#panel-log');
		box.textContent = (data.lines || []).join('\n') || data.detail || I18N.t('logsDynamic.noServiceEntries');
		box.scrollTop = box.scrollHeight;
	}
	$('#panel-log-refresh').addEventListener('click', event => busy(event.currentTarget, loadPanelLog).catch(fail));
	$('#panel-log-lines').addEventListener('change', () => loadPanelLog().catch(fail));

	// ---------- диагностика ----------

	const levelOf = level => ({ok: 'ok', warn: 'warn', warning: 'warn', error: 'error', critical: 'error'}[level] || 'warn');
	const row = (level, title, detail, evidence = '', timestamp = null) => `<div><span class="health-icon ${levelOf(level)}">${levelOf(level) === 'ok' ? '✓' : '!'}</span><span><strong>${esc(title)}</strong><small>${esc(detail)}${timestamp ? ` · ${esc(fmtDateTime(timestamp))}` : ''}</small>${evidence ? `<small class="mono">${esc(evidence)}</small>` : ''}</span></div>`;
	const group = (title, rows) => `<h3 class="subheading">${esc(title)}</h3><div class="health-list">${rows.join('')}</div>`;

	function configRow(apply) {
		const keys = Array.isArray(apply?.changed_keys) && apply.changed_keys.length ? I18N.t('logsDynamic.changedKeys', {keys: apply.changed_keys.join(', ')}) : '';
		if (apply?.state === 'applied') return row('ok', I18N.t('logsDynamic.configApplied'), I18N.t('logsDynamic.configAppliedDetail') + keys);
		if (apply?.state === 'restart_required') return row('warn', I18N.t('logsDynamic.restartRequired'), I18N.t('logsDynamic.restartRequiredDetail') + keys);
		if (apply?.state === 'start_required') return row('warn', I18N.t('logsDynamic.startRequired'), I18N.t('logsDynamic.startRequiredDetail') + keys);
		return row('error', I18N.t('logsDynamic.configUnknown'), apply?.detail || I18N.t('logsDynamic.configUnknownDetail'));
	}

	async function loadDiagnostics() {
		const box = $('#diagnostics-body');
		box.innerHTML = `<div class="empty">${esc(I18N.t('logsDynamic.collectingDiagnostics'))}</div>`;
		try {
			const data = await api('/api/diagnostics');
			const status = data.status || {}, metrics = data.metrics || {}, watchdog = data.watchdog || {}, causes = data.causes || {};
			const startup = causes.startup || {level: status.active ? 'warn' : 'error', title: I18N.t(status.active ? 'logsDynamic.serverStarting' : 'logsDynamic.gameServiceStopped'), detail: `${status.state || '—'} · ${status.substate || '—'}`};
			const connections = (causes.connections || []).map(item => row(item.level || 'error', item.title || I18N.t('logsDynamic.connectionRejected'), item.detail || '', item.evidence || '', item.timestamp));
			const problems = (data.recent_events || []).filter(item => ['warning', 'error', 'critical'].includes(item.severity)).slice(0, 5);
			box.innerHTML = [
				group(I18N.t('logsDynamic.serverStartup'), [row(startup.level || 'warn', startup.title || I18N.t('logsDynamic.startUnknown'), startup.detail || '', startup.evidence || '', startup.timestamp)]),
				group(I18N.t('logsDynamic.configApplication'), [configRow(data.config_apply)]),
				group(I18N.t('logsDynamic.playerConnections'), connections.length ? connections : [row('ok', I18N.t('logsDynamic.noLoginRejections'), I18N.t('logsDynamic.noLoginRejectionsDetail'))]),
				group(I18N.t('logsDynamic.systemAndEvents'), [
					row(VS.healthLevel(watchdog.state || 'unknown'), I18N.t('logsDynamic.watchdogTitle', {state: VS.healthTitles[watchdog.state] || watchdog.state || I18N.t('logsDynamic.noData')}), watchdog.detail || I18N.t('logsDynamic.notChecked')),
					row(metrics.disk_warning ? 'error' : 'ok', I18N.t(metrics.disk_warning ? 'logsDynamic.diskLow' : 'logsDynamic.diskOk'), I18N.t('logsDynamic.diskFree', {free: VS.fmtBytes(metrics.disk_free || 0)})),
					...(causes.journal_error ? [row('error', I18N.t('logsDynamic.serverLogUnavailable'), causes.journal_error)] : []),
					...problems.map(item => row(item.severity, item.title || item.kind, item.detail || item.source || '', '', item.ts)),
				]),
			].join('');
		} catch (error) { box.innerHTML = `<div class="notice error">${esc(I18N.t('logsDynamic.diagnosticsUnavailable', {detail: error.message}))}</div>`; }
	}
	$('#diagnostics-refresh').addEventListener('click', event => busy(event.currentTarget, loadDiagnostics));

	// ---------- производительность ----------

	async function loadPerformance() {
		const data = await api(`/api/performance?minutes=${encodeURIComponent($('#performance-minutes').value)}`);
		const support = $('#performance-support');
		support.classList.toggle('hidden', !!data.supported);
		support.className = `notice warn${data.supported ? ' hidden' : ''}`;
		support.textContent = I18N.t('logsDynamic.noLongTicksSupport');
		const series = data.series || [];
		const gaps = series.map(point => Number(point.max_gap_ms) || 0), tps = series.map(point => Number(point.tps) || 0);
		$('#perf-long-ticks').textContent = (data.long_ticks || []).length;
		$('#perf-worst').textContent = gaps.length ? `${Math.max(...gaps).toFixed(0)} ${I18N.t('units.ms')}` : '—';
		$('#perf-min-tps').textContent = tps.length ? Math.min(...tps).toFixed(1) : '—';
		$('#perf-spikes').textContent = (data.ping_spikes || []).length;
		$('#perf-overloads').textContent = (data.overloads || []).length;
		$('#performance-overloads').textContent = (data.overloads || []).join('\n') || I18N.t('logsDynamic.noOverloads');
		$('#performance-mods').textContent = (data.mod_errors || []).join('\n') || I18N.t('logsDynamic.noModErrors');

		const stamps = series.map(point => point.ts);
		const nearest = ts => { let best = 0; stamps.forEach((stamp, index) => { if (Math.abs(stamp - ts) < Math.abs(stamps[best] - ts)) best = index; }); return best; };
		const threshold = Number(data.threshold_ms) || 0;
		const max = Math.max(100, threshold * 1.2, ...gaps);
		VS.lineChart($('#performance-chart'), {
			series: [{values: gaps, cls: 'tick', area: false}, ...(threshold ? [{values: gaps.map(() => threshold), cls: 'threshold'}] : [])],
			max, unit: ` ${I18N.t('units.ms')}`, labels: VS.timeLabels(stamps),
			marks: stamps.length ? [...(data.long_ticks || []).map(item => ({index: nearest(item.ts)})), ...(data.ping_spikes || []).map(item => ({index: nearest(item.ts), cls: 'ping'}))] : [],
			empty: I18N.t('logsDynamic.notEnoughMeasurements'),
		});

		const entries = [
			...(data.long_ticks || []).map(item => ({ts: item.ts, kind: 'error', label: I18N.t('logsDynamic.tick'), text: I18N.t('logsDynamic.longTick', {ms: Number(item.gap_ms).toFixed(0)})})),
			...(data.ping_spikes || []).map(item => ({ts: item.ts, kind: 'warn', label: 'Ping', text: `${item.player} — ${Number(item.ping_ms).toFixed(0)} ${I18N.t('units.ms')}`})),
			...(data.player_events || []).filter(event => event.timestamp && event.player_event).map(event => ({ts: event.timestamp, kind: event.player_event.type === 'join' ? 'join' : 'leave', label: I18N.t(event.player_event.type === 'join' ? 'logsDynamic.join' : 'logsDynamic.leave'), text: event.player_event.player})),
		].sort((a, b) => b.ts - a.ts).slice(0, 150);
		$('#performance-timeline').innerHTML = entries.map(entry => `<div><time>${esc(fmtTime(entry.ts))}</time><span class="event-kind ${esc(entry.kind)}">${esc(entry.label)}</span><span>${esc(entry.text)}</span></div>`).join('') || `<div class="empty">${esc(I18N.t('logsDynamic.noNotableEvents'))}</div>`;
	}
	$('#performance-refresh').addEventListener('click', event => busy(event.currentTarget, loadPerformance).catch(fail));
	$('#performance-minutes').addEventListener('change', () => loadPerformance().catch(fail));

	// ---------- события панели ----------

	const SEVERITY = {info: I18N.t('logsDynamic.info'), warning: I18N.t('logsDynamic.attention'), error: I18N.t('logsDynamic.error'), critical: I18N.t('logsDynamic.critical')};
	const eventRow = item => `<div><time>${esc(VS.fmtDay(item.ts) === I18N.t('common.today') ? fmtShortTime(item.ts) : fmtDateTime(item.ts))}</time><span class="event-kind ${esc(item.severity || 'info')}">${esc(SEVERITY[item.severity] || item.severity || '—')}</span><span><strong>${esc(item.title || item.kind)}</strong> ${esc(item.detail || '')}<small>${esc(item.source || 'panel')} · ${esc(item.kind || '')}</small></span></div>`;

	async function loadEvents() {
		const data = await api(`/api/events?limit=150&severity=${encodeURIComponent($('#event-severity').value)}&search=${encodeURIComponent($('#event-search').value.trim())}`);
		$('#panel-events').innerHTML = (data.events || []).map(eventRow).join('') || `<div class="empty">${esc(I18N.t('logsDynamic.noEvents'))}</div>`;
		const filtered = $('#event-severity').value !== 'all' || $('#event-search').value.trim();
		// Поток отдаёт ленту без фильтра, поэтому при фильтре она обновляется запросом.
		if (filtered) { VS.closeStream('events'); return; }
		VS.stream('events', '/api/events/stream', {page: 'logs', onMessage: items => { if (Array.isArray(items)) $('#panel-events').innerHTML = items.map(eventRow).join('') || `<div class="empty">${esc(I18N.t('logsDynamic.noEvents'))}</div>`; }});
	}
	$('#events-refresh').addEventListener('click', event => busy(event.currentTarget, loadEvents).catch(fail));
	$('#event-severity').addEventListener('change', () => loadEvents().catch(fail));
	$('#event-search').addEventListener('input', () => { clearTimeout(eventTimer); eventTimer = setTimeout(() => loadEvents().catch(fail), 350); });

	// ---------- раздел ----------

	VS.pages.logs = {
		enter(tab) { this.tab(tab); },
		tab(view) {
			if (view !== 'stream') VS.closeStream('log');
			if (view !== 'events') VS.closeStream('events');
			const loaders = {stream: loadLog, panel: loadPanelLog, diagnostics: loadDiagnostics, performance: loadPerformance, events: loadEvents};
			(loaders[view] || loadLog)().catch(fail);
		},
	};
})();
