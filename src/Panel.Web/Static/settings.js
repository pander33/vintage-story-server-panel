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
		else if (schema.key === 'StartupCommands') control = `<textarea ${common} rows="4" placeholder="По одной команде в строке">${esc(value ?? "")}</textarea>`;
		else if (schema.key === 'Password') control = `<div class="password-field"><input type="password" ${common} value="${esc(value ?? "")}" placeholder="Пусто — без пароля" ${schema.maximum_length ? `maxlength="${esc(schema.maximum_length)}"` : ""}><button class="button small" type="button" data-toggle-password>Показать</button></div>`;
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
			// Пустое поле: прежняя пустая строка остаётся строкой, иначе каждое сохранение меняло бы
			// "" на null в тех ключах, которых никто не трогал.
			else result[key] = input.value || (input.dataset.nullable === '1' && config.config?.[key] !== '' ? null : '');
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

	$('#config-form').addEventListener('click', event => {
		const button = event.target.closest('[data-toggle-password]');
		if (!button) return;
		const input = button.previousElementSibling;
		input.type = input.type === 'password' ? 'text' : 'password';
		button.textContent = input.type === 'password' ? 'Показать' : 'Скрыть';
	});
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

	// Шаблон нового мира: форма по схеме игры и модов плюс вкладка JSON. Значения в шаблоне — строки,
	// как их пишет сама игра; ключи вне схемы (моды, у которых схему прочитать не удалось) сохраняются
	// как есть: форма меняет только свои поля.
	let worldSchema = null;
	let worldTemplateDoc = {};
	let worldMode = 'form';

	function worldControl(attribute, value) {
		const common = `data-wa-key="${esc(attribute.code)}"`;
		const hint = `${attribute.description || ''}${attribute.only_during_world_create ? `${attribute.description ? ' ' : ''}Задаётся только при создании мира.` : ''}`;
		if (attribute.data_type === 'bool') {
			return `<label class="config-field checkbox"><input type="checkbox" ${common} ${String(value) === 'true' ? 'checked' : ''}><div><span>${esc(attribute.label)}</span><small>${esc(hint)}</small></div></label>`;
		}
		let control;
		if (attribute.data_type === 'dropdown') {
			const known = (attribute.options || []).some(option => option.value === String(value));
			control = `<select ${common}>${known ? '' : `<option value="${esc(value)}" selected>${esc(value)} (вне списка)</option>`}${(attribute.options || []).map(option => `<option value="${esc(option.value)}" ${option.value === String(value) ? 'selected' : ''}>${esc(option.label)}</option>`).join('')}</select>`;
		} else {
			const numeric = attribute.data_type === 'integer' || attribute.data_type === 'number';
			control = `<input type="${numeric ? 'number' : 'text'}" ${common} value="${esc(value ?? '')}" ${attribute.data_type === 'number' ? 'step="any"' : (numeric ? 'step="1"' : '')}>`;
		}
		return `<label class="config-field"><span>${esc(attribute.label)}</span>${control}<small>${esc(hint)}</small></label>`;
	}

	function renderWorldForm() {
		const form = $('#world-template-form');
		if (!worldSchema?.attributes?.length) {
			form.innerHTML = '<div class="notice">Демон не передал схему параметров мира — изменяйте шаблон во вкладке JSON.</div>';
			return;
		}
		const groups = new Map((worldSchema.categories || []).map(category => [category.id, {...category, fields: []}]));
		worldSchema.attributes.forEach(attribute => {
			if (!groups.has(attribute.category)) groups.set(attribute.category, {id: attribute.category, label: attribute.category, fields: []});
			groups.get(attribute.category).fields.push(attribute);
		});
		const warnings = (worldSchema.warnings || []).length ? `<div class="notice warn">${worldSchema.warnings.map(esc).join('<br>')}</div>` : '';
		form.innerHTML = warnings + [...groups.values()].filter(group => group.fields.length).map(group => `<fieldset class="config-group"><legend>${esc(group.label)}</legend><div class="config-grid">${group.fields.map(attribute => worldControl(attribute, worldTemplateDoc[attribute.code] ?? attribute.default ?? '')).join('')}</div></fieldset>`).join('');
		if (!VS.can('admin')) $$('#world-template-form input, #world-template-form select').forEach(control => { control.disabled = true; });
	}

	/** Значения формы поверх текущего документа: ключи вне формы остаются, пустое число — не пишется. */
	function collectWorldForm() {
		const result = {...worldTemplateDoc};
		$$('[data-wa-key]', $('#world-template-form')).forEach(input => {
			const key = input.dataset.waKey;
			if (input.type === 'checkbox') result[key] = input.checked ? 'true' : 'false';
			else if (input.type === 'number' && input.value.trim() === '') delete result[key];
			else result[key] = input.value;
		});
		return result;
	}

	/** Текущее содержимое шаблона из активной вкладки; null — JSON не разобрался. */
	function currentWorldTemplate() {
		if (worldMode === 'form') return collectWorldForm();
		try {
			const value = JSON.parse($('#world-template').value);
			if (!value || Array.isArray(value) || typeof value !== 'object') throw new Error('нужен JSON-объект');
			return value;
		} catch (error) { toast(`Ошибка JSON: ${error.message}`, 'error'); return null; }
	}

	function setWorldMode(mode) {
		const value = currentWorldTemplate();
		if (!value) return;
		worldTemplateDoc = value;
		worldMode = mode;
		$$('#world-template-mode [data-mode]').forEach(button => button.classList.toggle('active', button.dataset.mode === mode));
		$('#world-template-form').classList.toggle('hidden', mode !== 'form');
		$('#world-template').classList.toggle('hidden', mode !== 'json');
		if (mode === 'form') renderWorldForm();
		else $('#world-template').value = JSON.stringify(worldTemplateDoc, null, 2);
	}

	$('#world-template-mode').addEventListener('click', event => {
		const button = event.target.closest('[data-mode]');
		if (button && button.dataset.mode !== worldMode) setWorldMode(button.dataset.mode);
	});

	// Стиль заполняет значения всех своих параметров; остальные поля не трогает.
	$('#world-template-style').addEventListener('change', event => {
		const style = (worldSchema?.playstyles || []).find(item => item.code === event.target.value);
		if (!style) return;
		const value = currentWorldTemplate();
		if (!value) return;
		worldTemplateDoc = {...value, ...style.values};
		if (worldMode === 'form') renderWorldForm(); else $('#world-template').value = JSON.stringify(worldTemplateDoc, null, 2);
		toast(`Значения стиля «${style.label}» подставлены — сохраните шаблон`);
		event.target.value = '';
	});

	function renderRuntimeWorld(runtime) {
		const byCode = new Map((worldSchema?.attributes || []).map(attribute => [attribute.code, attribute]));
		const entries = Object.entries(runtime.configuration || {}).sort(([a], [b]) => a.localeCompare(b));
		$('#runtime-world').innerHTML = entries.map(([name, value]) => {
			const attribute = byCode.get(name);
			const text = typeof value === 'string' ? value : JSON.stringify(value);
			const shown = attribute?.options?.find(option => option.value === String(value))?.label || text;
			const locked = attribute?.only_during_world_create;
			return `<div class="${locked ? '' : 'clickable'}" data-world-key="${esc(name)}" ${locked ? 'data-locked="1"' : ''}><span class="grow"><b>${esc(attribute?.label || name)}</b><small class="mono">${esc(name)} = ${esc(shown)}</small>${locked ? '<small class="muted">Задаётся при создании мира — в работающем мире не меняется</small>' : ''}</span></div>`;
		}).join('') || `<div class="empty">${esc(runtime.detail || "Телеметрия ещё не передала настройки мира.")}</div>`;
	}

	async function loadWorld() {
		const [template, runtime, mods, schema] = await Promise.all([api('/api/world-config'), api('/api/world-config/runtime'), api('/api/mod-configs'), VS.loadWorldSchema().catch(() => null)]);
		worldRevision = template.revision;
		worldSchema = schema;
		worldTemplateDoc = template.config || {};
		$('#world-template-style').innerHTML = '<option value="">— не менять —</option>' + (worldSchema?.playstyles || []).map(style => `<option value="${esc(style.code)}" title="${esc(style.description)}">${esc(style.label)}</option>`).join('');
		$('#world-template-style').disabled = !VS.can('admin') || !(worldSchema?.playstyles || []).length;
		// Без схемы форма бесполезна: открывается сразу вкладка JSON.
		worldMode = worldSchema?.attributes?.length ? 'form' : 'json';
		$$('#world-template-mode [data-mode]').forEach(button => button.classList.toggle('active', button.dataset.mode === worldMode));
		$('#world-template-form').classList.toggle('hidden', worldMode !== 'form');
		$('#world-template').classList.toggle('hidden', worldMode !== 'json');
		$('#world-template').value = JSON.stringify(worldTemplateDoc, null, 2);
		if (worldMode === 'form') renderWorldForm();
		renderRuntimeWorld(runtime);
		const files = mods.files || [];
		const selected = $('#mod-config-select').value;
		$('#mod-config-select').innerHTML = files.map(item => `<option value="${esc(item.name)}">${esc(item.name)}</option>`).join('') || '<option value="">JSON-файлы не найдены</option>';
		if (files.some(item => item.name === selected)) $('#mod-config-select').value = selected;
		if (files.length) await loadModConfig();
		else { modConfigDoc = null; $('#mod-config-editor').value = ''; $('#mod-config-form').innerHTML = '<div class="empty">JSON-файлы не найдены.</div>'; }
	}

	// Конфигурация мода: общей схемы нет, форма строится по самому JSON. Форма меняет только значения —
	// ключей не добавляет и не удаляет; массив объектов и прочее, что формой не выразить, правится во
	// вкладке JSON.
	let modConfigDoc = null;
	let modMode = 'form';

	/** Список, который форма выразит строками без потери типа: только строки или только числа. */
	const isPlainArray = value => Array.isArray(value) && (value.every(item => typeof item === 'string') || (value.length > 0 && value.every(item => typeof item === 'number')));

	function modField(path, key, value) {
		const where = esc(JSON.stringify(path));
		const title = esc(key);
		if (typeof value === 'boolean') return `<label class="config-field checkbox"><input type="checkbox" data-mc-path="${where}" data-mc-type="boolean" ${value ? 'checked' : ''}><div><span>${title}</span></div></label>`;
		if (typeof value === 'number') return `<label class="config-field"><span>${title}</span><input type="number" data-mc-path="${where}" data-mc-type="${Number.isInteger(value) ? 'integer' : 'number'}" step="${Number.isInteger(value) ? '1' : 'any'}" value="${esc(value)}"></label>`;
		if (typeof value === 'string') return `<label class="config-field"><span>${title}</span><input type="text" data-mc-path="${where}" data-mc-type="string" value="${esc(value)}"></label>`;
		if (value === null) return `<label class="config-field"><span>${title}</span><input type="text" value="null" disabled><small>Пустое значение — меняется только во вкладке JSON.</small></label>`;
		if (isPlainArray(value)) {
			const kind = value.length && typeof value[0] === 'number' ? 'numbers' : 'strings';
			return `<label class="config-field"><span>${title}</span><textarea rows="${Math.min(8, Math.max(2, value.length))}" data-mc-path="${where}" data-mc-type="${kind}">${esc(value.join('\n'))}</textarea><small>По одному значению в строке.</small></label>`;
		}
		if (Array.isArray(value)) return `<label class="config-field"><span>${title}</span><input type="text" value="Список из ${value.length} элементов" disabled><small>Меняется только во вкладке JSON.</small></label>`;
		return `<details class="config-nested" open><summary>${title}</summary><div class="config-grid">${Object.entries(value).map(([inner, item]) => modField([...path, inner], inner, item)).join('')}</div></details>`;
	}

	function renderModForm() {
		const form = $('#mod-config-form');
		if (!modConfigDoc || typeof modConfigDoc !== 'object' || Array.isArray(modConfigDoc)) {
			form.innerHTML = '<div class="notice">Этот файл не объект JSON — изменяйте его во вкладке JSON.</div>';
			return;
		}
		const entries = Object.entries(modConfigDoc);
		form.innerHTML = entries.length ? `<div class="config-grid">${entries.map(([key, value]) => modField([key], key, value)).join('')}</div>` : '<div class="empty">Файл пуст.</div>';
		if (!VS.can('admin')) $$('#mod-config-form input, #mod-config-form textarea').forEach(control => { control.disabled = true; });
	}

	/** Значения формы поверх документа. Число, которое не разобралось, — отказ, а не молчаливый ноль. */
	function collectModForm() {
		const result = structuredClone(modConfigDoc);
		for (const input of $$('[data-mc-path]', $('#mod-config-form'))) {
			const path = JSON.parse(input.dataset.mcPath);
			const type = input.dataset.mcType;
			let value;
			if (type === 'boolean') value = input.checked;
			else if (type === 'integer' || type === 'number') {
				value = Number(input.value);
				if (input.value.trim() === '' || !Number.isFinite(value)) throw new Error(`${path.join('.')}: нужно число`);
				if (type === 'integer' && !Number.isInteger(value)) throw new Error(`${path.join('.')}: нужно целое число`);
			} else if (type === 'numbers') {
				value = input.value.split('\n').map(line => line.trim()).filter(Boolean).map(Number);
				if (value.some(item => !Number.isFinite(item))) throw new Error(`${path.join('.')}: в списке нужны числа`);
			} else if (type === 'strings') value = input.value.split('\n').filter(line => line.trim() !== '');
			else value = input.value;
			let target = result;
			path.slice(0, -1).forEach(key => { target = target[key]; });
			target[path[path.length - 1]] = value;
		}
		return result;
	}

	function currentModConfig() {
		try {
			if (modMode === 'form') return collectModForm();
			return JSON.parse($('#mod-config-editor').value);
		} catch (error) { toast(`Ошибка: ${error.message}`, 'error'); return undefined; }
	}

	function setModMode(mode) {
		const value = currentModConfig();
		if (value === undefined) return;
		modConfigDoc = value;
		modMode = mode;
		$$('#mod-config-mode [data-mode]').forEach(button => button.classList.toggle('active', button.dataset.mode === mode));
		$('#mod-config-form').classList.toggle('hidden', mode !== 'form');
		$('#mod-config-editor').classList.toggle('hidden', mode !== 'json');
		if (mode === 'form') renderModForm();
		else $('#mod-config-editor').value = JSON.stringify(modConfigDoc, null, 2);
	}

	$('#mod-config-mode').addEventListener('click', event => {
		const button = event.target.closest('[data-mode]');
		if (button && button.dataset.mode !== modMode) setModMode(button.dataset.mode);
	});

	async function loadModConfig() {
		const name = $('#mod-config-select').value;
		if (!name) { modConfigDoc = null; $('#mod-config-form').innerHTML = ''; return; }
		const data = await api(`/api/mod-configs/${encodeURIComponent(name)}`);
		modRevision = data.modified_ns;
		modConfigDoc = data.config;
		$('#mod-config-editor').value = JSON.stringify(data.config, null, 2);
		if (modMode === 'form') renderModForm();
	}

	async function saveJson(kind, button) {
		let value;
		if (kind === 'world') {
			value = currentWorldTemplate();
			if (!value) return;
		} else {
			value = currentModConfig();
			if (value === undefined) return;
		}
		const url = kind === 'world' ? '/api/world-config' : `/api/mod-configs/${encodeURIComponent($("#mod-config-select").value)}`;
		await busy(button, () => api(url, {method: 'PUT', body: {config: value, revision: kind === 'world' ? worldRevision : modRevision}}));
		toast(kind === 'world' ? 'Шаблон сохранён — подействует при создании мира' : 'Конфигурация мода сохранена — нужен перезапуск', 'ok');
		await loadWorld();
	}

	// Нажатие на параметр текущего мира подставляет его имя в форму изменения, а тип команды —
	// из схемы: выбирать его руками нужно только для параметров вне схемы.
	const COMMAND_TYPES = {bool: 'bool', integer: 'int', number: 'double'};
	$('#runtime-world').addEventListener('click', event => {
		const row = event.target.closest('[data-world-key]');
		if (!row || row.dataset.locked) return;
		const name = row.dataset.worldKey;
		if (!/^[A-Za-z][A-Za-z0-9]*$/.test(name)) return;
		const attribute = (worldSchema?.attributes || []).find(item => item.code === name);
		$('#worldconfig-name').value = name;
		$('#worldconfig-action').value = 'set';
		if (attribute) $('#worldconfig-datatype').value = COMMAND_TYPES[attribute.data_type] || 'string';
		$('#worldconfig-value').focus();
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
