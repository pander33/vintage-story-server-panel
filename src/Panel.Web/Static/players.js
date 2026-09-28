'use strict';

// Игроки и консоль: подключения, каталог команд и произвольная консоль (ADR-17), действия
// над игроками, объявления, чат и события мира, история ping.

(() => {
	const {$, $$, esc, fmtTime, fmtShortTime, state, api, toast, fail, busy} = VS;

	const QUALITY = {excellent: I18N.t('playersDynamic.p001'), good: I18N.t('playersDynamic.p002'), unstable: I18N.t('playersDynamic.p003'), poor: I18N.t('playersDynamic.p004'), unavailable: I18N.t('playersDynamic.p005')};
	const CHAT_KINDS = {chat: I18N.t('playersDynamic.p006'), join: I18N.t('playersDynamic.p007'), leave: I18N.t('playersDynamic.p008'), death: I18N.t('playersDynamic.p009'), respawn: I18N.t('playersDynamic.p010')};
	const RAW_HISTORY_KEY = 'vs-console-history';

	let catalogue = [];
	let consoleAvailable = false;
	let consoleMode = 'catalog';
	let players = [];
	let searchTimer = null;
	let refreshTimer = null;

	// ---------- консоль ----------

	const REPLY_ERROR = /\[(error|fatal|critical)\]/i;
	const REPLY_WARN = /\[warning\]/i;

	/** Уровень строки — только по явной метке в скобках, не по словам в тексте (§2.2). */
	function replyLevel(line) {
		if (REPLY_ERROR.test(line)) return 'error';
		if (REPLY_WARN.test(line)) return 'warn';
		return '';
	}

	function consoleRow(text, cls = '') {
		const output = $('#console-output');
		output.querySelector('.quiet:only-child')?.remove();
		const row = document.createElement('div');
		if (cls) row.className = cls;
		const time = document.createElement('time');
		time.textContent = fmtTime(Date.now() / 1000);
		row.append(time, document.createTextNode(text));
		return row;
	}

	function consoleLine(text, cls = '') {
		const output = $('#console-output');
		output.append(consoleRow(text, cls));
		while (output.childElementCount > 400) output.firstElementChild.remove();
		output.scrollTop = output.scrollHeight;
	}

	function printReply(command, reply) {
		const output = $('#console-output');
		output.querySelector('.quiet:only-child')?.remove();
		const block = document.createElement('div');
		block.className = 'console-block';
		block.append(consoleRow(`> ${command}`, 'sent'));
		if (reply.length) {
			let outcome = 'ok';
			reply.forEach(line => {
				const level = replyLevel(line);
				if (level === 'error') outcome = 'error';
				else if (level === 'warn' && outcome === 'ok') outcome = 'warn';
				block.append(consoleRow(line, `reply${level ? ` ${level}` : ''}`));
			});
			block.classList.add(outcome);
		} else {
			block.append(consoleRow(I18N.t('playersDynamic.p011'), 'quiet'));
			block.classList.add('quiet-outcome');
		}
		output.append(block);
		while (output.childElementCount > 400) output.firstElementChild.remove();
		output.scrollTop = output.scrollHeight;
	}

	function setConsoleState(available, text) {
		consoleAvailable = available;
		$('#console-dot').className = `pulse-dot ${available ? 'ok' : 'error'}`;
		$('#console-state').textContent = text;
		$$('#console-catalog-form button, #console-raw-form button, #console-raw-input').forEach(control => { if (!control.dataset.roleLocked && !control.dataset.busyLocked) control.disabled = !available || (control.closest('#console-raw-form') ? !VS.can('admin') : !VS.can('operator')); });
	}

	function valueControl(item) {
		const value = item.value;
		if (!value) return I18N.t('playersDynamic.p012');
		if (value.kind === 'choice') return `<select id="console-value">${value.options.map(option => `<option>${esc(option)}</option>`).join('')}</select>`;
		return `<input id="console-value" type="number" min="${esc(value.min)}" max="${esc(value.max)}" step="${esc(value.step ?? 1)}" value="${esc(value.default ?? '')}">`;
	}

	function renderCatalogValue() {
		const item = catalogue.find(entry => entry.action === $('#console-action').value);
		$('#console-value-slot').innerHTML = item ? valueControl(item) : '';
		$('#console-value-field > span').textContent = item?.value?.label || I18N.t('playersDynamic.p013');
		$('#console-action-hint').textContent = item ? [item.hint, item.confirm ? `${I18N.t('playersDynamic.p014')}${item.confirm}` : ''].filter(Boolean).join(' · ') : '';
	}

	async function loadCatalogue() {
		const data = await api('/api/server/actions');
		catalogue = data.commands || [];
		const groups = {};
		catalogue.forEach(item => { (groups[item.group || I18N.t('playersDynamic.p015')] ||= []).push(item); });
		const selected = $('#console-action').value;
		$('#console-action').innerHTML = Object.entries(groups).map(([group, items]) => `<optgroup label="${esc(group)}">${items.map(item => `<option value="${esc(item.action)}">${esc(item.label)}</option>`).join('')}</optgroup>`).join('');
		if (catalogue.some(item => item.action === selected)) $('#console-action').value = selected;
		renderCatalogValue();
		const capability = data.capability || {};
		setConsoleState(!!capability.commands, capability.commands ? `${I18N.t('playersDynamic.p016')}${capability.transport}${I18N.t('playersDynamic.p017')}` : `${I18N.t('playersDynamic.p018')}${capability.detail || I18N.t('playersDynamic.p019')}`);
	}

	function selectConsoleMode(mode) {
		if (mode === 'raw' && !VS.can('admin')) return;
		consoleMode = mode;
		$$('[data-console-mode]').forEach(button => button.classList.toggle('active', button.dataset.consoleMode === mode));
		$('#console-catalog-form').classList.toggle('hidden', mode !== 'catalog');
		$('#console-raw-form').classList.toggle('hidden', mode !== 'raw');
		if (mode === 'raw') $('#console-raw-input').focus();
		else closeSuggest();
	}

	$$('[data-console-mode]').forEach(button => button.addEventListener('click', () => selectConsoleMode(button.dataset.consoleMode)));
	$('#console-action').addEventListener('change', renderCatalogValue);

	// ---------- очистка и копирование вывода ----------

	$('#console-clear').addEventListener('click', () => {
		$('#console-output').innerHTML = I18N.t('playersDynamic.p020');
	});
	$('#console-copy-all').addEventListener('click', () => {
		const text = $$('#console-output time').map(time => `${time.textContent} ${time.nextSibling?.textContent || ''}`).join('\n');
		navigator.clipboard?.writeText(text).then(() => toast(I18N.t('playersDynamic.p021'), 'ok')).catch(() => {});
	});

	// ---------- закреплённые команды ----------

	const pinsKey = () => `vs-console-pins:${state.current || ''}`;
	const loadPins = () => { try { return JSON.parse(localStorage.getItem(pinsKey()) || '[]'); } catch { return []; } };
	const savePins = pins => { try { localStorage.setItem(pinsKey(), JSON.stringify(pins.slice(0, 12))); } catch { /* приватный режим */ } };

	function renderPins() {
		const pins = loadPins();
		const box = $('#console-pins');
		box.hidden = !pins.length;
		box.innerHTML = pins.map((pin, index) => pin.kind === 'raw'
			? `<button type="button" data-raw data-pin="${index}${I18N.t('playersDynamic.p022')}${esc(pin.label)}</button>`
			: `<button type="button" data-pin="${index}${I18N.t('playersDynamic.p023')}${esc(pin.label)}</button>`).join('');
	}

	function addPin(pin) {
		if (pin.kind === 'raw' && !VS.can('admin')) return;
		const pins = loadPins();
		if (pins.some(item => item.kind === pin.kind && item.label === pin.label)) return;
		pins.push(pin);
		savePins(pins);
		renderPins();
	}

	$('#console-pins').addEventListener('click', event => {
		const button = event.target.closest('[data-pin]');
		if (!button) return;
		const pin = loadPins()[Number(button.dataset.pin)];
		if (!pin) return;
		if (pin.kind === 'raw') {
			// Только подстановка в поле: закреплённая произвольная строка не отправляется одним щелчком.
			selectConsoleMode('raw');
			$('#console-raw-input').value = pin.command;
			$('#console-raw-input').focus();
			return;
		}
		const item = catalogue.find(entry => entry.action === pin.action);
		if (!item) { toast(I18N.t('playersDynamic.p024'), 'error'); return; }
		api('/api/server/action', {method: 'POST', body: {action: pin.action, value: pin.value || ''}})
			.then(data => printReply(data.command || pin.label, data.reply || []))
			.catch(error => { consoleLine(`${pin.label}: ${error.message}`, 'fail'); fail(error); });
	});

	$('#console-pin-catalog').addEventListener('click', () => {
		const item = catalogue.find(entry => entry.action === $('#console-action').value);
		if (!item) return;
		const value = $('#console-value') ? String($('#console-value').value) : '';
		addPin({kind: 'action', action: item.action, value, label: item.label});
	});
	$('#console-pin-raw').addEventListener('click', () => {
		const command = $('#console-raw-input').value.trim();
		if (!command) return;
		addPin({kind: 'raw', command, label: command.length > 24 ? `${command.slice(0, 24)}…` : command});
	});

	// ---------- автодополнение произвольной консоли ----------

	const consoleCommands = window.VS_CONSOLE_COMMANDS || [];
	let suggestItems = [], suggestIndex = -1;

	function suggestionsFor(text) {
		const trimmed = text.trimStart();
		if (!trimmed.startsWith('/')) return [];
		const firstSpace = trimmed.indexOf(' ');
		const head = firstSpace === -1 ? trimmed : trimmed.slice(0, firstSpace);
		const byHistory = rawHistory.filter(item => item.startsWith(trimmed) && item !== trimmed).slice(-5).reverse()
			.map(item => ({command: item, syntax: item, description: I18N.t('playersDynamic.p025')}));
		const byCommand = firstSpace === -1
			? consoleCommands.filter(item => item.command.startsWith(head))
			: consoleCommands.filter(item => item.command === head);
		return [...byHistory, ...byCommand].slice(0, 8);
	}

	function closeSuggest() {
		suggestItems = []; suggestIndex = -1;
		$('#console-suggest').classList.add('hidden');
		$('#console-raw-input').setAttribute('aria-expanded', 'false');
	}

	function renderSuggest() {
		const list = $('#console-suggest');
		if (!suggestItems.length) { closeSuggest(); return; }
		list.innerHTML = suggestItems.map((item, index) => `<li role="option" class="${index === suggestIndex ? 'active' : ''}" data-index="${index}">${esc(item.syntax)}${item.description ? `<small>${esc(item.description)}</small>` : ''}</li>`).join('');
		list.classList.remove('hidden');
		$('#console-raw-input').setAttribute('aria-expanded', 'true');
		$('#console-raw-syntax').textContent = suggestIndex >= 0 ? suggestItems[suggestIndex].syntax : I18N.t('playersDynamic.p026');
	}

	function applySuggestion(index) {
		const item = suggestItems[index];
		if (!item) return;
		const input = $('#console-raw-input');
		input.value = item.command.startsWith('/') && item.syntax !== item.command ? `${item.command} ` : item.command;
		closeSuggest();
		input.focus();
	}

	$('#console-raw-input').addEventListener('input', event => {
		suggestItems = suggestionsFor(event.target.value);
		suggestIndex = -1;
		renderSuggest();
	});

	$('#console-suggest').addEventListener('click', event => {
		const item = event.target.closest('[data-index]');
		if (item) applySuggestion(Number(item.dataset.index));
	});

	$('#console-catalog-form').addEventListener('submit', async event => {
		event.preventDefault();
		const action = $('#console-action').value;
		const item = catalogue.find(entry => entry.action === action);
		if (!item) return;
		if (item.confirm && !await VS.confirmAction(item.label, item.confirm, I18N.t('playersDynamic.p027'))) return;
		const value = $('#console-value') ? String($('#console-value').value) : '';
		try {
			const data = await busy(event.submitter, () => api('/api/server/action', {method: 'POST', body: {action, value}}));
			printReply(data.command || item.label, data.reply || []);
		} catch (error) { consoleLine(`${item.label}: ${error.message}`, 'fail'); fail(error); }
	});

	// История произвольных команд живёт в браузере: это удобство одного человека, не состояние сервера.
	const rawHistory = (() => { try { return JSON.parse(localStorage.getItem(RAW_HISTORY_KEY) || '[]'); } catch { return []; } })();
	let rawCursor = rawHistory.length;
	function rememberRaw(command) {
		if (rawHistory[rawHistory.length - 1] !== command) rawHistory.push(command);
		while (rawHistory.length > 50) rawHistory.shift();
		rawCursor = rawHistory.length;
		try { localStorage.setItem(RAW_HISTORY_KEY, JSON.stringify(rawHistory)); } catch { /* приватный режим */ }
	}

	$('#console-raw-input').addEventListener('keydown', event => {
		const listOpen = suggestItems.length > 0;
		if (event.key === 'Escape' && listOpen) { closeSuggest(); return; }
		if ((event.key === 'Tab' || event.key === 'Enter') && listOpen && suggestIndex >= 0) {
			event.preventDefault();
			applySuggestion(suggestIndex);
			return;
		}
		if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
		event.preventDefault();
		if (listOpen) {
			suggestIndex = Math.max(0, Math.min(suggestItems.length - 1, suggestIndex + (event.key === 'ArrowUp' ? -1 : 1)));
			renderSuggest();
			return;
		}
		rawCursor = Math.max(0, Math.min(rawHistory.length, rawCursor + (event.key === 'ArrowUp' ? -1 : 1)));
		event.target.value = rawHistory[rawCursor] || '';
	});

	$('#console-raw-form').addEventListener('submit', async event => {
		event.preventDefault();
		const input = $('#console-raw-input');
		const command = input.value.trim();
		if (!command) return;
		try {
			const data = await busy(event.submitter, () => api('/api/server/console', {method: 'POST', body: {command}}));
			rememberRaw(command);
			input.value = '';
			printReply(data.command || command, data.reply || []);
		} catch (error) { consoleLine(`${command}: ${error.message}`, 'fail'); fail(error); }
		input.focus();
	});

	// ---------- игроки ----------

	function renderPlayers(list) {
		players = list;
		const picked = $('#player-name').value.trim();
		$('#players-table').innerHTML = list.map(player => `<tr class="clickable${player.name === picked ? ' picked' : ''}" data-player="${esc(player.name)}${I18N.t('playersDynamic.p028')}${esc((player.name || '?').slice(0, 1))}</span><span>${esc(player.name)}<small>${esc(player.role || player.state || '')}${I18N.t('playersDynamic.p029')}${esc(player.ip || (VS.can('operator') ? '—' : I18N.t('playersDynamic.p030')))}</td>
			<td data-label="Ping"><b>${player.ping_ms == null ? '—' : `${Number(player.ping_ms).toFixed(0)}${I18N.t('playersDynamic.p031')}`}${I18N.t('playersDynamic.p032')}${player.average_ping_ms == null ? '—' : `${Number(player.average_ping_ms).toFixed(0)} / ${Number(player.max_ping_ms).toFixed(0)}${I18N.t('playersDynamic.p033')}`}</td>
			<td data-label="Jitter">${player.jitter_ms == null ? '—' : `${Number(player.jitter_ms).toFixed(1)}${I18N.t('playersDynamic.p034')}`}${player.spike_count ? `<small>${esc(player.spike_count)}${I18N.t('playersDynamic.p035')}` : ''}${I18N.t('playersDynamic.p036')}${esc(player.quality || 'unavailable')}">${esc(QUALITY[player.quality] || I18N.t('playersDynamic.p037'))}</span></td></tr>`).join('') || I18N.t('playersDynamic.p038');
		$('#player-names').innerHTML = list.map(player => `<option value="${esc(player.name)}">`).join('');
		const select = $('#ping-player'), selected = select.value;
		select.innerHTML = I18N.t('playersDynamic.p039') + list.map(player => `<option value="${esc(player.name)}">${esc(player.name)}</option>`).join('');
		if (list.some(player => player.name === selected)) select.value = selected;
		$('#quality-list').innerHTML = list.map(player => `<div><span class="player-avatar">${esc(player.name.slice(0, 1))}</span><span class="grow"><b>${esc(player.name)}</b><small>ping ${player.ping_ms == null ? '—' : `${Number(player.ping_ms).toFixed(0)}${I18N.t('playersDynamic.p040')}`} · jitter ${player.jitter_ms == null ? '—' : `${Number(player.jitter_ms).toFixed(1)}${I18N.t('playersDynamic.p041')}`}${I18N.t('playersDynamic.p042')}${esc(player.spike_count || 0)}${player.ip ? ` · ${esc(player.ip)}` : ''}</small></span><span class="quality ${esc(player.quality || 'unavailable')}">${esc(QUALITY[player.quality] || I18N.t('playersDynamic.p043'))}</span></div>`).join('') || I18N.t('playersDynamic.p044');
	}

	$('#players-table').addEventListener('click', event => {
		const row = event.target.closest('[data-player]');
		if (!row) return;
		$('#player-name').value = row.dataset.player;
		$('#ping-player').value = row.dataset.player;
		$$('#players-table tr').forEach(item => item.classList.toggle('picked', item === row));
	});

	function renderTelemetryState(telemetry) {
		const fresh = telemetry?.available && !telemetry?.stale;
		$('#telemetry-dot').className = `pulse-dot ${fresh ? 'ok' : 'warn'}`;
		$('#telemetry-detail').textContent = fresh ? `${I18N.t('playersDynamic.p045')}${telemetry.age_seconds != null ? `${telemetry.age_seconds}${I18N.t('playersDynamic.p046')}` : I18N.t('playersDynamic.p047')}` : (telemetry?.detail || I18N.t('playersDynamic.p048'));
		$('#players-source').textContent = fresh ? I18N.t('playersDynamic.p049') : I18N.t('playersDynamic.p050');
	}

	function renderPlayerEvents(events) {
		$('#player-events').innerHTML = events.slice(-12).reverse().map(event => {
			const player = event.player_event || {};
			return `<div><time>${esc(event.timestamp ? fmtShortTime(event.timestamp) : '—')}</time><span class="event-kind ${player.type === 'join' ? 'join' : 'leave'}">${player.type === 'join' ? I18N.t('playersDynamic.p051') : I18N.t('playersDynamic.p052')}</span><span><strong>${esc(player.player)}</strong></span></div>`;
		}).join('') || I18N.t('playersDynamic.p053');
	}

	async function loadPlayers() {
		const data = await api('/api/players');
		renderPlayers(data.online || []);
		renderTelemetryState(data.telemetry || {});
		renderPlayerEvents(data.events || []);
		const count = (data.online || []).length;
		$('#players-count').textContent = `${String(count).padStart(2, '0')}${I18N.t('playersDynamic.p054')}`;
	}

	function watchPlayerEvents() {
		VS.stream('players-log', '/api/logs/stream?category=player', {
			page: 'players',
			onMessage: item => { if (item.player_event) { clearTimeout(refreshTimer); refreshTimer = setTimeout(() => loadPlayers().catch(() => {}), 600); } },
		});
	}

	VS.on('telemetry', data => {
		if (state.page !== 'players' || !(data?.available && !data?.stale)) return;
		// Поток телеметрии несёт игроков без статистики ping; полная таблица — по запросу раз в 10 с.
		$('#players-count').textContent = `${String((data.players || []).length).padStart(2, '0')}${I18N.t('playersDynamic.p055')}`;
	});

	$('#players-refresh').addEventListener('click', event => busy(event.currentTarget, loadPlayers).catch(fail));

	// ---------- действия ----------

	function updateActionForm() {
		const action = $('#player-action').value;
		$('#player-detail-field').classList.toggle('hidden', !['kick', 'ban'].includes(action));
		$('#player-mode-field').classList.toggle('hidden', action !== 'gamemode');
	}
	$('#player-action').addEventListener('change', updateActionForm);
	updateActionForm();

	const ACTION_TITLES = {kick: I18N.t('playersDynamic.p056'), ban: I18N.t('playersDynamic.p057'), unban: I18N.t('playersDynamic.p058'), 'whitelist-add': I18N.t('playersDynamic.p059'), 'whitelist-remove': I18N.t('playersDynamic.p060'), op: I18N.t('playersDynamic.p061'), deop: I18N.t('playersDynamic.p062'), gamemode: I18N.t('playersDynamic.p063')};

	$('#player-action-form').addEventListener('submit', async event => {
		event.preventDefault();
		const action = $('#player-action').value, player = $('#player-name').value.trim();
		const detail = action === 'gamemode' ? $('#player-mode').value : $('#player-detail').value.trim();
		if (['ban', 'op'].includes(action) && !await VS.confirmAction(`${ACTION_TITLES[action]}: ${player}?`, action === 'ban' ? I18N.t('playersDynamic.p064') : I18N.t('playersDynamic.p065'), ACTION_TITLES[action], action === 'ban')) return;
		try {
			await busy(event.submitter, () => api('/api/players/action', {method: 'POST', body: {action, player, detail}}));
			toast(`${ACTION_TITLES[action]}: ${player}${I18N.t('playersDynamic.p066')}`, 'ok');
			$('#player-detail').value = '';
			setTimeout(() => loadPlayers().catch(() => {}), 1200);
			if (['ban', 'unban', 'whitelist-add', 'whitelist-remove'].includes(action)) reloadListsSoon();
		} catch (error) { fail(error); }
	});

	$('#announce-form').addEventListener('submit', async event => {
		event.preventDefault();
		const text = $('#announce-text').value.trim();
		try {
			await busy(event.submitter, () => api('/api/players/action', {method: 'POST', body: {action: 'announce', player: '', detail: text}}));
			$('#announce-text').value = '';
			consoleLine(`> /announce ${text}`, 'sent');
			toast(I18N.t('playersDynamic.p067'), 'ok');
		} catch (error) { fail(error); }
	});

	// ---------- чат и события ----------

	function chatText(event) {
		if (event.kind === 'chat') return event.text || '';
		if (event.kind === 'death') return `${I18N.t('playersDynamic.p068')}${event.detail || event.text || I18N.t('playersDynamic.p069')}`;
		return {join: I18N.t('playersDynamic.p070'), leave: I18N.t('playersDynamic.p071'), respawn: I18N.t('playersDynamic.p072')}[event.kind] || event.text || '';
	}
	const chatRow = event => `<div><time>${esc(event.at ? fmtShortTime(event.at) : '—')}</time><span class="event-kind ${esc(event.kind)}">${esc(CHAT_KINDS[event.kind] || event.kind)}</span><span><strong>${esc(event.player || '—')}</strong> ${esc(chatText(event))}</span></div>`;
	const chatQuery = () => `kind=${encodeURIComponent($('#chat-kind').value)}&search=${encodeURIComponent($('#chat-search').value.trim())}`;

	async function loadChat() {
		VS.closeStream('chat');
		const data = await api(`/api/game-events?lines=200&${chatQuery()}`);
		const support = $('#chat-support');
		support.classList.toggle('hidden', !!data.supported);
		support.className = `notice warn${data.supported ? ' hidden' : ''}`;
		support.textContent = I18N.t('playersDynamic.p073');
		const feed = $('#chat-feed');
		feed.innerHTML = (data.events || []).map(chatRow).join('') || I18N.t('playersDynamic.p074');
		feed.scrollTop = feed.scrollHeight;
		if (!data.supported) { $('#chat-state').textContent = I18N.t('playersDynamic.p075'); return; }
		VS.stream('chat', `/api/game-events/stream?${chatQuery()}`, {
			page: 'players',
			onOpen: () => { $('#chat-state').textContent = I18N.t('playersDynamic.p076'); },
			onError: () => { $('#chat-state').textContent = I18N.t('playersDynamic.p077'); },
			onMessage: event => {
				if (event.stream_error) { $('#chat-state').textContent = event.stream_error; return; }
				const box = $('#chat-feed'), bottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
				box.querySelector('.empty')?.remove();
				box.insertAdjacentHTML('beforeend', chatRow(event));
				while (box.childElementCount > 400) box.firstElementChild.remove();
				if (bottom) box.scrollTop = box.scrollHeight;
			},
		});
	}

	$('#chat-refresh').addEventListener('click', () => loadChat().catch(fail));
	$('#chat-kind').addEventListener('change', () => loadChat().catch(fail));
	$('#chat-search').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => loadChat().catch(fail), 400); });

	// ---------- ping ----------

	async function loadPing() {
		const player = $('#ping-player').value;
		const box = $('#ping-chart');
		if (!player) { box.innerHTML = I18N.t('playersDynamic.p078'); $('#ping-summary').innerHTML = ''; return; }
		const data = await api(`/api/telemetry?player=${encodeURIComponent(player)}&minutes=60`);
		const history = data.history || [];
		const values = history.map(point => Number(point.ping_ms) || 0);
		VS.lineChart(box, {series: [{values, cls: 'ping', area: true}], max: Math.max(50, ...values) * 1.1, unit: I18N.t('playersDynamic.p079'), labels: VS.timeLabels(history.map(point => point.ts)), empty: `${I18N.t('playersDynamic.p080')}${player}.`, height: 190});
		const info = players.find(item => item.name === player) || {};
		$('#ping-summary').innerHTML = values.length ? `${I18N.t('playersDynamic.p081')}${info.ping_ms == null ? '—' : `${Number(info.ping_ms).toFixed(0)}${I18N.t('playersDynamic.p082')}`}${I18N.t('playersDynamic.p083')}${(values.reduce((a, b) => a + b, 0) / values.length).toFixed(0)}${I18N.t('playersDynamic.p084')}${Math.max(...values).toFixed(0)}${I18N.t('playersDynamic.p085')}${info.jitter_ms == null ? '—' : `${Number(info.jitter_ms).toFixed(1)}${I18N.t('playersDynamic.p086')}`}</b></span>` : '';
	}
	$('#ping-player').addEventListener('change', () => loadPing().catch(fail));

	// ---------- бан и белый список ----------

	// Списки читаются из файлов игры (GET /api/players/lists), а меняются командами консоли через
	// /api/players/action: после действия игре нужно время записать файл, поэтому перечитывание — с паузой.
	let lists = {banned: [], whitelisted: [], whitelist_mode: false, stale: false};
	let listView = 'banned';
	let listsTimer = null;

	function listUntil(entry) {
		if (entry.forever) return I18N.t('playersDynamic.p087');
		return entry.until ? `${I18N.t('playersDynamic.p088')}${VS.fmtDateTime(entry.until)}` : '—';
	}

	function renderLists() {
		$('#lists-banned-count').textContent = lists.banned.length;
		$('#lists-whitelisted-count').textContent = lists.whitelisted.length;
		for (const button of $$('[data-list]')) {
			const active = button.dataset.list === listView;
			button.classList.toggle('active', active);
			button.setAttribute('aria-pressed', String(active));
		}

		const mode = $('#lists-mode');
		mode.className = `notice${lists.stale ? ' warn' : ''}${listView === 'whitelisted' ? '' : ' hidden'}`;
		mode.textContent = lists.stale
			? I18N.t('playersDynamic.p089')
			: lists.whitelist_mode
				? I18N.t('playersDynamic.p090')
				: I18N.t('playersDynamic.p091');
		$('#whitelist-add-form').classList.toggle('hidden', listView !== 'whitelisted');

		const banned = listView === 'banned';
		$('#lists-head').innerHTML = banned
			? I18N.t('playersDynamic.p092')
			: I18N.t('playersDynamic.p093');
		const query = $('#lists-search').value.trim().toLowerCase();
		const rows = (banned ? lists.banned : lists.whitelisted)
			.filter(entry => !query || `${entry.player} ${entry.issued_by || ''}`.toLowerCase().includes(query));
		const action = banned ? 'unban' : 'whitelist-remove';
		const label = banned ? I18N.t('playersDynamic.p094') : I18N.t('playersDynamic.p095');
		const player = entry => `${I18N.t('playersDynamic.p096')}${esc(entry.player)}</b>${entry.uid ? `<small class="mono">${esc(entry.uid)}</small>` : ''}</td>`;
		const button = entry => `<td><button class="button small" type="button" data-list-action="${action}" data-player="${esc(entry.player)}" data-min-role="operator" data-mutates>${label}</button></td>`;
		$('#lists-table').innerHTML = rows.map(entry => banned
			? `<tr>${player(entry)}${I18N.t('playersDynamic.p097')}${esc(entry.issued_by || '—')}${I18N.t('playersDynamic.p098')}${esc(listUntil(entry))}${I18N.t('playersDynamic.p099')}${esc(entry.reason || '—')}</td>${button(entry)}</tr>`
			: `<tr>${player(entry)}${I18N.t('playersDynamic.p100')}${esc(entry.issued_by || '—')}</td>${button(entry)}</tr>`).join('')
			|| `<tr><td colspan="${banned ? 5 : 3}" class="muted">${query ? I18N.t('playersDynamic.p101') : banned ? I18N.t('playersDynamic.p102') : I18N.t('playersDynamic.p103')}</td></tr>`;
		VS.applyRoles($('#lists-table'));
		VS.applyBusyLock($('#lists-table'));
	}

	async function loadLists() {
		lists = await api('/api/players/lists');
		renderLists();
	}

	function reloadListsSoon() {
		clearTimeout(listsTimer);
		listsTimer = setTimeout(() => loadLists().catch(() => {}), 1500);
	}

	async function sendListAction(button, action, player) {
		await busy(button, () => api('/api/players/action', {method: 'POST', body: {action, player, detail: ''}}));
		toast(`${ACTION_TITLES[action]}: ${player}${I18N.t('playersDynamic.p104')}`, 'ok');
		reloadListsSoon();
	}

	$$('[data-list]').forEach(button => button.addEventListener('click', () => { listView = button.dataset.list; renderLists(); }));
	$('#lists-search').addEventListener('input', renderLists);
	$('#lists-refresh').addEventListener('click', event => busy(event.currentTarget, loadLists).catch(fail));

	$('#lists-table').addEventListener('click', async event => {
		const button = event.target.closest('[data-list-action]');
		if (!button) return;
		const {listAction: action, player} = button.dataset;
		if (action === 'whitelist-remove' && lists.whitelist_mode
			&& !await VS.confirmAction(`${I18N.t('playersDynamic.p105')}${player}${I18N.t('playersDynamic.p106')}`, I18N.t('playersDynamic.p107'), I18N.t('playersDynamic.p108'), true)) return;
		try { await sendListAction(button, action, player); } catch (error) { fail(error); }
	});

	$('#whitelist-add-form').addEventListener('submit', async event => {
		event.preventDefault();
		const input = $('#whitelist-add-name'), player = input.value.trim();
		try {
			await sendListAction(event.submitter, 'whitelist-add', player);
			input.value = '';
		} catch (error) { fail(error); }
	});

	// ---------- раздел ----------

	VS.on('session', () => {
		if (!VS.can('admin')) { $('#console-raw-tab').disabled = true; $('#console-raw-tab').title = I18N.t('playersDynamic.p109'); }
	});

	VS.on('instances', renderPins);

	VS.pages.players = {
		enter(tab) {
			Promise.all([loadPlayers(), loadCatalogue()]).catch(fail);
			renderPins();
			watchPlayerEvents();
			VS.every('players', 10000, loadPlayers, {page: 'players', now: false});
			this.tab(tab);
		},
		tab(view) {
			if (view === 'activity') loadChat().catch(fail);
			else VS.closeStream('chat');
			if (view === 'quality') loadPing().catch(fail);
			if (view === 'lists') loadLists().catch(fail);
		},
	};
})();
