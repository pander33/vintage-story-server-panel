'use strict';

// Игроки и консоль: подключения, каталог команд и произвольная консоль (ADR-17), действия
// над игроками, объявления, чат и события мира, история ping.

(() => {
	const {$, $$, esc, fmtTime, fmtShortTime, state, api, toast, fail, busy} = VS;

	const QUALITY = {excellent: 'Отличное', good: 'Хорошее', unstable: 'Нестабильное', poor: 'Плохое', unavailable: 'Нет данных'};
	const CHAT_KINDS = {chat: 'Чат', join: 'Вход', leave: 'Выход', death: 'Смерть', respawn: 'Возрождение'};
	const RAW_HISTORY_KEY = 'vs-console-history';

	let catalogue = [];
	let consoleAvailable = false;
	let consoleMode = 'catalog';
	let players = [];
	let searchTimer = null;
	let refreshTimer = null;

	// ---------- консоль ----------

	function consoleLine(text, cls = '') {
		const output = $('#console-output');
		output.querySelector('.quiet:only-child')?.remove();
		const row = document.createElement('div');
		if (cls) row.className = cls;
		const time = document.createElement('time');
		time.textContent = fmtTime(Date.now() / 1000);
		row.append(time, document.createTextNode(text));
		output.append(row);
		while (output.childElementCount > 400) output.firstElementChild.remove();
		output.scrollTop = output.scrollHeight;
	}

	function printReply(command, reply) {
		consoleLine(`> ${command}`, 'sent');
		if (reply.length) reply.forEach(line => consoleLine(line));
		else consoleLine('Сервер не вывел ответ за секунду ожидания. Полный вывод — во вкладке «Журнал».', 'quiet');
	}

	function setConsoleState(available, text) {
		consoleAvailable = available;
		$('#console-dot').className = `pulse-dot ${available ? "ok" : "error"}`;
		$('#console-state').textContent = text;
		$$('#console-catalog-form button, #console-raw-form button, #console-raw-input').forEach(control => { if (!control.dataset.roleLocked) control.disabled = !available || (control.closest('#console-raw-form') ? !VS.can('admin') : !VS.can('operator')); });
	}

	function valueControl(item) {
		const value = item.value;
		if (!value) return '<input disabled placeholder="Без значения">';
		if (value.kind === 'choice') return `<select id="console-value">${value.options.map(option => `<option>${esc(option)}</option>`).join("")}</select>`;
		return `<input id="console-value" type="number" min="${esc(value.min)}" max="${esc(value.max)}" step="${esc(value.step ?? 1)}" value="${esc(value.default ?? "")}">`;
	}

	function renderCatalogValue() {
		const item = catalogue.find(entry => entry.action === $('#console-action').value);
		$('#console-value-slot').innerHTML = item ? valueControl(item) : '';
		$('#console-value-field > span').textContent = item?.value?.label || 'Значение';
		$('#console-action-hint').textContent = item ? [item.hint, item.confirm ? `Потребует подтверждения: ${item.confirm}` : ''].filter(Boolean).join(' · ') : '';
	}

	async function loadCatalogue() {
		const data = await api('/api/server/actions');
		catalogue = data.commands || [];
		const groups = {};
		catalogue.forEach(item => { (groups[item.group || 'Прочее'] ||= []).push(item); });
		const selected = $('#console-action').value;
		$('#console-action').innerHTML = Object.entries(groups).map(([group, items]) => `<optgroup label="${esc(group)}">${items.map(item => `<option value="${esc(item.action)}">${esc(item.label)}</option>`).join("")}</optgroup>`).join('');
		if (catalogue.some(item => item.action === selected)) $('#console-action').value = selected;
		renderCatalogValue();
		const capability = data.capability || {};
		setConsoleState(!!capability.commands, capability.commands ? `Канал ${capability.transport} доступен` : `Консоль недоступна: ${capability.detail || "транспорт не настроен"}`);
	}

	function selectConsoleMode(mode) {
		if (mode === 'raw' && !VS.can('admin')) return;
		consoleMode = mode;
		$$('[data-console-mode]').forEach(button => button.classList.toggle('active', button.dataset.consoleMode === mode));
		$('#console-catalog-form').classList.toggle('hidden', mode !== 'catalog');
		$('#console-raw-form').classList.toggle('hidden', mode !== 'raw');
		if (mode === 'raw') $('#console-raw-input').focus();
	}

	$$('[data-console-mode]').forEach(button => button.addEventListener('click', () => selectConsoleMode(button.dataset.consoleMode)));
	$('#console-action').addEventListener('change', renderCatalogValue);

	$('#console-catalog-form').addEventListener('submit', async event => {
		event.preventDefault();
		const action = $('#console-action').value;
		const item = catalogue.find(entry => entry.action === action);
		if (!item) return;
		if (item.confirm && !await VS.confirmAction(item.label, item.confirm, 'Выполнить')) return;
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
		if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
		event.preventDefault();
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
		$('#players-table').innerHTML = list.map(player => `<tr class="clickable${player.name === picked ? " picked" : ""}" data-player="${esc(player.name)}">
			<td><span class="player-cell"><span class="player-avatar">${esc((player.name || "?").slice(0, 1))}</span><span>${esc(player.name)}<small>${esc(player.role || player.state || "")}</small></span></span></td>
			<td class="mono">${esc(player.ip || (VS.can("operator") ? "—" : "скрыт"))}</td>
			<td><b>${player.ping_ms == null ? "—" : `${Number(player.ping_ms).toFixed(0)} мс`}</b></td>
			<td>${player.average_ping_ms == null ? "—" : `${Number(player.average_ping_ms).toFixed(0)} / ${Number(player.max_ping_ms).toFixed(0)} мс`}</td>
			<td>${player.jitter_ms == null ? "—" : `${Number(player.jitter_ms).toFixed(1)} мс`}${player.spike_count ? `<small>${esc(player.spike_count)} скач.</small>` : ""}</td>
			<td><span class="quality ${esc(player.quality || "unavailable")}">${esc(QUALITY[player.quality] || "Нет данных")}</span></td></tr>`).join('') || '<tr><td colspan="6" class="muted">Подключённых игроков нет</td></tr>';
		$('#player-names').innerHTML = list.map(player => `<option value="${esc(player.name)}">`).join('');
		const select = $('#ping-player'), selected = select.value;
		select.innerHTML = '<option value="">Выберите игрока</option>' + list.map(player => `<option value="${esc(player.name)}">${esc(player.name)}</option>`).join('');
		if (list.some(player => player.name === selected)) select.value = selected;
		$('#quality-list').innerHTML = list.map(player => `<div><span class="player-avatar">${esc(player.name.slice(0, 1))}</span><span class="grow"><b>${esc(player.name)}</b><small>ping ${player.ping_ms == null ? "—" : `${Number(player.ping_ms).toFixed(0)} мс`} · jitter ${player.jitter_ms == null ? "—" : `${Number(player.jitter_ms).toFixed(1)} мс`} · скачков ${esc(player.spike_count || 0)}${player.ip ? ` · ${esc(player.ip)}` : ""}</small></span><span class="quality ${esc(player.quality || "unavailable")}">${esc(QUALITY[player.quality] || "Нет данных")}</span></div>`).join('') || '<div class="empty">Подключённых игроков нет.</div>';
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
		$('#telemetry-dot').className = `pulse-dot ${fresh ? "ok" : "warn"}`;
		$('#telemetry-detail').textContent = fresh ? `Телеметрия получена ${telemetry.age_seconds != null ? `${telemetry.age_seconds} с назад` : "только что"}` : (telemetry?.detail || 'Телеметрия недоступна: показан список из журнала, ping неизвестен');
		$('#players-source').textContent = fresh ? 'Список обновляется вместе с телеметрией' : 'Резервный список из журнала: ping и качество недоступны';
	}

	function renderPlayerEvents(events) {
		$('#player-events').innerHTML = events.slice(-12).reverse().map(event => {
			const player = event.player_event || {};
			return `<div><time>${esc(event.timestamp ? fmtShortTime(event.timestamp) : "—")}</time><span class="event-kind ${player.type === "join" ? "join" : "leave"}">${player.type === "join" ? "Вход" : "Выход"}</span><span><strong>${esc(player.player)}</strong></span></div>`;
		}).join('') || '<div class="empty">Входов и выходов в журнале пока нет.</div>';
	}

	async function loadPlayers() {
		const data = await api('/api/players');
		renderPlayers(data.online || []);
		renderTelemetryState(data.telemetry || {});
		renderPlayerEvents(data.events || []);
		const count = (data.online || []).length;
		$('#players-count').textContent = `${String(count).padStart(2, "0")} онлайн`;
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
		$('#players-count').textContent = `${String((data.players || []).length).padStart(2, "0")} онлайн`;
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

	const ACTION_TITLES = {kick: 'Отключить', ban: 'Заблокировать', unban: 'Разблокировать', 'whitelist-add': 'Добавить в whitelist', 'whitelist-remove': 'Убрать из whitelist', op: 'Выдать оператора', deop: 'Снять оператора', gamemode: 'Сменить режим'};

	$('#player-action-form').addEventListener('submit', async event => {
		event.preventDefault();
		const action = $('#player-action').value, player = $('#player-name').value.trim();
		const detail = action === 'gamemode' ? $('#player-mode').value : $('#player-detail').value.trim();
		if (['ban', 'op'].includes(action) && !await VS.confirmAction(`${ACTION_TITLES[action]}: ${player}?`, action === 'ban' ? 'Игрок будет отключён и не сможет войти до разблокировки.' : 'Оператор получает полный доступ к командам сервера.', ACTION_TITLES[action], action === 'ban')) return;
		try {
			await busy(event.submitter, () => api('/api/players/action', {method: 'POST', body: {action, player, detail}}));
			toast(`${ACTION_TITLES[action]}: ${player} — команда отправлена`, 'ok');
			$('#player-detail').value = '';
			setTimeout(() => loadPlayers().catch(() => {}), 1200);
		} catch (error) { fail(error); }
	});

	$('#announce-form').addEventListener('submit', async event => {
		event.preventDefault();
		const text = $('#announce-text').value.trim();
		try {
			await busy(event.submitter, () => api('/api/players/action', {method: 'POST', body: {action: 'announce', player: '', detail: text}}));
			$('#announce-text').value = '';
			consoleLine(`> /announce ${text}`, 'sent');
			toast('Объявление отправлено', 'ok');
		} catch (error) { fail(error); }
	});

	// ---------- чат и события ----------

	function chatText(event) {
		if (event.kind === 'chat') return event.text || '';
		if (event.kind === 'death') return `погиб: ${event.detail || event.text || "причина неизвестна"}`;
		return {join: 'вошёл в игру', leave: 'вышел из игры', respawn: 'возродился'}[event.kind] || event.text || '';
	}
	const chatRow = event => `<div><time>${esc(event.at ? fmtShortTime(event.at) : "—")}</time><span class="event-kind ${esc(event.kind)}">${esc(CHAT_KINDS[event.kind] || event.kind)}</span><span><strong>${esc(event.player || "—")}</strong> ${esc(chatText(event))}</span></div>`;
	const chatQuery = () => `kind=${encodeURIComponent($("#chat-kind").value)}&search=${encodeURIComponent($("#chat-search").value.trim())}`;

	async function loadChat() {
		VS.closeStream('chat');
		const data = await api(`/api/game-events?lines=200&${chatQuery()}`);
		const support = $('#chat-support');
		support.classList.toggle('hidden', !!data.supported);
		support.className = `notice warn${data.supported ? " hidden" : ""}`;
		support.textContent = 'Установленный мод телеметрии не ведёт журнал событий. Обновите vspaneltelemetry и перезапустите сервер.';
		const feed = $('#chat-feed');
		feed.innerHTML = (data.events || []).map(chatRow).join('') || '<div class="empty">Событий пока нет.</div>';
		feed.scrollTop = feed.scrollHeight;
		if (!data.supported) { $('#chat-state').textContent = 'Поток недоступен'; return; }
		VS.stream('chat', `/api/game-events/stream?${chatQuery()}`, {
			page: 'players',
			onOpen: () => { $('#chat-state').textContent = 'Поток активен'; },
			onError: () => { $('#chat-state').textContent = 'Переподключение…'; },
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
		if (!player) { box.innerHTML = '<div class="chart-empty">Выберите подключённого игрока.</div>'; $('#ping-summary').innerHTML = ''; return; }
		const data = await api(`/api/telemetry?player=${encodeURIComponent(player)}&minutes=60`);
		const history = data.history || [];
		const values = history.map(point => Number(point.ping_ms) || 0);
		VS.lineChart(box, {series: [{values, cls: 'ping', area: true}], max: Math.max(50, ...values) * 1.1, unit: ' мс', labels: VS.timeLabels(history.map(point => point.ts)), empty: `Недостаточно измерений для ${player}.`, height: 190});
		const info = players.find(item => item.name === player) || {};
		$('#ping-summary').innerHTML = values.length ? `<span>Сейчас <b>${info.ping_ms == null ? "—" : `${Number(info.ping_ms).toFixed(0)} мс`}</b></span><span>Средний <b>${(values.reduce((a, b) => a + b, 0) / values.length).toFixed(0)} мс</b></span><span>Максимум <b>${Math.max(...values).toFixed(0)} мс</b></span><span>Jitter <b>${info.jitter_ms == null ? "—" : `${Number(info.jitter_ms).toFixed(1)} мс`}</b></span>` : '';
	}
	$('#ping-player').addEventListener('change', () => loadPing().catch(fail));

	// ---------- раздел ----------

	VS.on('session', () => {
		if (!VS.can('admin')) { $('#console-raw-tab').disabled = true; $('#console-raw-tab').title = 'Произвольная консоль доступна только администратору'; }
	});

	VS.pages.players = {
		enter(tab) {
			Promise.all([loadPlayers(), loadCatalogue()]).catch(fail);
			watchPlayerEvents();
			VS.every('players', 10000, loadPlayers, {page: 'players', now: false});
			this.tab(tab);
		},
		tab(view) {
			if (view === 'activity') loadChat().catch(fail);
			else VS.closeStream('chat');
			if (view === 'quality') loadPing().catch(fail);
		},
	};
})();
