'use strict';

// Ядро интерфейса: запросы к API, сессия, права, диалоги, разделы и живые потоки.
// Разделы (overview.js, players.js …) регистрируют себя в VS.pages и не знают друг о друге.

const VS = (() => {
	const $ = (selector, root = document) => root.querySelector(selector);
	const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

	const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', '\'': '&#39;'}[char]));

	const fmtBytes = value => {
		const n = Number(value) || 0;
		if (!n) return `0 ${I18N.t('units.b')}`;
		const units = ['units.b', 'units.kb', 'units.mb', 'units.gb', 'units.tb'].map(key => I18N.t(key));
		const i = Math.min(Math.floor(Math.log(n) / Math.log(1024)), units.length - 1);
		return `${(n / 1024 ** i).toFixed(i ? 1 : 0)} ${units[i]}`;
	};

	const fmtDuration = value => {
		const s = Math.max(0, Number(value) || 0);
		const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
		if (d) return `${d} ${I18N.t('units.d')} ${h} ${I18N.t('units.h')}`;
		if (h) return `${h} ${I18N.t('units.h')} ${m} ${I18N.t('units.min')}`;
		return `${m} ${I18N.t('units.min')}`;
	};

	/** Секунды Unix или ISO-строка — в Date. */
	const toDate = value => {
		if (value === null || value === undefined || value === '') return null;
		const date = typeof value === 'number' ? new Date(value * 1000) : new Date(value);
		return Number.isNaN(date.getTime()) ? null : date;
	};
	const fmtDateTime = value => { const d = toDate(value); return d ? d.toLocaleString(I18N.locale(), {day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit'}) : '—'; };
	const fmtTime = value => { const d = toDate(value); return d ? d.toLocaleTimeString(I18N.locale(), {hour: '2-digit', minute: '2-digit', second: '2-digit'}) : '—'; };
	const fmtShortTime = value => { const d = toDate(value); return d ? d.toLocaleTimeString(I18N.locale(), {hour: '2-digit', minute: '2-digit'}) : '—'; };
	const fmtAgo = value => {
		const d = toDate(value);
		if (!d) return '—';
		const s = Math.round((Date.now() - d.getTime()) / 1000);
		if (s < 60) return I18N.t('units.agoJustNow');
		if (s < 3600) return I18N.t('units.agoMin', {n: Math.floor(s / 60)});
		if (s < 86400) return I18N.t('units.agoHour', {n: Math.floor(s / 3600)});
		return fmtDateTime(value);
	};
	const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
	const fmtDay = value => {
		const d = toDate(value);
		if (!d) return '';
		const today = new Date();
		const yesterday = new Date(today.getTime() - 86400000);
		if (sameDay(d, today)) return I18N.t('common.today');
		if (sameDay(d, yesterday)) return I18N.t('common.yesterday');
		return d.toLocaleDateString(I18N.locale(), {day: 'numeric', month: 'long'});
	};

	// ---------- состояние ----------

	const state = {csrf: '', user: '', role: 'viewer', page: 'overview', instances: [], current: '', status: null, telemetry: null};
	const rank = {viewer: 0, operator: 1, admin: 2};
	const roleTitles = {viewer: I18N.t('role.viewer'), operator: I18N.t('role.operator'), admin: I18N.t('role.admin')};
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
		catch { setConnection(false, I18N.t('core.noConnectionPanel')); throw new ApiError(I18N.t('core.panelUnavailableCheckConnection'), 0); }
		let data = {};
		if (response.ok && options.response === 'blob') return response.blob();
		try { data = await response.json(); } catch { /* пустое тело */ }
		if (response.status === 401 && !options.allowAnonymous) { showLogin(); throw new ApiError(I18N.t('core.loginRequired'), 401); }
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
			const title = I18N.t('core.requiresRole', {role: roleTitles[element.dataset.minRole]});
			const controls = element.matches('button, input, select, textarea') ? [element] : $$('button, input, select, textarea', element);
			if (element.matches('label.button')) element.classList.toggle('is-disabled', !allowed);
			for (const control of controls) {
				if (!allowed) { control.disabled = true; control.dataset.roleLocked = '1'; control.title = title; }
				else if (control.dataset.roleLocked) { control.disabled = !!control.dataset.busyLocked; delete control.dataset.roleLocked; control.title = ''; }
			}
		}
	}

	/** Выключить кнопку на время запроса, не снимая блокировки по роли. */
	async function busy(button, action) {
		if (button) button.disabled = true;
		try { return await action(); }
		finally { if (button && !button.dataset.roleLocked && !button.dataset.busyLocked) button.disabled = false; }
	}

	// ---------- диалоги ----------

	/**
	 * Диалог подтверждения. phrase — фраза, которую нужно ввести дословно (её же проверяет сервер).
	 * fields — поля ввода [{name, label, value, placeholder, type, hint}]. Возвращает значения или null.
	 */
	function dialog({eyebrow = I18N.t('core.confirmationEyebrow'), title, text = '', phrase = '', fields = [], confirm = I18N.t('core.continue'), danger = false, cancel = I18N.t('core.cancel')}) {
		return new Promise(resolve => {
			const box = document.createElement('dialog');
			box.className = 'dialog small';
			box.innerHTML = `<form method="dialog"><div class="dialog-head"><div><span class="eyebrow">${esc(eyebrow)}</span><h2>${esc(title)}</h2>${text ? `<p>${esc(text)}</p>` : ''}</div><button class="dialog-close" type="button" value="cancel" aria-label="${esc(I18N.t('common.close'))}">×</button></div>
				<div class="dialog-fields">${fields.map((field, index) => `<label class="form-field"><span>${esc(field.label)}</span>${field.type === 'textarea' ? `<textarea data-field="${index}" rows="4">${esc(field.value || '')}</textarea>` : `<input data-field="${index}" type="${esc(field.type || 'text')}" value="${esc(field.value || '')}" placeholder="${esc(field.placeholder || '')}" autocomplete="off">`}${field.hint ? `<small>${esc(field.hint)}</small>` : ''}</label>`).join('')}
				${phrase ? `<div class="form-field"><span>${esc(I18N.t('core.typeToConfirm'))}</span><div class="dialog-phrase">${esc(phrase)}</div><input data-phrase autocomplete="off" spellcheck="false"></div>` : ''}</div>
				<div class="dialog-actions"><button class="button" type="button" data-cancel>${esc(cancel)}</button><button class="button ${danger ? 'danger' : 'primary'}" type="submit" data-confirm>${esc(confirm)}</button></div></form>`;
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
	const confirmAction = (title, text, confirm = I18N.t('core.continue'), danger = false) => dialog({title, text, confirm, danger}).then(result => result !== null);

	/**
	 * Диалог со своим содержимым: заголовок, тело и панель кнопок заполняет вызывающий.
	 * wide — широкое окно. Возвращает {box, body, actions, close}; onClose вызывается при закрытии любым способом.
	 */
	function modal({eyebrow = '', title, text = '', wide = false, onClose = null}) {
		const box = document.createElement('dialog');
		box.className = `dialog ${wide ? '' : 'small'}`.trim();
		box.innerHTML = `<div class="dialog-head"><div>${eyebrow ? `<span class="eyebrow">${esc(eyebrow)}</span>` : ''}<h2>${esc(title)}</h2>${text ? `<p>${esc(text)}</p>` : ''}</div><button class="dialog-close" type="button" aria-label="${esc(I18N.t('common.close'))}">×</button></div><div class="dialog-fields" data-body></div><div class="dialog-actions" data-actions></div>`;
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
	// collapsed — ход свёрнут оператором и до конца запросов показывается в строке операций, а не
	// диалогом. Пока идёт задача браузера (task), диалог не открывается вовсе: её ход уже в строке.
	const operation = {pending: 0, delay: null, box: null, watching: false, last: null, collapsed: false};

	function trackBegin() {
		operation.pending++;
		if (operation.pending === 1 && !operation.box && !operation.collapsed && !local.task) operation.delay = setTimeout(openOperation, 1500);
		renderLocal();
	}
	function trackEnd() {
		operation.pending = Math.max(0, operation.pending - 1);
		if (operation.pending === 0 && !operation.watching) {
			closeOperation();
			operation.collapsed = false;
		}
		renderLocal();
	}

	function openOperation() {
		operation.delay = null;
		if (operation.box) return;
		const box = document.createElement('dialog');
		box.className = 'dialog small';
		box.innerHTML = `<div class="dialog-head"><div><span class="eyebrow">${esc(I18N.t('core.inProgress'))}</span><h2 data-title>${esc(I18N.t('core.serverOperation'))}</h2><p>${esc(I18N.t('core.collapseHint'))}</p></div></div>
			<div class="dialog-fields"><ol class="progress-steps" data-steps><li class="running">${esc(I18N.t('core.sendingData'))}</li></ol><div class="operation-meter hidden" data-meter><i></i></div><small class="muted" data-elapsed></small></div>
			<div class="dialog-actions"><button class="button" type="button" data-collapse>${esc(I18N.t('core.collapseToStatusBar'))}</button></div>`;
		box.addEventListener('cancel', event => event.preventDefault());
		$('[data-collapse]', box).addEventListener('click', () => {
			operation.collapsed = operation.pending > 0;
			closeOperation();
			renderLocal();
		});
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
			toast(I18N.t('core.operationDone'), 'ok');
			emit('operation-finished');
		}
	}

	function renderOperation(data) {
		const box = operation.box, meter = $('[data-meter]', box);
		$('[data-title]', box).textContent = data.title || I18N.t('core.serverOperation');
		$('[data-steps]', box).innerHTML = (data.stages || []).map((stage, index) => {
			const state = index < data.stage ? 'done' : (index === data.stage ? 'running' : '');
			const counting = index === data.stage && data.total_bytes > 0;
			const detail = counting ? I18N.t('core.progressDetail', {done: fmtBytes(data.done_bytes || 0), total: fmtBytes(data.total_bytes)}) : '';
			return `<li class="${state}"><span>${esc(stage)}${esc(detail)}</span></li>`;
		}).join('');
		const counting = data.total_bytes > 0;
		meter.classList.toggle('hidden', !counting);
		if (counting) $('i', meter).style.width = `${Math.min(100, Math.round((data.done_bytes || 0) * 100 / data.total_bytes))}%`;
		const seconds = Math.floor(data.elapsed_seconds || 0);
		$('[data-elapsed]', box).textContent = I18N.t('core.elapsedTime', {time: `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`});
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

	// ---------- строка операций ----------

	/**
	 * Одна строка под верхней панелью для долгих операций (интерфейс 2.1 §1): занятость сервера
	 * читается из status.maintenance, не только из операций этого браузера. Пока сервер занят,
	 * изменяющие кнопки (data-mutates) неактивны.
	 */
	// Ключи — имена назначения аренды (LeasePurposeWire): maintenance, watchdog-recovery, update, provision.
	const activityOutcomes = {
		update: () => api('/api/updates').then(data => (data.history || [])[0]),
		maintenance: () => api('/api/automation').then(data => (data.runs || [])[0]),
	};
	const activityGoto = {
		maintenance: ['maintenance', 'schedule'],
		'watchdog-recovery': ['maintenance', 'watchdog'],
		update: ['maintenance', 'updates'],
	};
	const ACTIVITY_FAILED = ['failed', 'interrupted', 'cancelled'];
	let activeMaintenance = null;
	let activityHideTimer = null;
	let mutationsLocked = false;

	/**
	 * Блокировка изменяющих кнопок на время аренды. Кнопка, закрытая по роли, остаётся закрытой и
	 * после снятия: занятость и роль — два независимых замка (data-busy-locked и data-role-locked).
	 */
	function applyBusyLock(root = document) {
		for (const element of $$('[data-mutates]', root)) {
			if (mutationsLocked) {
				if (!element.dataset.busyLocked) {
					element.dataset.busyLocked = '1';
					element.disabled = true;
					element.title = I18N.t('core.serverBusy');
				}
			} else if (element.dataset.busyLocked) {
				delete element.dataset.busyLocked;
				const role = element.dataset.roleLocked;
				element.disabled = !!role;
				element.title = role ? I18N.t('core.requiresRole', {role: roleTitles[element.dataset.minRole] || ''}) : '';
			}
		}
	}

	function setBusyLock(locked) {
		mutationsLocked = locked;
		applyBusyLock();
	}

	async function pollActivity() {
		const bar = $('#activity-bar');
		if (!bar || bar.classList.contains('hidden') || (!activeMaintenance && local.task)) return;
		try {
			const data = await api('/api/operation');
			const stage = data.active ? (data.stages || [])[data.stage] : null;
			$('#activity-stage').textContent = stage ? `· ${stage}` : '';
			const counting = data.active && data.total_bytes > 0;
			$('#activity-track').hidden = !counting;
			if (counting) $('#activity-meter').style.width = `${Math.min(100, Math.round((data.done_bytes || 0) * 100 / data.total_bytes))}%`;
		} catch { /* аренда всё равно источник истины, строка просто не детализируется */ }
	}

	async function showActivityOutcome(maintenance) {
		const bar = $('#activity-bar');
		let ok = true;
		try {
			const record = await (activityOutcomes[maintenance.purpose]?.() ?? Promise.resolve(null));
			if (record?.status) ok = !ACTIVITY_FAILED.includes(record.status);
		} catch { /* оценка недоступна — считаем успехом по умолчанию */ }
		bar.classList.remove('hidden', 'running');
		bar.classList.add(ok ? 'ok' : 'error');
		$('#activity-dot').className = `pulse-dot ${ok ? 'ok' : 'error'}`;
		$('#activity-title').textContent = I18N.t(ok ? 'core.done' : 'core.errorShort');
		$('#activity-stage').textContent = '';
		$('#activity-track').hidden = true;
		$('#activity-time').textContent = '';
		$('#activity-more').hidden = true;
		emit('operation-finished');
		clearTimeout(activityHideTimer);
		activityHideTimer = setTimeout(() => bar.classList.add('hidden'), 10000);
	}

	function renderActivityBar(status) {
		const bar = $('#activity-bar');
		if (!bar) return;
		const maintenance = status?.maintenance || null;
		$('#nav-maintenance').hidden = !maintenance;
		updateMenuBadge();
		if (maintenance) {
			clearTimeout(activityHideTimer);
			activeMaintenance = maintenance;
			bar.classList.remove('hidden', 'ok', 'error');
			bar.classList.add('running');
			$('#activity-dot').className = 'pulse-dot warn';
			$('#activity-title').textContent = maintenance.title;
			$('#activity-time').textContent = fmtAgo(maintenance.started_at);
			$('#activity-more').hidden = !activityGoto[maintenance.purpose];
			setBusyLock(true);
			every('activity', 2000, pollActivity);
		} else {
			setBusyLock(!!local.task);
			stopTimer('activity');
			if (activeMaintenance) { showActivityOutcome(activeMaintenance); activeMaintenance = null; }
			else renderLocal();
		}
	}

	// ---------- работа этого браузера ----------

	/**
	 * Строка операций показывает и то, чего сервер арендой не видит: свёрнутый ход запроса и задачу
	 * из нескольких запросов подряд (установка модов из сборки). Аренда сервера важнее: пока она
	 * есть, строка занята ею. Задача живёт в этой вкладке — уход со страницы спрашивает подтверждения.
	 */
	const local = {task: null, outcome: null, hideTimer: null, shown: false};

	function renderLocal() {
		const bar = $('#activity-bar');
		if (!bar || activeMaintenance) return;
		clearTimeout(local.hideTimer);
		const task = local.task;
		if (task || (operation.collapsed && operation.pending > 0)) {
			bar.classList.remove('hidden', 'ok', 'error');
			bar.classList.add('running');
			$('#activity-dot').className = 'pulse-dot warn';
			$('#activity-more').hidden = true;
			if (task) {
				$('#activity-title').textContent = task.title;
				$('#activity-stage').textContent = task.total ? `· ${Math.min(task.done + 1, task.total)}/${task.total}${task.label ? ` · ${task.label}` : ''}` : '';
				$('#activity-track').hidden = !task.total;
				if (task.total) $('#activity-meter').style.width = `${Math.round(task.done * 100 / task.total)}%`;
				$('#activity-time').textContent = fmtAgo(task.started / 1000);
				stopTimer('activity');
			} else {
				$('#activity-title').textContent = operation.last?.title || I18N.t('core.serverOperation');
				$('#activity-time').textContent = '';
				every('activity', 2000, pollActivity);
			}
			setBusyLock(!!task);
			local.shown = true;
			return;
		}
		// Строкой не владеем — не трогать: в ней может догорать итог обслуживания.
		if (!local.shown) return;
		stopTimer('activity');
		setBusyLock(false);
		const outcome = local.outcome;
		if (!outcome) { bar.classList.add('hidden'); local.shown = false; return; }
		bar.classList.remove('hidden', 'running', 'ok', 'error');
		bar.classList.add(outcome.ok ? 'ok' : 'error');
		$('#activity-dot').className = `pulse-dot ${outcome.ok ? 'ok' : 'error'}`;
		$('#activity-title').textContent = `${I18N.t(outcome.ok ? 'core.done' : 'core.errorShort')} · ${outcome.title}`;
		$('#activity-stage').textContent = outcome.summary ? `· ${outcome.summary}` : '';
		$('#activity-track').hidden = true;
		$('#activity-time').textContent = '';
		$('#activity-more').hidden = true;
		local.hideTimer = setTimeout(() => { local.outcome = null; renderLocal(); }, outcome.ok ? 10000 : 60000);
	}

	/**
	 * Начать задачу браузера: title — что делается. step(label, done, total) — ход, finish(ok, summary) —
	 * итог, который строка показывает ещё какое-то время (ошибка — дольше).
	 */
	function task(title) {
		const job = {title, label: '', done: 0, total: 0, started: Date.now()};
		local.task = job;
		local.outcome = null;
		renderLocal();
		return {
			step(label, done, total) {
				Object.assign(job, {label, done, total});
				if (local.task === job) renderLocal();
			},
			finish(ok, summary = '') {
				if (local.task !== job) return;
				local.task = null;
				local.outcome = {ok, title, summary};
				renderLocal();
				emit('operation-finished');
			},
		};
	}

	window.addEventListener('beforeunload', event => {
		if (local.task) event.preventDefault();
	});

	document.addEventListener('DOMContentLoaded', () => {
		$('#activity-more')?.addEventListener('click', () => {
			const goto = activityGoto[state.status?.maintenance?.purpose || ''];
			if (goto) selectPage(goto[0], goto[1]);
		});
	});

	on('status', renderActivityBar);

	// ---------- значки меню ----------

	/** Отметка «просмотрено до» — удобство одного зрителя; без неё значок берёт события за сутки. */
	const eventsSeenKey = () => `vs-events-seen:${state.current || ''}`;
	const eventsSeen = () => { try { return Number(localStorage.getItem(eventsSeenKey())) || 0; } catch { return 0; } };
	function markEventsSeen() {
		try { localStorage.setItem(eventsSeenKey(), String(Date.now() / 1000)); } catch { /* приватный режим */ }
		$('#nav-logs').hidden = true;
		updateMenuBadge();
	}

	async function pollLogsBadge() {
		if (!can('viewer')) return;
		const data = await api('/api/events?limit=60&severity=all');
		const since = eventsSeen() || Date.now() / 1000 - 86400;
		const fresh = (data.events || []).filter(item => (item.ts || 0) > since);
		const badge = $('#nav-logs');
		if (!fresh.length) { badge.hidden = true; return; }
		const worst = fresh.some(item => item.severity === 'error' || item.severity === 'critical') ? 'error' : 'warn';
		badge.hidden = false;
		badge.className = `nav-badge ${worst}`;
		badge.textContent = fresh.length > 9 ? '9+' : String(fresh.length);
	}

	async function pollContentBadge() {
		if (!can('operator')) return;
		const data = await api('/api/mods');
		const issues = (data.mods || []).some(mod => (mod.dependency_issues || []).length);
		$('#nav-content').hidden = !issues;
	}

	async function pollBackupsBadge() {
		if (!can('viewer')) return;
		const data = await api('/api/backups');
		const latest = (data.backups || []).reduce((max, item) => Math.max(max, item.modified || 0), 0);
		const stale = !latest || (Date.now() / 1000 - latest) > 48 * 3600;
		$('#nav-backups').hidden = !stale;
	}

	async function pollSettingsBadge() {
		if (!can('admin')) return;
		const data = await api('/api/config');
		const applyState = data.apply_state?.state;
		$('#nav-settings').hidden = applyState !== 'restart_required' && applyState !== 'start_required';
	}

	const badgePollers = [
		['badge-logs', 60000, pollLogsBadge],
		['badge-content', 300000, pollContentBadge],
		['badge-backups', 600000, pollBackupsBadge],
		['badge-settings', 60000, pollSettingsBadge],
	];

	/** Сводная точка на «☰» — самый высокий уровень среди значков меню, чтобы на телефоне было видно, что внутри что-то есть. */
	function updateMenuBadge() {
		const visible = $$('.nav-badge:not([hidden])');
		const toggle = $('#menu-toggle');
		toggle.classList.remove('has-badge', 'warn', 'error');
		if (!visible.length) return;
		toggle.classList.add('has-badge', visible.some(item => item.classList.contains('error')) ? 'error' : 'warn');
	}

	function startBadgePolling() {
		for (const [key, ms, action] of badgePollers) every(key, ms, () => action().then(updateMenuBadge).catch(() => {}));
	}
	function stopBadgePolling() {
		for (const [key] of badgePollers) stopTimer(key);
	}
	function resetBadges() {
		$$('.nav-badge').forEach(element => { if (element.id !== 'nav-players' && element.id !== 'nav-maintenance') element.hidden = true; });
		startBadgePolling();
	}

	// Вкладка в фоне не опрашивает; при возврате — сразу проверить, не устарел ли значок.
	document.addEventListener('visibilitychange', () => {
		if (document.visibilityState === 'hidden') stopBadgePolling();
		else startBadgePolling();
	});

	on('instances', resetBadges);
	document.addEventListener('click', event => {
		if (event.target.closest('[data-goto="logs:events"], [data-goto="logs:stream"]')) markEventsSeen();
	});

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
	const pageTitles = {overview: I18N.t('nav.overview'), players: I18N.t('nav.players'), logs: I18N.t('nav.logs'), content: I18N.t('nav.content'), backups: I18N.t('nav.backups'), maintenance: I18N.t('nav.maintenance'), settings: I18N.t('nav.settings')};
	const activeTab = {};

	// ---------- мобильное меню ----------

	const isMenuOpen = () => $('#sidebar').classList.contains('open');
	function openMenu() {
		$('#sidebar').classList.add('open');
		$('#sidebar-backdrop').hidden = false;
		$('#menu-toggle').setAttribute('aria-expanded', 'true');
		document.body.classList.add('menu-open');
		$('.nav-item.active', $('#sidebar'))?.focus();
	}
	function closeMenu() {
		if (!isMenuOpen()) return;
		$('#sidebar').classList.remove('open');
		$('#sidebar-backdrop').hidden = true;
		$('#menu-toggle').setAttribute('aria-expanded', 'false');
		document.body.classList.remove('menu-open');
		$('#menu-toggle').focus();
	}

	function selectPage(name, tab = null, {updateHash = true} = {}) {
		if (!pageTitles[name]) name = 'overview';
		const previous = state.page;
		if (previous !== name) { releasePage(previous); pages[previous]?.leave?.(); }
		state.page = name;
		$$('.page').forEach(element => element.classList.toggle('active', element.id === `page-${name}`));
		$$('.nav-item').forEach(element => element.classList.toggle('active', element.dataset.page === name));
		$('#breadcrumb-page').textContent = pageTitles[name];
		closeMenu();
		if (tab) selectTab(name, tab, {silent: true});
		if (updateHash) history.replaceState(null, '', `#${name}${activeTab[name] ? `/${activeTab[name]}` : ''}`);
		pages[name]?.enter?.(activeTab[name] || defaultTab(name));
		window.scrollTo({top: 0});
	}

	function defaultTab(group) { return $(`[data-tabs="${group}"] .work-tab`)?.dataset.view || null; }

	function selectTab(group, view, {silent = false} = {}) {
		const tabs = $$(`[data-tabs="${group}"] .work-tab`);
		if (!tabs.some(tab => tab.dataset.view === view)) view = defaultTab(group);
		if (!view) return;
		tabs.forEach(tab => { const active = tab.dataset.view === view; tab.classList.toggle('active', active); tab.setAttribute('aria-selected', String(active)); if (active) tab.scrollIntoView({inline: 'center', block: 'nearest'}); });
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
		$('#connection-dot').className = `pulse-dot ${ok ? 'ok' : 'error'}`;
		$('#connection-text').textContent = text;
	}

	async function refreshStatus() {
		try {
			const status = await api('/api/status');
			state.status = status;
			setConnection(true, I18N.t('core.connectionOk'));
			emit('status', status);
		} catch (error) {
			if (error.status !== 401) { setConnection(false, I18N.t('core.noConnectionServer')); emit('status-error', error); }
		}
	}

	function connectTelemetry() {
		stream('telemetry', '/api/telemetry/stream', {onMessage: data => { state.telemetry = data; emit('telemetry', data); }});
	}

	function updateSystemWidget() {
		const status = state.status;
		const dot = $('#system-dot'), title = $('#system-title'), detail = $('#system-detail');
		if (!status) { dot.className = 'pulse-dot error'; title.textContent = I18N.t('core.stateUnknown'); detail.textContent = I18N.t('core.noStatus'); return; }
		const health = status.health?.state || 'unknown';
		const good = ['ready'].includes(health), warn = ['starting', 'degraded', 'disabled', 'stopped', 'recovering'].includes(health);
		dot.className = `pulse-dot ${good ? 'ok' : (warn ? 'warn' : 'error')}`;
		title.textContent = good ? I18N.t('core.serverReady') : (health === 'stopped' ? I18N.t('core.serverStopped') : (healthTitles[health] || I18N.t('core.needsAttention')));
		detail.textContent = I18N.t('core.checkedAt', {time: fmtShortTime(Date.now() / 1000)});
	}

	const healthTitles = Object.fromEntries(['disabled', 'stopped', 'starting', 'ready', 'degraded', 'unresponsive', 'hung', 'recovering', 'unknown'].map(key => [key, I18N.t(`health.${key}`)]));
	const healthLevel = state => state === 'ready' ? 'ok' : (['starting', 'recovering', 'degraded', 'disabled', 'stopped'].includes(state) ? 'warn' : 'error');

	// ---------- графики ----------

	/**
	 * Линейный график в SVG. series — [{values:[число|null], cls}], max — верх шкалы.
	 * Линии растягиваются по ширине (preserveAspectRatio=none), толщина не искажается.
	 */
	const chartResize = new ResizeObserver(entries => {
		for (const entry of entries) {
			const container = entry.target;
			if (container.dataset.redrawing) continue;
			const args = container.vsChartArgs;
			if (args) { container.dataset.redrawing = '1'; lineChart(container, args); delete container.dataset.redrawing; }
		}
	});

	/**
	 * Перерисовывает график при смене ширины контейнера (поворот экрана, § 6): последние данные
	 * запоминаются на самом контейнере, а не в состоянии страницы.
	 */
	function lineChart(container, options) {
		container.vsChartArgs = options;
		chartResize.observe(container);
		const {series, max, labels = [], marks = [], empty = I18N.t('core.notEnoughData'), height = 210, unit = ''} = options;
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
				d += `${open ? 'L' : 'M'}${x(index).toFixed(1)} ${y(value).toFixed(1)}`;
				open = true;
			});
			const area = item.area && d ? `<path class="area" d="${d}L${width} ${bottom}L0 ${bottom}Z"/>` : '';
			return `${area}<path class="line ${esc(item.cls)}" d="${d}"/>`;
		}).join('');
		const markup = marks.map(mark => `<rect class="mark ${esc(mark.cls || '')}" x="${(x(mark.index) - 3).toFixed(1)}" y="${bottom - 12}" width="6" height="12"/>`).join('');
		const axis = labels.length ? `<div class="chart-axis">${labels.map(label => `<span>${esc(label)}</span>`).join('')}</div>` : '';
		container.innerHTML = `<div class="chart-scale">${esc(I18N.t('core.chartScale', {max: Math.round(max), unit}))}</div><svg class="chart" viewBox="0 0 ${width} ${height}" preserveAspectRatio="none" role="img" aria-label="${esc(I18N.t('core.chartAriaLabel'))}"><path class="gridline" d="${grid}"/>${paths}${markup}</svg>${axis}`;
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
		startBadgePolling();
		connectTelemetry();
		emit('session', session);
		routeFromHash();
	}

	function tickClock() {
		$('#local-time').textContent = new Date().toLocaleDateString(I18N.locale(), {weekday: 'long', day: 'numeric', month: 'long', year: 'numeric'});
	}

	function initLangSwitch() {
		const buttons = $$('[data-lang]');
		buttons.forEach(button => button.classList.toggle('active', button.dataset.lang === I18N.getLang()));
		buttons.forEach(button => button.addEventListener('click', () => I18N.setLang(button.dataset.lang)));
	}

	document.addEventListener('DOMContentLoaded', async () => {
		initLangSwitch();
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
		$('#menu-toggle').addEventListener('click', () => (isMenuOpen() ? closeMenu() : openMenu()));
		$('#sidebar-backdrop').addEventListener('click', closeMenu);
		document.addEventListener('keydown', event => { if (event.key === 'Escape' && isMenuOpen()) closeMenu(); });
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
		state, can, roleTitles, on, emit, toast, fail, api, applyRoles, applyBusyLock, busy, dialog, confirmAction, modal, task,
		stream, closeStream, every, stopTimer, pages, selectPage, selectTab,
		loadInstances, currentServer, selectInstance, refreshStatus, healthTitles, healthLevel,
		lineChart, timeLabels,
	};
})();
