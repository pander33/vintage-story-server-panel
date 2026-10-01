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
		const common = `data-key="${esc(schema.key)}" data-type="${esc(schema.type)}" data-nullable="${schema.nullable ? '1' : '0'}"`;
		const apply = config.apply;
		const tracked = (apply?.changed_keys || []).includes(schema.key);
		const pending = tracked && ['restart_required', 'start_required'].includes(apply?.state);
		const mark = tracked && apply?.state === 'applied' ? I18N.t('settingsDynamic.s001') : (pending ? `<b class="setting-state pending">${apply.state === 'restart_required' ? I18N.t('settingsDynamic.s002') : I18N.t('settingsDynamic.s003')}</b>` : '');
		const restart = schema.restart_required ? I18N.t('settingsDynamic.s004') : '';
		if (schema.type === 'boolean') {
			return `<label class="config-field checkbox${pending ? ' pending' : ''}"><input type="checkbox" ${common} ${value ? 'checked' : ''}><div><span>${esc(schema.label)} ${restart} ${mark}</span><small>${esc(schema.description || '')}</small></div></label>`;
		}
		let control;
		if (schema.type === 'select') control = `<select ${common}>${(schema.options || []).map(option => `<option value="${esc(JSON.stringify(option.value))}" ${option.value === value ? 'selected' : ''}>${esc(option.label)}</option>`).join('')}</select>`;
		else if (schema.key === 'StartupCommands') control = `<textarea ${common}${I18N.t('settingsDynamic.s005')}${esc(value ?? '')}</textarea>`;
		else if (schema.key === 'Password') control = `<div class="password-field"><input type="password" ${common} value="${esc(value ?? '')}${I18N.t('settingsDynamic.s006')}${schema.maximum_length ? `maxlength="${esc(schema.maximum_length)}"` : ''}${I18N.t('settingsDynamic.s007')}`;
		else {
			const numeric = ['integer', 'number'].includes(schema.type);
			control = `<input type="${numeric ? 'number' : 'text'}" ${common} value="${esc(value ?? '')}" ${schema.min != null ? `min="${esc(schema.min)}"` : ''} ${schema.max != null ? `max="${esc(schema.max)}"` : ''} ${numeric ? `step="${schema.type === 'number' ? 'any' : '1'}"` : ''} ${schema.maximum_length ? `maxlength="${esc(schema.maximum_length)}"` : ''}>`;
		}
		return `<label class="config-field${pending ? ' pending' : ''}"><span>${esc(schema.label)} ${restart} ${mark}</span>${control}<small>${esc(schema.description || '')}</small></label>`;
	}

	function renderForm() {
		const schema = config.schema;
		const form = $('#config-form');
		if (!schema || !Array.isArray(schema.categories) || !Array.isArray(schema.fields)) {
			form.innerHTML = I18N.t('settingsDynamic.s008');
			$('#config-json').value = JSON.stringify(config.config || {}, null, 2);
			return;
		}
		const groups = new Map(schema.categories.map(category => [category.id, {...category, fields: []}]));
		schema.fields.forEach(item => groups.get(item.category)?.fields.push(item));
		form.innerHTML = [...groups.values()].filter(group => group.fields.length).map(group => `<fieldset class="config-group"><legend>${esc(group.label)}</legend><div class="config-grid">${group.fields.map(item => field(item, config.config[item.key])).join('')}</div></fieldset>`).join('');
		$('#config-json').value = JSON.stringify(config.config, null, 2);
		if (!VS.can('admin')) $$('#config-form input, #config-form select').forEach(control => { control.disabled = true; });
	}

	function renderApply() {
		const apply = config.apply;
		const box = $('#config-apply');
		if (!apply) { box.classList.add('hidden'); return; }
		const count = (apply.changed_keys || []).length;
		const [title, detail] = {
			applied: [I18N.t('settingsDynamic.s009'), I18N.t('settingsDynamic.s010')],
			restart_required: [I18N.t('settingsDynamic.s011'), I18N.t('settingsDynamic.s012')],
			start_required: [I18N.t('settingsDynamic.s013'), I18N.t('settingsDynamic.s014')],
		}[apply.state] || [I18N.t('settingsDynamic.s015'), apply.detail || I18N.t('settingsDynamic.s016')];
		box.className = `apply-state ${esc(apply.state || 'unknown')}`;
		$('#config-apply-title').textContent = title;
		$('#config-apply-detail').textContent = `${detail}${count ? `${I18N.t('settingsDynamic.s017')}${count}.` : ''}`;
		const action = $('#config-apply-action');
		const needed = ['restart_required', 'start_required'].includes(apply.state);
		action.classList.toggle('hidden', !needed);
		action.dataset.service = apply.state === 'restart_required' ? 'restart' : 'start';
		action.textContent = apply.state === 'restart_required' ? I18N.t('settingsDynamic.s018') : I18N.t('settingsDynamic.s019');
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
		box.innerHTML = changes.map(change => `<div><strong>${esc(change.label)}</strong><code><del>${esc(JSON.stringify(change.before))}</del> → <ins>${esc(JSON.stringify(change.after))}</ins></code>${change.restart_required ? I18N.t('settingsDynamic.s020') : ''}</div>`).join('');
		if (!changes.length) toast(I18N.t('settingsDynamic.s021'));
	}

	function message(text, level = '') {
		const box = $('#config-message');
		box.classList.toggle('hidden', !text);
		box.className = `notice ${level}${text ? '' : ' hidden'}`;
		box.textContent = text;
	}

	async function loadConfig() {
		const data = await api('/api/config');
		config = {config: data.config || {}, revision: data.revision || '', schema: data.schema || null, backups: Array.isArray(data.backups) ? data.backups : [], apply: data.apply_state || null};
		renderApply();
		renderForm();
		$('#config-diff').classList.add('hidden');
		$('#config-backup').innerHTML = config.backups.length ? I18N.t('settingsDynamic.s022') + config.backups.map(item => `<option value="${esc(item.name)}">${esc(fmtDateTime(item.modified))} · ${esc(item.name)}</option>`).join('') : I18N.t('settingsDynamic.s023');
		$('#config-rollback').disabled = true;
		message(data.validation_warning ? `${I18N.t('settingsDynamic.s024')}${data.validation_warning}` : '', 'warn');
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
		button.textContent = input.type === 'password' ? I18N.t('settingsDynamic.s025') : I18N.t('settingsDynamic.s026');
	});
	$('#config-reload').addEventListener('click', event => busy(event.currentTarget, loadConfig).catch(fail));
	$('#config-preview').addEventListener('click', event => busy(event.currentTarget, preview).catch(fail));
	$('#config-save').addEventListener('click', async event => {
		try {
			const {next, changes} = await busy(event.currentTarget, preview);
			if (!changes.length) return;
			if (!await VS.confirmAction(`${I18N.t('settingsDynamic.s027')}${changes.length}?`, I18N.t('settingsDynamic.s028'), I18N.t('settingsDynamic.s029'))) return;
			const data = await busy(event.currentTarget, () => api('/api/config', {method: 'PUT', body: {config: next, revision: config.revision}}));
			await loadConfig();
			const applied = data.apply_state?.state;
			if (data.auto_restarted && applied !== 'applied') toast(I18N.t('settingsDynamic.s030'), 'error');
			else toast(data.auto_restarted ? I18N.t('settingsDynamic.s031') : (applied === 'restart_required' ? I18N.t('settingsDynamic.s032') : (applied === 'start_required' ? I18N.t('settingsDynamic.s033') : I18N.t('settingsDynamic.s034'))), 'ok');
			// Настройки записаны, но новый игровой порт в брандмауэре не открылся: игроки не подключатся.
			if (data.firewall_warning) toast(data.firewall_warning, 'error');
		} catch (error) { fail(error); }
	});
	$('#config-json-apply').addEventListener('click', () => {
		try {
			const value = JSON.parse($('#config-json').value);
			if (!value || Array.isArray(value) || typeof value !== 'object') throw new Error(I18N.t('settingsDynamic.s035'));
			config.config = value;
			renderForm();
			toast(I18N.t('settingsDynamic.s036'));
		} catch (error) { toast(`${I18N.t('settingsDynamic.s037')}${error.message}`, 'error'); }
	});
	$('#config-backup').addEventListener('change', event => { $('#config-rollback').disabled = !event.target.value; });
	$('#config-rollback').addEventListener('click', async event => {
		const name = $('#config-backup').value;
		if (!name || !await VS.confirmAction(I18N.t('settingsDynamic.s038'), `${I18N.t('settingsDynamic.s039')}${name}${I18N.t('settingsDynamic.s040')}`, I18N.t('settingsDynamic.s041'), true)) return;
		try {
			const data = await busy(event.currentTarget, () => api('/api/config/rollback', {method: 'POST', body: {name}}));
			await loadConfig();
			const failed = data.auto_restarted && data.apply_state?.state !== 'applied';
			toast(failed ? I18N.t('settingsDynamic.s042') : `${I18N.t('settingsDynamic.s043')}${data.restored}`, failed ? 'error' : 'ok');
			if (data.firewall_warning) toast(data.firewall_warning, 'error');
		} catch (error) { fail(error); }
	});
	$('#config-apply-action').addEventListener('click', async event => {
		const action = event.currentTarget.dataset.service;
		try { await busy(event.currentTarget, () => api('/api/service', {method: 'POST', body: {action}})); toast(action === 'restart' ? I18N.t('settingsDynamic.s044') : I18N.t('settingsDynamic.s045'), 'ok'); setTimeout(() => loadConfig().catch(() => {}), 1500); }
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
		const hint = `${attribute.description || ''}${attribute.only_during_world_create ? `${attribute.description ? ' ' : ''}${I18N.t('settingsDynamic.s046')}` : ''}`;
		if (attribute.data_type === 'bool') {
			return `<label class="config-field checkbox"><input type="checkbox" ${common} ${String(value) === 'true' ? 'checked' : ''}><div><span>${esc(attribute.label)}</span><small>${esc(hint)}</small></div></label>`;
		}
		let control;
		if (attribute.data_type === 'dropdown') {
			const known = (attribute.options || []).some(option => option.value === String(value));
			control = `<select ${common}>${known ? '' : `<option value="${esc(value)}" selected>${esc(value)}${I18N.t('settingsDynamic.s047')}`}${(attribute.options || []).map(option => `<option value="${esc(option.value)}" ${option.value === String(value) ? 'selected' : ''}>${esc(option.label)}</option>`).join('')}</select>`;
		} else {
			const numeric = attribute.data_type === 'integer' || attribute.data_type === 'number';
			control = `<input type="${numeric ? 'number' : 'text'}" ${common} value="${esc(value ?? '')}" ${attribute.data_type === 'number' ? 'step="any"' : (numeric ? 'step="1"' : '')}>`;
		}
		return `<label class="config-field"><span>${esc(attribute.label)}</span>${control}<small>${esc(hint)}</small></label>`;
	}

	function renderWorldForm() {
		const form = $('#world-template-form');
		if (!worldSchema?.attributes?.length) {
			form.innerHTML = I18N.t('settingsDynamic.s048');
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
			if (!value || Array.isArray(value) || typeof value !== 'object') throw new Error(I18N.t('settingsDynamic.s049'));
			return value;
		} catch (error) { toast(`${I18N.t('settingsDynamic.s050')}${error.message}`, 'error'); return null; }
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
		toast(`${I18N.t('settingsDynamic.s051')}${style.label}${I18N.t('settingsDynamic.s052')}`);
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
			return `<div class="${locked ? '' : 'clickable'}" data-world-key="${esc(name)}" ${locked ? 'data-locked="1"' : ''}><span class="grow"><b>${esc(attribute?.label || name)}</b><small class="mono">${esc(name)} = ${esc(shown)}</small>${locked ? I18N.t('settingsDynamic.s053') : ''}</span></div>`;
		}).join('') || `<div class="empty">${esc(runtime.detail || I18N.t('settingsDynamic.s054'))}</div>`;
	}

	async function loadWorld() {
		const [template, runtime, mods, schema] = await Promise.all([api('/api/world-config'), api('/api/world-config/runtime'), api('/api/mod-configs'), VS.loadWorldSchema().catch(() => null)]);
		worldRevision = template.revision;
		worldSchema = schema;
		worldTemplateDoc = template.config || {};
		$('#world-template-style').innerHTML = I18N.t('settingsDynamic.s055') + (worldSchema?.playstyles || []).map(style => `<option value="${esc(style.code)}" title="${esc(style.description)}">${esc(style.label)}</option>`).join('');
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
		$('#mod-config-select').innerHTML = files.map(item => `<option value="${esc(item.name)}">${esc(item.name)}</option>`).join('') || I18N.t('settingsDynamic.s056');
		if (files.some(item => item.name === selected)) $('#mod-config-select').value = selected;
		if (files.length) await loadModConfig();
		else { modConfigDoc = null; $('#mod-config-editor').value = ''; $('#mod-config-form').innerHTML = I18N.t('settingsDynamic.s057'); }
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
		if (value === null) return `<label class="config-field"><span>${title}${I18N.t('settingsDynamic.s058')}`;
		if (isPlainArray(value)) {
			const kind = value.length && typeof value[0] === 'number' ? 'numbers' : 'strings';
			return `<label class="config-field"><span>${title}</span><textarea rows="${Math.min(8, Math.max(2, value.length))}" data-mc-path="${where}" data-mc-type="${kind}">${esc(value.join('\n'))}${I18N.t('settingsDynamic.s059')}`;
		}
		if (Array.isArray(value)) return `<label class="config-field"><span>${title}${I18N.t('settingsDynamic.s060')}${value.length}${I18N.t('settingsDynamic.s061')}`;
		return `<details class="config-nested" open><summary>${title}</summary><div class="config-grid">${Object.entries(value).map(([inner, item]) => modField([...path, inner], inner, item)).join('')}</div></details>`;
	}

	function renderModForm() {
		const form = $('#mod-config-form');
		if (!modConfigDoc || typeof modConfigDoc !== 'object' || Array.isArray(modConfigDoc)) {
			form.innerHTML = I18N.t('settingsDynamic.s062');
			return;
		}
		const entries = Object.entries(modConfigDoc);
		form.innerHTML = entries.length ? `<div class="config-grid">${entries.map(([key, value]) => modField([key], key, value)).join('')}</div>` : I18N.t('settingsDynamic.s063');
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
				if (input.value.trim() === '' || !Number.isFinite(value)) throw new Error(`${path.join('.')}${I18N.t('settingsDynamic.s064')}`);
				if (type === 'integer' && !Number.isInteger(value)) throw new Error(`${path.join('.')}${I18N.t('settingsDynamic.s065')}`);
			} else if (type === 'numbers') {
				value = input.value.split('\n').map(line => line.trim()).filter(Boolean).map(Number);
				if (value.some(item => !Number.isFinite(item))) throw new Error(`${path.join('.')}${I18N.t('settingsDynamic.s066')}`);
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
		} catch (error) { toast(`${I18N.t('settingsDynamic.s067')}${error.message}`, 'error'); return undefined; }
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
		const url = kind === 'world' ? '/api/world-config' : `/api/mod-configs/${encodeURIComponent($('#mod-config-select').value)}`;
		await busy(button, () => api(url, {method: 'PUT', body: {config: value, revision: kind === 'world' ? worldRevision : modRevision}}));
		toast(kind === 'world' ? I18N.t('settingsDynamic.s068') : I18N.t('settingsDynamic.s069'), 'ok');
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
		try { await busy(event.submitter, () => api('/api/world-config/runtime', {method: 'POST', body})); toast(I18N.t('settingsDynamic.s070'), 'ok'); setTimeout(() => loadWorld().catch(() => {}), 3000); }
		catch (error) { fail(error); }
	});

	// ---------- доступ ----------

	async function loadAccounts() {
		$('#totp-user').textContent = state.user;
		if (!VS.can('admin')) { $('#account-list').innerHTML = `<div><span class="avatar">${esc(state.user.slice(0, 2))}</span><span class="grow"><b>${esc(state.user)}</b><small>${esc(VS.roleTitles[state.role])}${I18N.t('settingsDynamic.s071')}`; return; }
		const data = await api('/api/accounts');
		$('#account-list').innerHTML = (data.users || []).map(user => `<div><span class="avatar">${esc(user.username.slice(0, 2))}</span><span class="grow"><b>${esc(user.username)}${user.username === state.user ? I18N.t('settingsDynamic.s072') : ''}</b><small>${esc(VS.roleTitles[user.role] || user.role)} · TOTP ${user.totp_enabled ? I18N.t('settingsDynamic.s073') : I18N.t('settingsDynamic.s074')}</small></span><span class="row-actions"><button class="button small" type="button" data-account-edit="${esc(user.username)}" data-role="${esc(user.role)}${I18N.t('settingsDynamic.s075')}${user.username === 'admin' ? '' : `<button class="button small danger" type="button" data-account-delete="${esc(user.username)}${I18N.t('settingsDynamic.s076')}`}</span></div>`).join('');
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
		if (!await VS.confirmAction(`${I18N.t('settingsDynamic.s077')}${name}?`, I18N.t('settingsDynamic.s078'), I18N.t('settingsDynamic.s079'), true)) return;
		try { await busy(remove, () => api(`/api/accounts/${encodeURIComponent(name)}`, {method: 'DELETE'})); toast(I18N.t('settingsDynamic.s080'), 'ok'); await loadAccounts(); }
		catch (error) { fail(error); }
	});

	$('#account-form').addEventListener('submit', async event => {
		event.preventDefault();
		try {
			await busy(event.submitter, () => api('/api/accounts', {method: 'PUT', body: {username: $('#account-name').value.trim(), password: $('#account-password').value, role: $('#account-role').value}}));
			$('#account-password').value = '';
			toast(I18N.t('settingsDynamic.s081'), 'ok');
			await loadAccounts();
		} catch (error) { fail(error); }
	});

	$('#totp-begin').addEventListener('click', async () => {
		const result = await dialog({eyebrow: I18N.t('settingsDynamic.s082'), title: I18N.t('settingsDynamic.s083'), text: I18N.t('settingsDynamic.s084'), fields: [{label: I18N.t('settingsDynamic.s085'), type: 'password'}], confirm: I18N.t('settingsDynamic.s086')});
		if (!result) return;
		try {
			const data = await api('/api/accounts/totp/begin', {method: 'POST', body: {password: result.values[0]}});
			const box = $('#totp-setup');
			box.className = 'notice';
			box.innerHTML = `${I18N.t('settingsDynamic.s087')}${esc(data.secret)}${I18N.t('settingsDynamic.s088')}${esc(data.uri)}${I18N.t('settingsDynamic.s089')}`;
			$('#totp-confirm-form').addEventListener('submit', async submit => {
				submit.preventDefault();
				try { await busy(submit.submitter, () => api('/api/accounts/totp/confirm', {method: 'POST', body: {code: $('#totp-code').value.trim()}})); toast(I18N.t('settingsDynamic.s090'), 'ok'); box.className = 'hidden'; await loadAccounts(); }
				catch (error) { fail(error); }
			});
		} catch (error) { fail(error); }
	});

	$('#totp-disable').addEventListener('click', async () => {
		const result = await dialog({eyebrow: I18N.t('settingsDynamic.s091'), title: I18N.t('settingsDynamic.s092'), text: I18N.t('settingsDynamic.s093'), fields: [{label: I18N.t('settingsDynamic.s094'), type: 'password'}], confirm: I18N.t('settingsDynamic.s095'), danger: true});
		if (!result) return;
		try { await api('/api/accounts/totp', {method: 'DELETE', body: {password: result.values[0]}}); toast(I18N.t('settingsDynamic.s096'), 'ok'); await loadAccounts(); }
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
		SECRETS.forEach(([key, id]) => { const input = $(id); input.value = ''; input.placeholder = settings[`${key}_configured`] ? I18N.t('settingsDynamic.s097') : (input.dataset.placeholder ||= input.placeholder); });
	}

	$('#notifications-form').addEventListener('submit', async event => {
		event.preventDefault();
		const body = {enabled: $('#notifications-enabled').checked, minimum_severity: $('#notifications-level').value, cooldown_seconds: Number($('#notifications-cooldown').value)};
		SECRETS.forEach(([key, id]) => { if ($(id).value) body[key] = $(id).value; });
		try { await busy(event.submitter, () => api('/api/notifications', {method: 'PUT', body})); toast(I18N.t('settingsDynamic.s098'), 'ok'); await loadNotifications(); }
		catch (error) { fail(error); }
	});
	$('#notifications-test').addEventListener('click', async event => {
		try { await busy(event.currentTarget, () => api('/api/notifications/test', {method: 'POST'})); toast(I18N.t('settingsDynamic.s099'), 'ok'); }
		catch (error) { fail(error); }
	});
	$('#notifications-clear').addEventListener('click', async event => {
		if (!await VS.confirmAction(I18N.t('settingsDynamic.s100'), I18N.t('settingsDynamic.s101'), I18N.t('settingsDynamic.s102'), true)) return;
		try {
			await busy(event.currentTarget, () => api('/api/notifications', {method: 'PUT', body: {enabled: false, minimum_severity: $('#notifications-level').value, cooldown_seconds: Number($('#notifications-cooldown').value), webhook_url: '', discord_url: '', telegram_token: '', telegram_chat_id: ''}}));
			toast(I18N.t('settingsDynamic.s103'), 'ok');
			await loadNotifications();
		} catch (error) { fail(error); }
	});

	// ---------- каталог серверов ----------

	/** Текущий каталог серверов и место на его диске; подсказка — в форме пути этой платформы. */
	function renderServersRoot(data) {
		const rows = [[I18N.t('settingsDynamic.s104'), data.instances_root || '—'], [I18N.t('settingsDynamic.s105'), data.total ? `${fmtBytes(data.free)}${I18N.t('settingsDynamic.s106')}${fmtBytes(data.total)}` : '—']];
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
		if (!await VS.confirmAction(I18N.t('settingsDynamic.s107'), `${I18N.t('settingsDynamic.s108')}${path}${I18N.t('settingsDynamic.s109')}`, I18N.t('settingsDynamic.s110'))) return;
		try {
			const data = await busy(event.submitter, () => api('/api/settings/servers-root', {method: 'PUT', body: {path}}));
			renderServersRoot(data);
			$('#servers-root-path').value = '';
			toast(`${I18N.t('settingsDynamic.s111')}${data.instances_root}`, 'ok');
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
