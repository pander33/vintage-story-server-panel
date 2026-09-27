'use strict';

// Ядро интерфейса: запросы к API, сессия, права, диалоги, разделы и живые потоки.
// Разделы (overview.js, players.js …) регистрируют себя в VS.pages и не знают друг о друге.

const VS = (() => {
	const $ = (selector, root = document) => root.querySelector(selector);
	const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

	const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'}[char]));

	const fmtBytes = value => {
		const n = Number(value) || 0;
		if (!n) return '0 Б';
		const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
		const i = Math.min(Math.floor(Math.log(n) / Math.log(1024)), units.length - 1);
		return `${(n / 1024 ** i).toFixed(i ? 1 : 0)} ${units[i]}`;
	};

	const fmtDuration = value => {
		const s = Math.max(0, Number(value) || 0);
		const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
		if (d) return `${d} д ${h} ч`;
		if (h) return `${h} ч ${m} мин`;
		return `${m} мин`;
	};

	/** Секунды Unix или ISO-строка — в Date. */
	const toDate = value => {
		if (value === null || value === undefined || value === '') return null;
		const date = typeof value === 'number' ? new Date(value * 1000) : new Date(value);
		return Number.isNaN(date.getTime()) ? null : date;
	};
	const fmtDateTime = value => { const d = toDate(value); return d ? d.toLocaleString('ru-RU', {day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit'}) : '—'; };
	const fmtTime = value => { const d = toDate(value); return d ? d.toLocaleTimeString('ru-RU', {hour: '2-digit', minute: '2-digit', second: '2-digit'}) : '—'; };
	const fmtShortTime = value => { const d = toDate(value); return d ? d.toLocaleTimeString('ru-RU', {hour: '2-digit', minute: '2-digit'}) : '—'; };
	const fmtAgo = value => {
		const d = toDate(value);
		if (!d) return '—';
		const s = Math.round((Date.now() - d.getTime()) / 1000);
		if (s < 60) return 'только что';
		if (s < 3600) return `${Math.floor(s / 60)} мин назад`;
		if (s < 86400) return `${Math.floor(s / 3600)} ч назад`;
		return fmtDateTime(value);
	};
	const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
	const fmtDay = value => {
		const d = toDate(value);
		if (!d) return '';
		const today = new Date();
		const yesterday = new Date(today.getTime() - 86400000);
		if (sameDay(d, today)) return 'сегодня';
		if (sameDay(d, yesterday)) return 'вчера';
		return d.toLocaleDateString('ru-RU', {day: 'numeric', month: 'long'});
	};

	// ---------- состояние ----------

	const state = {csrf: '', user: '', role: 'viewer', page: 'overview', instances: [], current: '', panelInstance: '', status: null, telemetry: null};
	const rank = {viewer: 0, operator: 1, admin: 2};
	const roleTitles = {viewer: 'Наблюдатель', operator: 'Оператор', admin: 'Администратор'};
	const can = role => (rank[state.role] ?? 0) >= (rank[role] ?? 99);

	// ---------- события ----------

	const listeners = {};
	const on = (name, handler) => { (listeners[name] ||= []).push(handler); };
	const emit = (name, payload) => (listeners[name] || []).forEach(handler => { try { handler(payload); } catch (error) { console.error(error); } });

	// ---------- уведомления ----------

	function toast(message, kind = '') {
		const stack = $('#toasts');
		const item = document.createElement('div');
		item.className = `toast ${kind}`;
		item.textContent = message;
		stack.append(item);
		while (stack.childElementCount > 4) stack.firstElementChild.remove();
		setTimeout(() => item.remove(), kind === 'error' ? 7000 : 4200);
	}
	const fail = error => toast(error?.message || String(error), 'error');

	// ---------- API ----------

	class ApiError extends Error {
		constructor(message, status) { super(message); this.status = status; }
	}

	async function api(path, options = {}) {
		const method = options.method || 'GET';
		const tracked = method !== 'GET' && !UNTRACKED.test(path);
		if (tracked) trackBegin();
		try { return await request(path, options); }
		finally { if (tracked) trackEnd(); }
	}

	async function request(path, options) {
		const init = {method: options.method || 'GET', headers: {...(options.headers || {})}};
		if (state.csrf && init.method !== 'GET') init.headers['X-CSRF-Token'] = state.csrf;
		if (options.body instanceof FormData) init.body = options.body;
		else if (options.body instanceof Blob) { init.headers['Content-Type'] = 'application/octet-stream'; init.body = options.body; }
		else if (options.body !== undefined) { init.headers['Content-Type'] = 'application/json'; init.body = JSON.stringify(options.body); }
		let response;
		try { response = await fetch(path, init); }
		catch { setConnection(false, 'Нет связи с панелью'); throw new ApiError('Панель недоступна: проверьте соединение', 0); }
		let data = {};
		if (response.ok && options.response === 'blob') return response.blob();
		try { data = await response.json(); } catch { /* пустое тело */ }
		if (response.status === 401 && !options.allowAnonymous) { showLogin(); throw new ApiError('Требуется вход', 401); }
		if (!response.ok) {
			const detail = typeof data.detail === 'string' ? data.detail : (Array.isArray(data.detail) ? data.detail.map(item => item.msg).join('; ') : '');
			throw new ApiError(detail || `HTTP ${response.status}`, response.status);
		}
		return data;
	}

	// ---------- права ----------

	/** Элементы с data-min-role: скрыть (data-hide-denied) или выключить с подсказкой. */
	function applyRoles(root = document) {
		for (const element of $$('[data-min-role]', root)) {
			const allowed = can(element.dataset.minRole);
			if (element.hasAttribute('data-hide-denied')) { element.hidden = !allowed; continue; }
			const title = `Нужна роль «${roleTitles[element.dataset.minRole]}»`;
			const controls = element.matches('button, input, select, textarea') ? [element] : $$('button, input, select, textarea', element);
			if (element.matches('label.button')) element.classList.toggle('is-disabled', !allowed);
			for (const control of controls) {
				if (!allowed) { control.disabled = true; control.dataset.roleLocked = '1'; control.title = title; }
				else if (control.dataset.roleLocked) { control.disabled = false; delete control.dataset.roleLocked; control.title = ''; }
			}
		}
	}

	/** Выключить кнопку на время запроса, не снимая блокировки по роли. */
	async function busy(button, action) {
		if (button) button.disabled = true;
		try { return await action(); }
		finally { if (button && !button.dataset.roleLocked) button.disabled = false; }
	}

	// ---------- диалоги ----------

	/**
	 * Диалог подтверждения. phrase — фраза, которую нужно ввести дословно (её же проверяет сервер).
	 * fields — поля ввода [{name, label, value, placeholder, type, hint}]. Возвращает значения или null.
	 */
	function dialog({eyebrow = 'ПОДТВЕРЖДЕНИЕ', title, text = '', phrase = '', fields = [], confirm = 'Продолжить', danger = false, cancel = 'Отмена'}) {
		return new Promise(resolve => {
			const box = document.createElement('dialog');
			box.className = 'dialog small';
			box.innerHTML = `<form method="dialog"><div class="dialog-head"><div><span class="eyebrow">${esc(eyebrow)}</span><h2>${esc(title)}</h2>${text ? `<p>${esc(text)}</p>` : ""}</div><button class="dialog-close" type="button" value="cancel" aria-label="Закрыть">×</button></div>
				<div class="dialog-fields">${fields.map((field, index) => `<label class="form-field"><span>${esc(field.label)}</span>${field.type === "textarea" ? `<textarea data-field="${index}" rows="4">${esc(field.value || "")}</textarea>` : `<input data-field="${index}" type="${esc(field.type || "text")}" value="${esc(field.value || "")}" placeholder="${esc(field.placeholder || "")}" autocomplete="off">`}${field.hint ? `<small>${esc(field.hint)}</small>` : ""}</label>`).join("")}
				${phrase ? `<div class="form-field"><span>Для подтверждения введите</span><div class="dialog-phrase">${esc(phrase)}</div><input data-phrase autocomplete="off" spellcheck="false"></div>` : ""}</div>
				<div class="dialog-actions"><button class="button" type="button" data-cancel>${esc(cancel)}</button><button class="button ${danger ? "danger" : "primary"}" type="submit" data-confirm>${esc(confirm)}</button></div></form>`;
			document.body.append(box);
			const submit = $('[data-confirm]', box), phraseInput = $('[data-phrase]', box);
			const check = () => { if (phraseInput) submit.disabled = phraseInput.value.trim() !== phrase; };
			phraseInput?.addEventListener('input', check);
			fields.forEach((field, index) => { if (field.phraseFrom) $(`[data-field="${index}"]`, box).addEventListener('input', event => { phrase = field.phraseFrom(event.target.value); $('.dialog-phrase', box).textContent = phrase; check(); }); });
			check();
			let result = null;
			$('form', box).addEventListener('submit', event => {
				event.preventDefault();
				if (phraseInput && phraseInput.value.trim() !== phrase) return;
				result = {values: fields.map((_, index) => $(`[data-field="${index}"]`, box).value), phrase: phraseInput ? phraseInput.value.trim() : ''};
				box.close();
			});
			$$('[data-cancel], .dialog-close', box).forEach(button => button.addEventListener('click', () => box.close()));
			box.addEventListener('close', () => { box.remove(); resolve(result); });
			box.showModal();
			($('[data-field]', box) || phraseInput || submit).focus();
		});
	}
	const confirmAction = (title, text, confirm = 'Продолжить', danger = false) => dialog({title, text, confirm, danger}).then(result => result !== null);

	/**
	 * Диалог со своим содержимым: заголовок, тело и панель кнопок заполняет вызывающий.
	 * wide — широкое окно. Возвращает {box, body, actions, close}; onClose вызывается при закрытии любым способом.
	 */
	function modal({eyebrow = '', title, text = '', wide = false, onClose = null}) {
		const box = document.createElement('dialog');
		box.className = `dialog ${wide ? '' : 'small'}`.trim();
		box.innerHTML = `<div class="dialog-head"><div>${eyebrow ? `<span class="eyebrow">${esc(eyebrow)}</span>` : ''}<h2>${esc(title)}</h2>${text ? `<p>${esc(text)}</p>` : ''}</div><button class="dialog-close" type="button" aria-label="Закрыть">×</button></div><div class="dialog-fields" data-body></div><div class="dialog-actions" data-actions></div>`;
		document.body.append(box);
		const close = () => { if (box.open) box.close(); };
		$('.dialog-close', box).addEventListener('click', close);
		box.addEventListener('close', () => { box.remove(); if (onClose) onClose(); });
		box.showModal();
		return {box, body: $('[data-body]', box), actions: $('[data-actions]', box), close};
	}

	// ---------- ход долгой операции ----------

	/**
	 * Любое изменяющее действие, которое длится дольше пары секунд, показывает ход: демон ведёт
	 * текущую операцию сервера, панель отдаёт её на /api/operation. Отдельно подключать каждое
	 * действие не нужно: следит сам api(). Диалог модальный и сам не закрывается — сервер в это
	 * время останавливается, перезапускается или меняется, и править настройки некогда.
	 */
	const UNTRACKED = /^\/api\/(login|logout|session|operation|instances\/select|server\/console|provision(\/|$))/;
	const operation = {pending: 0, delay: null, box: null, watching: false, last: null};

	function trackBegin() {
		operation.pending++;
		if (operation.pending === 1 && !operation.box) operation.delay = setTimeout(openOperation, 1500);
	}
	function trackEnd() {
		operation.pending = Math.max(0, operation.pending - 1);
		if (operation.pending === 0 && !operation.watching) closeOperation();
	}

	function openOperation() {
		operation.delay = null;
		if (operation.box) return;
		const box = document.createElement('dialog');
		box.className = 'dialog small';
		box.innerHTML = `<div class="dialog-head"><div><span class="eyebrow">ВЫПОЛНЯЕТСЯ</span><h2 data-title>Операция на сервере</h2><p>Не закрывайте страницу, пока операция не завершится.</p></div></div>
			<div class="dialog-fields"><ol class="progress-steps" data-steps><li class="running">Передача данных и ожидание ответа сервера</li></ol><div class="operation-meter hidden" data-meter><i></i></div><small class="muted" data-elapsed></small></div>`;
		box.addEventListener('cancel', event => event.preventDefault());
		document.body.append(box);
		operation.box = box;
		box.showModal();
		every('operation', 1000, pollOperation);
	}

	function closeOperation() {
		if (operation.delay) { clearTimeout(operation.delay); operation.delay = null; }
		if (!operation.box) return;
		stopTimer('operation');
		operation.box.close();
		operation.box.remove();
		operation.box = null;
		operation.watching = false;
		operation.last = null;
	}

	async function pollOperation() {
		const data = await api('/api/operation');
		if (!operation.box) return;
		if (data.active) { operation.last = data; renderOperation(data); }
		else if (operation.watching && operation.pending === 0) {
			closeOperation();
			toast('Операция завершена', 'ok');
			emit('operation-finished');
		}
	}

	function renderOperation(data) {
		const box = operation.box, meter = $('[data-meter]', box);
		$('[data-title]', box).textContent = data.title || 'Операция на сервере';
		$('[data-steps]', box).innerHTML = (data.stages || []).map((stage, index) => {
			const state = index < data.stage ? 'done' : (index === data.stage ? 'running' : '');
			const counting = index === data.stage && data.total_bytes > 0;
			const detail = counting ? ` — ${fmtBytes(data.done_bytes || 0)} из ${fmtBytes(data.total_bytes)}` : '';
			return `<li class="${state}"><span>${esc(stage)}${esc(detail)}</span></li>`;
		}).join('');
		const counting = data.total_bytes > 0;
		meter.classList.toggle('hidden', !counting);
		if (counting) $('i', meter).style.width = `${Math.min(100, Math.round((data.done_bytes || 0) * 100 / data.total_bytes))}%`;
		const seconds = Math.floor(data.elapsed_seconds || 0);
		$('[data-elapsed]', box).textContent = `Прошло ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
	}

	/** После перезагрузки страницы: если демон ещё работает над операцией, снова показать её ход. */
	async function resumeOperation() {
		try {
			const data = await api('/api/operation');
			if (!data.active || operation.box) return;
			operation.watching = true;
			openOperation();
			operation.last = data;
			renderOperation(data);
		} catch { /* ход не показывается, сама работа от этого не зависит */ }
	}

	// ---------- живые потоки и таймеры ----------

	const live = {streams: new Map(), timers: new Map()};

	/** Поток SSE; ключ заменяет прежний поток с тем же ключом. page — раздел-владелец, закрывается при уходе. */
	function stream(key, url, {onMessage, onOpen, onError, page = null}) {
		closeStream(key);
		const source = new EventSource(url);
		source.onmessage = event => { let data; try { data = JSON.parse(event.data); } catch { return; } onMessage(data); };
		if (onOpen) source.onopen = onOpen;
		if (onError) source.onerror = onError;
		live.streams.set(key, {source, page});
		return source;
	}
	function closeStream(key) { const item = live.streams.get(key); if (item) { item.source.close(); live.streams.delete(key); } }

	function every(key, ms, action, {page = null, now = true} = {}) {
		stopTimer(key);
		const run = () => { Promise.resolve().then(action).catch(() => {}); };
		live.timers.set(key, {id: setInterval(run, ms), page});
		if (now) run();
	}
	function stopTimer(key) { const item = live.timers.get(key); if (item) { clearInterval(item.id); live.timers.delete(key); } }

	function releasePage(page) {
		for (const [key, item] of live.streams) if (item.page === page) closeStream(key);
		for (const [key, item] of live.timers) if (item.page === page) stopTimer(key);
	}
	function releaseAll() {
		for (const key of [...live.streams.keys()]) closeStream(key);
		for (const key of [...live.timers.keys()]) stopTimer(key);
	}

	// ---------- разделы и вкладки ----------

	const pages = {};
	const pageTitles = {overview: 'Обзор', players: 'Игроки и консоль', logs: 'Журнал и события', content: 'Миры и моды', backups: 'Резервные копии', maintenance: 'Обслуживание', settings: 'Настройки'};
	const activeTab = {};

	function selectPage(name, tab = null, {updateHash = true} = {}) {
		if (!pageTitles[name]) name = 'overview';
		const previous = state.page;
		if (previous !== name) { releasePage(previous); pages[previous]?.leave?.(); }
		state.page = name;
		$$('.page').forEach(element => element.classList.toggle('active', element.id === `page-${name}`));
		$$('.nav-item').forEach(element => element.classList.toggle('active', element.dataset.page === name));
		$('#breadcrumb-page').textContent = pageTitles[name];
		$('#sidebar').classList.remove('open');
		if (tab) selectTab(name, tab, {silent: true});
		if (updateHash) history.replaceState(null, '', `#${name}${activeTab[name] ? `/${activeTab[name]}` : ""}`);
		pages[name]?.enter?.(activeTab[name] || defaultTab(name));
		window.scrollTo({top: 0});
	}

	function defaultTab(group) { return $(`[data-tabs="${group}"] .work-tab`)?.dataset.view || null; }

	function selectTab(group, view, {silent = false} = {}) {
		const tabs = $$(`[data-tabs="${group}"] .work-tab`);
		if (!tabs.some(tab => tab.dataset.view === view)) view = defaultTab(group);
		if (!view) return;
		tabs.forEach(tab => { const active = tab.dataset.view === view; tab.classList.toggle('active', active); tab.setAttribute('aria-selected', String(active)); });
		$$(`[data-panel^="${group}:"]`).forEach(panel => panel.classList.toggle('active', panel.dataset.panel === `${group}:${view}`));
		activeTab[group] = view;
		if (state.page === group) history.replaceState(null, '', `#${group}/${view}`);
		if (!silent) pages[group]?.tab?.(view);
	}

	function routeFromHash() {
		const [page, tab] = location.hash.slice(1).split('/');
		selectPage(page || 'overview', tab || null, {updateHash: !page});
	}

	// ---------- серверы ----------

	async function loadInstances() {
		const data = await api('/api/instances');
		state.instances = data.instances || [];
		state.current = data.current || '';
		state.panelInstance = data.panel_instance || '';
		state.instancesDetail = data.detail || '';
		const current = currentServer();
		$$('.current-server-id').forEach(element => { element.textContent = current ? (current.name || current.id) : (state.current || '—'); });
		$('#topbar-server').textContent = state.current || '—';
		emit('instances', data);
		return data;
	}
	const currentServer = () => state.instances.find(item => item.id === state.current) || null;

	async function selectInstance(id) {
		if (!id || id === state.current) return;
		await api('/api/instances/select', {method: 'POST', body: {instance: id}});
		// Выбор хранится в куке: EventSource не шлёт заголовков, и только перезагрузка
		// гарантирует, что все потоки и страница говорят об одном сервере.
		location.reload();
	}

	// ---------- статус и телеметрия ----------

	function setConnection(ok, text) {
		$('#connection-dot').className = `pulse-dot ${ok ? "ok" : "error"}`;
		$('#connection-text').textContent = text;
	}

	async function refreshStatus() {
		try {
			const status = await api('/api/status');
			state.status = status;
			setConnection(true, 'Связь установлена');
			emit('status', status);
		} catch (error) {
			if (error.status !== 401) { setConnection(false, 'Нет связи с сервером'); emit('status-error', error); }
		}
	}

	function connectTelemetry() {
		stream('telemetry', '/api/telemetry/stream', {onMessage: data => { state.telemetry = data; emit('telemetry', data); }});
	}

	function updateSystemWidget() {
		const status = state.status;
		const dot = $('#system-dot'), title = $('#system-title'), detail = $('#system-detail');
		if (!status) { dot.className = 'pulse-dot error'; title.textContent = 'Состояние неизвестно'; detail.textContent = 'Панель не получила статус'; return; }
		const health = status.health?.state || 'unknown';
		const good = ['ready'].includes(health), warn = ['starting', 'degraded', 'disabled', 'stopped', 'recovering'].includes(health);
		dot.className = `pulse-dot ${good ? "ok" : (warn ? "warn" : "error")}`;
		title.textContent = good ? 'Сервер готов' : (health === 'stopped' ? 'Сервер остановлен' : (healthTitles[health] || 'Требует внимания'));
		detail.textContent = `Проверка · ${fmtShortTime(Date.now() / 1000)}`;
	}

	const healthTitles = {disabled: 'Наблюдение выключено', stopped: 'Остановлен', starting: 'Загружается', ready: 'Готов', degraded: 'Ограниченная проверка', unresponsive: 'Нет ответа', hung: 'Зависание подтверждено', recovering: 'Восстановление', unknown: 'Неизвестно'};
	const healthLevel = state => state === 'ready' ? 'ok' : (['starting', 'recovering', 'degraded', 'disabled', 'stopped'].includes(state) ? 'warn' : 'error');

	// ---------- графики ----------

	/**
	 * Линейный график в SVG. series — [{values:[число|null], cls}], max — верх шкалы.
	 * Линии растягиваются по ширине (preserveAspectRatio=none), толщина не искажается.
	 */
	function lineChart(container, {series, max, labels = [], marks = [], empty = 'Недостаточно измерений', height = 210, unit = ''}) {
		const length = Math.max(0, ...series.map(item => item.values.length));
		if (length < 2) { container.innerHTML = `<div class="chart-empty">${esc(empty)}</div>`; return; }
		const width = 1000, top = 8, bottom = height - 8;
		const x = index => index / (length - 1) * width;
		const y = value => bottom - Math.min(max, Math.max(0, value)) / max * (bottom - top);
		const grid = [0, .25, .5, .75, 1].map(part => `M0 ${top + (bottom - top) * part}H${width}`).join('');
		const paths = series.map(item => {
			let d = '', open = false;
			item.values.forEach((value, index) => {
				if (value === null || value === undefined || Number.isNaN(value)) { open = false; return; }
				d += `${open ? "L" : "M"}${x(index).toFixed(1)} ${y(value).toFixed(1)}`;
				open = true;
			});
			const area = item.area && d ? `<path class="area" d="${d}L${width} ${bottom}L0 ${bottom}Z"/>` : '';
			return `${area}<path class="line ${esc(item.cls)}" d="${d}"/>`;
		}).join('');
		const markup = marks.map(mark => `<rect class="mark ${esc(mark.cls || "")}" x="${(x(mark.index) - 3).toFixed(1)}" y="${bottom - 12}" width="6" height="12"/>`).join('');
		const axis = labels.length ? `<div class="chart-axis">${labels.map(label => `<span>${esc(label)}</span>`).join("")}</div>` : '';
		container.innerHTML = `<div class="chart-scale">шкала до ${esc(Math.round(max))}${esc(unit)}</div><svg class="chart" viewBox="0 0 ${width} ${height}" preserveAspectRatio="none" role="img" aria-label="График"><path class="gridline" d="${grid}"/>${paths}${markup}</svg>${axis}`;
		container.querySelector('.chart').style.height = `${height}px`;
	}

	/** Подписи оси времени: пять меток по краям и между ними. */
	function timeLabels(timestamps, format = fmtShortTime) {
		if (timestamps.length < 2) return [];
		return [0, .25, .5, .75, 1].map(part => format(timestamps[Math.round(part * (timestamps.length - 1))]));
	}

	// ---------- запуск ----------

	function showLogin() {
		releaseAll();
		state.csrf = '';
		$('#panel-view').classList.add('hidden');
		$('#login-view').classList.remove('hidden');
		$('#login-password').focus();
	}

	async function showPanel(session) {
		state.csrf = session.csrf || '';
		state.user = session.user || 'admin';
		state.role = session.role || 'viewer';
		$('#login-view').classList.add('hidden');
		$('#panel-view').classList.remove('hidden');
		$('#user-name').textContent = state.user;
		$('#user-role').textContent = roleTitles[state.role] || state.role;
		$('#user-avatar').textContent = state.user.slice(0, 2);
		applyRoles();
		try { await loadInstances(); } catch (error) { fail(error); }
		resumeOperation();
		every('status', 5000, refreshStatus);
		every('instances', 20000, () => loadInstances().catch(() => {}), {now: false});
		connectTelemetry();
		emit('session', session);
		routeFromHash();
	}

	function tickClock() {
		$('#local-time').textContent = new Date().toLocaleDateString('ru-RU', {weekday: 'long', day: 'numeric', month: 'long', year: 'numeric'});
	}

	document.addEventListener('DOMContentLoaded', async () => {
		tickClock();
		setInterval(tickClock, 60000);

		$$('.nav-item').forEach(button => button.addEventListener('click', () => selectPage(button.dataset.page)));
		$$('[data-tabs] .work-tab').forEach(tab => tab.addEventListener('click', () => selectTab(tab.closest('[data-tabs]').dataset.tabs, tab.dataset.view)));
		document.addEventListener('click', event => {
			const link = event.target.closest('[data-goto]');
			if (!link) return;
			const [page, tab] = link.dataset.goto.split(':');
			selectPage(page, tab);
		});
		$('#menu-toggle').addEventListener('click', () => $('#sidebar').classList.toggle('open'));
		window.addEventListener('hashchange', () => { if (!$('#panel-view').classList.contains('hidden')) routeFromHash(); });
		on('status', updateSystemWidget);
		on('status-error', updateSystemWidget);

		$('#login-form').addEventListener('submit', async event => {
			event.preventDefault();
			$('#login-error').textContent = '';
			const button = event.submitter;
			try {
				const data = await busy(button, () => api('/api/login', {method: 'POST', allowAnonymous: true, body: {username: $('#login-username').value.trim(), password: $('#login-password').value, otp: $('#login-otp').value.trim()}}));
				$('#login-password').value = '';
				$('#login-otp').value = '';
				await showPanel({csrf: data.csrf, user: data.user, role: data.role});
			} catch (error) { $('#login-error').textContent = error.message; }
		});
		$('#logout').addEventListener('click', async () => {
			try { await api('/api/logout', {method: 'POST'}); } catch { /* сессия и так закончится */ }
			showLogin();
		});

		try {
			const session = await api('/api/session', {allowAnonymous: true});
			if (session.authenticated) await showPanel(session);
			else showLogin();
		} catch { showLogin(); }
	});

	return {
		$, $$, esc, fmtBytes, fmtDuration, fmtDateTime, fmtTime, fmtShortTime, fmtAgo, fmtDay, toDate,
		state, can, roleTitles, on, emit, toast, fail, api, applyRoles, busy, dialog, confirmAction, modal,
		stream, closeStream, every, stopTimer, pages, selectPage, selectTab,
		loadInstances, currentServer, selectInstance, refreshStatus, healthTitles, healthLevel,
		lineChart, timeLabels,
	};
})();
