'use strict';

// Настройки: serverconfig.json со схемой, диффом, откатом и состоянием применения; живая
// конфигурация мира, шаблон нового мира и конфигурации модов; учётные записи и TOTP; уведомления.

(() => {
	const {$, $$, esc, fmtBytes, fmtDateTime, state, api, toast, fail, busy, dialog} = VS;

	let config = {config: null, revision: '', schema: null, backups: [], apply: null};
	let worldRevision = null;
	let modRevision = null;

	// ---------- serverconfig.json ----------

	function field(schema, value) {
		const common = `data-key="${esc(schema.key)}" data-type="${esc(schema.type)}" data-nullable="${schema.nullable ? "1" : "0"}"`;
		const apply = config.apply;
		const tracked = (apply?.changed_keys || []).includes(schema.key);
		const pending = tracked && ['restart_required', 'start_required'].includes(apply?.state);
		const mark = tracked && apply?.state === 'applied' ? '<b class="setting-state applied">Применено</b>' : (pending ? `<b class="setting-state pending">${apply.state === "restart_required" ? "Нужен перезапуск" : "Ждёт запуска"}</b>` : '');
		const restart = schema.restart_required ? '<i title="Изменение требует перезапуска сервера">↻</i>' : '';
		if (schema.type === 'boolean') {
			return `<label class="config-field checkbox${pending ? " pending" : ""}"><input type="checkbox" ${common} ${value ? "checked" : ""}><div><span>${esc(schema.label)} ${restart} ${mark}</span><small>${esc(schema.description || "")}</small></div></label>`;
		}
		let control;
		if (schema.type === 'select') control = `<select ${common}>${(schema.options || []).map(option => `<option value="${esc(JSON.stringify(option.value))}" ${option.value === value ? "selected" : ""}>${esc(option.label)}</option>`).join("")}</select>`;
		else {
			const numeric = ['integer', 'number'].includes(schema.type);
			control = `<input type="${numeric ? "number" : "text"}" ${common} value="${esc(value ?? "")}" ${schema.min != null ? `min="${esc(schema.min)}"` : ""} ${schema.max != null ? `max="${esc(schema.max)}"` : ""} ${numeric ? `step="${schema.type === "number" ? "any" : "1"}"` : ""} ${schema.maximum_length ? `maxlength="${esc(schema.maximum_length)}"` : ""}>`;
		}
		return `<label class="config-field${pending ? " pending" : ""}"><span>${esc(schema.label)} ${restart} ${mark}</span>${control}<small>${esc(schema.description || "")}</small></label>`;
	}

	function renderForm() {
		const schema = config.schema;
		const form = $('#config-form');
		if (!schema || !Array.isArray(schema.categories) || !Array.isArray(schema.fields)) {
			form.innerHTML = '<div class="notice error">Сервер не передал схему настроек. Отредактируйте параметры в расширенном JSON.</div>';
			$('#config-json').value = JSON.stringify(config.config || {}, null, 2);
			return;
		}
		const groups = new Map(schema.categories.map(category => [category.id, {...category, fields: []}]));
		schema.fields.forEach(item => groups.get(item.category)?.fields.push(item));
		form.innerHTML = [...groups.values()].filter(group => group.fields.length).map(group => `<fieldset class="config-group"><legend>${esc(group.label)}</legend><div class="config-grid">${group.fields.map(item => field(item, config.config[item.key])).join("")}</div></fieldset>`).join('');
		$('#config-json').value = JSON.stringify(config.config, null, 2);
		if (!VS.can('admin')) $$('#config-form input, #config-form select').forEach(control => { control.disabled = true; });
	}

	function renderApply() {
		const apply = config.apply;
		const box = $('#config-apply');
		if (!apply) { box.classList.add('hidden'); return; }
		const count = (apply.changed_keys || []).length;
		const [title, detail] = {
			applied: ['Настройки применены', 'Текущий процесс сервера запущен с сохранённой конфигурацией.'],
			restart_required: ['Требуется перезапуск сервера', 'Файл сохранён, но работающий сервер использует прежние значения.'],
			start_required: ['Настройки применятся при запуске', 'Сервер остановлен и прочитает конфигурацию при следующем запуске.'],
		}[apply.state] || ['Состояние настроек неизвестно', apply.detail || 'Обновите данные или проверьте сервер.'];
		box.className = `apply-state ${esc(apply.state || "unknown")}`;
		$('#config-apply-title').textContent = title;
		$('#config-apply-detail').textContent = `${detail}${count ? ` Изменено параметров: ${count}.` : ""}`;
		const action = $('#config-apply-action');
		const needed = ['restart_required', 'start_required'].includes(apply.state);
		action.classList.toggle('hidden', !needed);
		action.dataset.service = apply.state === 'restart_required' ? 'restart' : 'start';
		action.textContent = apply.state === 'restart_required' ? 'Перезапустить сейчас' : 'Запустить сейчас';
	}

	function collect() {
		const result = JSON.parse(JSON.stringify(config.config || {}));
		$$('[data-key]', $('#config-form')).forEach(input => {
			const key = input.dataset.key, type = input.dataset.type;
			if (['integer', 'number'].includes(type) && input.value.trim() === '') { delete result[key]; return; }
			if (type === 'boolean') result[key] = input.checked;
			else if (type === 'select') result[key] = JSON.parse(input.value);
			else if (type === 'integer') result[key] = Number.parseInt(input.value, 10);
			else if (type === 'number') result[key] = Number(input.value);
			else result[key] = input.value || (input.dataset.nullable === '1' ? null : '');
		});
		$('#config-json').value = JSON.stringify(result, null, 2);
		return result;
	}

	function renderDiff(changes) {
		const box = $('#config-diff');
		box.classList.toggle('hidden', !changes.length);
		box.innerHTML = changes.map(change => `<div><strong>${esc(change.label)}</strong><code><del>${esc(JSON.stringify(change.before))}</del> → <ins>${esc(JSON.stringify(change.after))}</ins></code>${change.restart_required ? '<span class="badge warn">перезапуск</span>' : ""}</div>`).join('');
		if (!changes.length) toast('Изменений нет');
	}

	function message(text, level = '') {
		const box = $('#config-message');
		box.classList.toggle('hidden', !text);
		box.className = `notice ${level}${text ? "" : " hidden"}`;
		box.textContent = text;
	}

	async function loadConfig() {
		const data = await api('/api/config');
		config = {config: data.config || {}, revision: data.revision || '', schema: data.schema || null, backups: Array.isArray(data.backups) ? data.backups : [], apply: data.apply_state || null};
		renderApply();
		renderForm();
		$('#config-diff').classList.add('hidden');
		$('#config-backup').innerHTML = config.backups.length ? '<option value="">Выберите копию</option>' + config.backups.map(item => `<option value="${esc(item.name)}">${esc(fmtDateTime(item.modified))} · ${esc(item.name)}</option>`).join('') : '<option value="">Нет резервных копий</option>';
		$('#config-rollback').disabled = true;
		message(data.validation_warning ? `Внимание: ${data.validation_warning}` : '', 'warn');
	}

	async function preview() {
		const next = collect();
		const data = await api('/api/config/preview', {method: 'POST', body: {config: next, revision: config.revision}});
		renderDiff(data.changes || []);
		return {next, changes: data.changes || []};
	}

	$('#config-reload').addEventListener('click', event => busy(event.currentTarget, loadConfig).catch(fail));
	$('#config-preview').addEventListener('click', event => busy(event.currentTarget, preview).catch(fail));
	$('#config-save').addEventListener('click', async event => {
		try {
			const {next, changes} = await busy(event.currentTarget, preview);
			if (!changes.length) return;
			if (!await VS.confirmAction(`Сохранить изменения: ${changes.length}?`, 'Перед записью панель сохранит копию serverconfig.json. Работающий сервер будет перезапущен, чтобы применить настройки.', 'Сохранить')) return;
			const data = await busy(event.currentTarget, () => api('/api/config', {method: 'PUT', body: {config: next, revision: config.revision}}));
			await loadConfig();
			const applied = data.apply_state?.state;
			if (data.auto_restarted && applied !== 'applied') toast('Сохранено, но сервер не запустился — проверьте журнал', 'error');
			else toast(data.auto_restarted ? 'Сохранено — сервер перезапущен' : (applied === 'restart_required' ? 'Сохранено — нужен перезапуск' : (applied === 'start_required' ? 'Сохранено — применится при запуске' : 'Настройки применены')), 'ok');
		} catch (error) { fail(error); }
	});
	$('#config-json-apply').addEventListener('click', () => {
		try {
			const value = JSON.parse($('#config-json').value);
			if (!value || Array.isArray(value) || typeof value !== 'object') throw new Error('нужен JSON-объект');
			config.config = value;
			renderForm();
			toast('JSON применён к форме — проверьте изменения и сохраните');
		} catch (error) { toast(`Ошибка JSON: ${error.message}`, 'error'); }
	});
	$('#config-backup').addEventListener('change', event => { $('#config-rollback').disabled = !event.target.value; });
	$('#config-rollback').addEventListener('click', async event => {
		const name = $('#config-backup').value;
		if (!name || !await VS.confirmAction('Откатить serverconfig.json?', `Файл будет заменён копией ${name}; текущий вариант сохранится отдельной копией.`, 'Откатить', true)) return;
		try {
			const data = await busy(event.currentTarget, () => api('/api/config/rollback', {method: 'POST', body: {name}}));
			await loadConfig();
			const failed = data.auto_restarted && data.apply_state?.state !== 'applied';
			toast(failed ? 'Конфигурация восстановлена, но сервер не запустился — проверьте журнал' : `Восстановлено из ${data.restored}`, failed ? 'error' : 'ok');
		} catch (error) { fail(error); }
	});
	$('#config-apply-action').addEventListener('click', async event => {
		const action = event.currentTarget.dataset.service;
		try { await busy(event.currentTarget, () => api('/api/service', {method: 'POST', body: {action}})); toast(action === 'restart' ? 'Сервер перезапущен' : 'Сервер запущен', 'ok'); setTimeout(() => loadConfig().catch(() => {}), 1500); }
		catch (error) { fail(error); }
	});

	// ---------- мир и моды ----------

	async function loadWorld() {
		const [template, runtime, mods] = await Promise.all([api('/api/world-config'), api('/api/world-config/runtime'), api('/api/mod-configs')]);
		worldRevision = template.revision;
		$('#world-template').value = JSON.stringify(template.config || {}, null, 2);
		const entries = Object.entries(runtime.configuration || {}).sort(([a], [b]) => a.localeCompare(b));
		$('#runtime-world').innerHTML = entries.map(([name, value]) => `<div class="clickable" data-world-key="${esc(name)}"><span class="grow"><b class="mono">${esc(name)}</b><small class="mono">${esc(typeof value === "string" ? value : JSON.stringify(value))}</small></span></div>`).join('') || `<div class="empty">${esc(runtime.detail || "Телеметрия ещё не передала настройки мира.")}</div>`;
		const files = mods.files || [];
		const selected = $('#mod-config-select').value;
		$('#mod-config-select').innerHTML = files.map(item => `<option value="${esc(item.name)}">${esc(item.name)}</option>`).join('') || '<option value="">JSON-файлы не найдены</option>';
		if (files.some(item => item.name === selected)) $('#mod-config-select').value = selected;
		if (files.length) await loadModConfig(); else $('#mod-config-editor').value = '';
	}

	async function loadModConfig() {
		const name = $('#mod-config-select').value;
		if (!name) return;
		const data = await api(`/api/mod-configs/${encodeURIComponent(name)}`);
		modRevision = data.modified_ns;
		$('#mod-config-editor').value = JSON.stringify(data.config, null, 2);
	}

	async function saveJson(kind, button) {
		const editor = kind === 'world' ? $('#world-template') : $('#mod-config-editor');
		let value;
		try { value = JSON.parse(editor.value); } catch (error) { toast(`Ошибка JSON: ${error.message}`, 'error'); return; }
		const url = kind === 'world' ? '/api/world-config' : `/api/mod-configs/${encodeURIComponent($("#mod-config-select").value)}`;
		await busy(button, () => api(url, {method: 'PUT', body: {config: value, revision: kind === 'world' ? worldRevision : modRevision}}));
		toast(kind === 'world' ? 'Шаблон сохранён — подействует при создании мира' : 'Конфигурация мода сохранена — нужен перезапуск', 'ok');
		await loadWorld();
	}

	// Нажатие на параметр текущего мира подставляет его имя в форму изменения.
	$('#runtime-world').addEventListener('click', event => {
		const name = event.target.closest('[data-world-key]')?.dataset.worldKey;
		if (name && /^[A-Za-z][A-Za-z0-9]*$/.test(name)) { $('#worldconfig-name').value = name; $('#worldconfig-value').focus(); }
	});

	$('#world-template-save').addEventListener('click', event => saveJson('world', event.currentTarget).catch(fail));
	$('#mod-config-save').addEventListener('click', event => saveJson('mod', event.currentTarget).catch(fail));
	$('#mod-config-select').addEventListener('change', () => loadModConfig().catch(fail));
	$('#worldconfig-form').addEventListener('submit', async event => {
		event.preventDefault();
		const body = {action: $('#worldconfig-action').value, datatype: $('#worldconfig-datatype').value, name: $('#worldconfig-name').value.trim(), value: $('#worldconfig-value').value};
		try { await busy(event.submitter, () => api('/api/world-config/runtime', {method: 'POST', body})); toast('Команда отправлена серверу', 'ok'); setTimeout(() => loadWorld().catch(() => {}), 3000); }
		catch (error) { fail(error); }
	});

	// ---------- доступ ----------

	async function loadAccounts() {
		$('#totp-user').textContent = state.user;
		if (!VS.can('admin')) { $('#account-list').innerHTML = `<div><span class="avatar">${esc(state.user.slice(0, 2))}</span><span class="grow"><b>${esc(state.user)}</b><small>${esc(VS.roleTitles[state.role])} · список пользователей видит только администратор</small></span></div>`; return; }
		const data = await api('/api/accounts');
		$('#account-list').innerHTML = (data.users || []).map(user => `<div><span class="avatar">${esc(user.username.slice(0, 2))}</span><span class="grow"><b>${esc(user.username)}${user.username === state.user ? " · вы" : ""}</b><small>${esc(VS.roleTitles[user.role] || user.role)} · TOTP ${user.totp_enabled ? "включён" : "выключен"}</small></span><span class="row-actions"><button class="button small" type="button" data-account-edit="${esc(user.username)}" data-role="${esc(user.role)}">Изменить</button>${user.username === "admin" ? "" : `<button class="button small danger" type="button" data-account-delete="${esc(user.username)}">Удалить</button>`}</span></div>`).join('');
	}

	$('#account-list').addEventListener('click', async event => {
		const edit = event.target.closest('[data-account-edit]'), remove = event.target.closest('[data-account-delete]');
		if (edit) {
			$('#account-name').value = edit.dataset.accountEdit;
			$('#account-role').value = edit.dataset.role;
			$('#account-password').focus();
			return;
		}
		if (!remove) return;
		const name = remove.dataset.accountDelete;
		if (!await VS.confirmAction(`Удалить пользователя ${name}?`, 'Его сессии перестанут действовать сразу.', 'Удалить', true)) return;
		try { await busy(remove, () => api(`/api/accounts/${encodeURIComponent(name)}`, {method: 'DELETE'})); toast('Пользователь удалён', 'ok'); await loadAccounts(); }
		catch (error) { fail(error); }
	});

	$('#account-form').addEventListener('submit', async event => {
		event.preventDefault();
		try {
			await busy(event.submitter, () => api('/api/accounts', {method: 'PUT', body: {username: $('#account-name').value.trim(), password: $('#account-password').value, role: $('#account-role').value}}));
			$('#account-password').value = '';
			toast('Учётная запись сохранена; прежние сессии пользователя отозваны', 'ok');
			await loadAccounts();
		} catch (error) { fail(error); }
	});

	$('#totp-begin').addEventListener('click', async () => {
		const result = await dialog({eyebrow: 'ВТОРОЙ ФАКТОР', title: 'Настроить TOTP', text: 'Для настройки второго фактора подтвердите текущий пароль.', fields: [{label: 'Текущий пароль', type: 'password'}], confirm: 'Продолжить'});
		if (!result) return;
		try {
			const data = await api('/api/accounts/totp/begin', {method: 'POST', body: {password: result.values[0]}});
			const box = $('#totp-setup');
			box.className = 'notice';
			box.innerHTML = `<strong>Добавьте ключ в приложение-аутентификатор</strong><p>Создайте запись TOTP и введите секретный ключ вручную, затем подтвердите кодом.</p>
				<div class="secret-block"><span class="muted">Секретный ключ</span><code>${esc(data.secret)}</code></div>
				<details class="work-details"><summary>URI для импорта</summary><div class="secret-block"><code>${esc(data.uri)}</code></div></details>
				<form class="inline-form" id="totp-confirm-form"><input id="totp-code" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" autocomplete="one-time-code" placeholder="Код из приложения" required><button class="button primary" type="submit">Подтвердить</button></form>`;
			$('#totp-confirm-form').addEventListener('submit', async submit => {
				submit.preventDefault();
				try { await busy(submit.submitter, () => api('/api/accounts/totp/confirm', {method: 'POST', body: {code: $('#totp-code').value.trim()}})); toast('TOTP включён', 'ok'); box.className = 'hidden'; await loadAccounts(); }
				catch (error) { fail(error); }
			});
		} catch (error) { fail(error); }
	});

	$('#totp-disable').addEventListener('click', async () => {
		const result = await dialog({eyebrow: 'ВТОРОЙ ФАКТОР', title: 'Отключить TOTP?', text: 'Вход снова будет только по паролю. Подтвердите текущим паролем.', fields: [{label: 'Текущий пароль', type: 'password'}], confirm: 'Отключить', danger: true});
		if (!result) return;
		try { await api('/api/accounts/totp', {method: 'DELETE', body: {password: result.values[0]}}); toast('TOTP отключён', 'ok'); await loadAccounts(); }
		catch (error) { fail(error); }
	});

	// ---------- уведомления ----------

	const SECRETS = [['webhook_url', '#notifications-webhook'], ['discord_url', '#notifications-discord'], ['telegram_token', '#notifications-telegram-token'], ['telegram_chat_id', '#notifications-telegram-chat']];

	async function loadNotifications() {
		const data = await api('/api/events?limit=1');
		const settings = data.notifications || {};
		$('#notifications-enabled').checked = !!settings.enabled;
		$('#notifications-level').value = settings.minimum_severity || 'warning';
		$('#notifications-cooldown').value = settings.cooldown_seconds || 300;
		SECRETS.forEach(([key, id]) => { const input = $(id); input.value = ''; input.placeholder = settings[`${key}_configured`] ? 'Сохранено · оставьте пустым, чтобы не менять' : (input.dataset.placeholder ||= input.placeholder); });
	}

	$('#notifications-form').addEventListener('submit', async event => {
		event.preventDefault();
		const body = {enabled: $('#notifications-enabled').checked, minimum_severity: $('#notifications-level').value, cooldown_seconds: Number($('#notifications-cooldown').value)};
		SECRETS.forEach(([key, id]) => { if ($(id).value) body[key] = $(id).value; });
		try { await busy(event.submitter, () => api('/api/notifications', {method: 'PUT', body})); toast('Уведомления сохранены', 'ok'); await loadNotifications(); }
		catch (error) { fail(error); }
	});
	$('#notifications-test').addEventListener('click', async event => {
		try { await busy(event.currentTarget, () => api('/api/notifications/test', {method: 'POST'})); toast('Тестовое уведомление отправлено', 'ok'); }
		catch (error) { fail(error); }
	});
	$('#notifications-clear').addEventListener('click', async event => {
		if (!await VS.confirmAction('Удалить все каналы?', 'Будут стёрты webhook-адреса и реквизиты Telegram; отправка уведомлений выключится.', 'Удалить', true)) return;
		try {
			await busy(event.currentTarget, () => api('/api/notifications', {method: 'PUT', body: {enabled: false, minimum_severity: $('#notifications-level').value, cooldown_seconds: Number($('#notifications-cooldown').value), webhook_url: '', discord_url: '', telegram_token: '', telegram_chat_id: ''}}));
			toast('Каналы и секреты удалены', 'ok');
			await loadNotifications();
		} catch (error) { fail(error); }
	});

	// ---------- каталог серверов ----------

	/** Текущий каталог серверов и место на его диске; подсказка — в форме пути этой платформы. */
	function renderServersRoot(data) {
		const rows = [['Сейчас', data.instances_root || '—'], ['Свободно на диске', data.total ? `${fmtBytes(data.free)} из ${fmtBytes(data.total)}` : '—']];
		$('#servers-root-state').innerHTML = rows.map(([name, value]) => `<div><span class="grow"><b>${esc(name)}</b><small class="mono">${esc(value)}</small></span></div>`).join('');
		const windows = /^[A-Za-z]:\\/.test(data.instances_root || '');
		$('#servers-root-path').placeholder = windows ? 'D:\\vs-servers' : '/srv/vs-panel';
	}

	async function loadPanel() {
		if (!VS.can('admin')) return;
		renderServersRoot(await api('/api/settings/servers-root'));
	}

	$('#servers-root-form').addEventListener('submit', async event => {
		event.preventDefault();
		const path = $('#servers-root-path').value.trim();
		if (!await VS.confirmAction('Сменить каталог серверов?', `Новые серверы будут создаваться в ${path}. Уже созданные серверы останутся на своих местах и продолжат работать.`, 'Сменить')) return;
		try {
			const data = await busy(event.submitter, () => api('/api/settings/servers-root', {method: 'PUT', body: {path}}));
			renderServersRoot(data);
			$('#servers-root-path').value = '';
			toast(`Каталог серверов: ${data.instances_root}`, 'ok');
		} catch (error) { fail(error); }
	});

	// ---------- раздел ----------

	VS.pages.settings = {
		enter(tab) { this.tab(tab); },
		tab(view) {
			const loaders = {server: loadConfig, world: loadWorld, access: loadAccounts, notifications: loadNotifications, panel: loadPanel};
			(loaders[view] || loadConfig)().catch(fail);
		},
	};
})();
