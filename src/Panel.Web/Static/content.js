'use strict';

// Миры и моды: файлы миров, установленные моды и их обновления, ModDB, наборы и откат,
// архив и корзина миров.

(() => {
	const {$, $$, esc, fmtBytes, fmtDateTime, api, toast, fail, busy, dialog} = VS;

	const PLAYSTYLES = {surviveandbuild: 'Выживание и строительство', exploration: 'Исследование', wildernesssurvival: 'Суровое выживание', homosapiens: 'Homo Sapiens', creativebuilding: 'Творчество'};
	let serverActive = false;
	let mods = {mods: [], rollbacks: {}, profiles: []};

	// ---------- миры ----------

	async function loadWorlds() {
		const data = await api('/api/worlds');
		serverActive = !!data.server_active;
		const notice = $('#worlds-state');
		notice.className = `notice ${serverActive ? "warn" : "ok"}`;
		notice.textContent = serverActive
			? 'Сервер работает: загрузка и скачивание доступны, а переключение, клонирование, архив и создание мира — после остановки.'
			: 'Сервер остановлен: все операции с мирами доступны.';
		const admin = VS.can('admin');
		const off = serverActive || !admin ? 'disabled' : '';
		$('#worlds-list').innerHTML = (data.worlds || []).map(world => `<div>
			<span class="item-symbol">${world.active ? "◈" : "◇"}</span>
			<span class="grow"><b>${esc(world.name)}</b><small>${esc(fmtBytes(world.size))} · изменён ${esc(fmtDateTime(world.modified))}</small></span>
			${world.active ? '<em class="ok">АКТИВНЫЙ</em>' : ""}
			<span class="row-actions">
				<a class="button small" href="/api/worlds/download/${encodeURIComponent(world.name)}" download>Скачать</a>
				${world.active ? "" : `<button class="button small" type="button" data-world="activate" data-name="${esc(world.name)}" ${off}>Сделать активным</button>`}
				<button class="button small" type="button" data-world="clone" data-name="${esc(world.name)}" ${off}>Клонировать</button>
				${world.active ? "" : `<button class="button small" type="button" data-world="archive" data-name="${esc(world.name)}" ${off}>В архив</button><button class="button small danger" type="button" data-world="trash" data-name="${esc(world.name)}" ${off}>В корзину</button>`}
			</span></div>`).join('') || '<div class="empty">Файлы .vcdbs не найдены.</div>';
		$$('#world-create-form input, #world-create-form select, #world-create-form button').forEach(control => { if (!control.dataset.roleLocked) control.disabled = serverActive; });
	}

	$('#worlds-list').addEventListener('click', async event => {
		const button = event.target.closest('[data-world]');
		if (!button) return;
		const action = button.dataset.world, name = button.dataset.name;
		try {
			if (action === 'clone') {
				const result = await dialog({eyebrow: 'КЛОНИРОВАНИЕ', title: `Клонировать ${name}`, text: 'Копия появится рядом отдельным файлом.', fields: [{label: 'Имя файла копии (.vcdbs)', value: `copy-${name}`}], confirm: 'Клонировать'});
				if (!result) return;
				await busy(button, () => api('/api/worlds/clone', {method: 'POST', body: {source: name, name: result.values[0].trim()}}));
				toast('Мир клонирован', 'ok');
			} else if (action === 'activate') {
				if (!await VS.confirmAction(`Сделать ${name} активным?`, 'Сервер загрузит этот мир при следующем запуске. Перед изменением панель сохранит копию serverconfig.json.', 'Сделать активным')) return;
				await busy(button, () => api('/api/worlds/action', {method: 'POST', body: {name, action}}));
				toast('Активный мир изменён', 'ok');
			} else {
				const archive = action === 'archive';
				const phrase = `${archive ? "АРХИВИРОВАТЬ" : "УДАЛИТЬ"} ${name}`;
				const result = await dialog({eyebrow: archive ? 'АРХИВ' : 'КОРЗИНА', title: archive ? 'Отправить мир в архив?' : 'Убрать мир в корзину?', text: `Файл будет перемещён в Saves/${archive ? ".archive" : ".trash"} и останется восстанавливаемым из вкладки «Архив и корзина».`, phrase, confirm: archive ? 'В архив' : 'В корзину', danger: !archive});
				if (!result) return;
				await busy(button, () => api('/api/worlds/action', {method: 'POST', body: {name, action, confirmation: result.phrase}}));
				toast('Операция выполнена', 'ok');
			}
			await loadWorlds();
		} catch (error) { fail(error); }
	});

	$('#world-upload').addEventListener('change', async event => {
		const file = event.target.files[0];
		if (!file) return;
		const body = new FormData();
		body.append('file', file);
		toast(`Загрузка ${file.name} (${fmtBytes(file.size)})…`);
		try { await api('/api/worlds/upload', {method: 'POST', body}); toast('Мир загружен', 'ok'); await loadWorlds(); }
		catch (error) { fail(error); }
		finally { event.target.value = ''; }
	});

	// Список стилей — из данных игры и включённых модов; статичный запасной вариант — на случай,
	// если демон недоступен или схему прочитать не удалось.
	$('#world-create-playstyle').innerHTML = Object.entries(PLAYSTYLES).map(([value, title]) => `<option value="${esc(value)}">${esc(title)}</option>`).join('');
	VS.loadWorldSchema().then(schema => {
		if (!schema.playstyles?.length) return;
		$('#world-create-playstyle').innerHTML = schema.playstyles.map(style => `<option value="${esc(style.code)}" title="${esc(style.description)}">${esc(style.label)}</option>`).join('');
	}).catch(() => {});
	$('#world-create-form').addEventListener('submit', async event => {
		event.preventDefault();
		try {
			await busy(event.submitter, () => api('/api/worlds/create', {method: 'POST', body: {filename: $('#world-create-file').value.trim(), world_name: $('#world-create-name').value.trim(), seed: $('#world-create-seed').value.trim(), playstyle: $('#world-create-playstyle').value}}));
			toast('Мир подготовлен. Запустите сервер — игра создаст файл.', 'ok');
			await loadWorlds();
		} catch (error) { fail(error); }
	});

	// ---------- моды ----------

	function issueText(issue) {
		const required = issue.required ? ` ≥ ${issue.required}` : '';
		if (issue.status === 'missing') return `нет ${issue.modid}${required}`;
		if (issue.status === 'outdated') return `${issue.modid}: установлена ${issue.installed || "—"}, нужна ≥ ${issue.required}`;
		return `${issue.modid}: версию проверить не удалось (установлена ${issue.installed || "—"}, нужна ${issue.required || "—"})`;
	}

	/** Проблемы зависимостей, которые можно решить установкой. */
	const fixable = issue => issue.status === 'missing' || issue.status === 'outdated';

	async function loadMods() {
		const data = await api('/api/mods');
		mods = {mods: data.mods || [], rollbacks: data.rollbacks || {}, profiles: data.profiles || []};
		$('#mods-title').textContent = `Установленные моды · ${mods.mods.length} (включено ${data.enabled ?? "—"})`;
		const admin = VS.can('admin') ? '' : 'disabled';
		$('#mods-list').innerHTML = mods.mods.map(mod => {
			const issues = mod.dependency_issues || [];
			const deps = Object.entries(mod.dependencies || {}).map(([id, version]) => `${id}${version ? ` ≥ ${version}` : ""}`);
			const detail = [mod.version ? `v${mod.version}` : '', fmtBytes(mod.size), deps.length ? `зависит от ${deps.join(", ")}` : 'без зависимостей'].filter(Boolean).join(' · ');
			return `<div><span class="item-symbol">✥</span><span class="grow"><b>${esc(mod.display_name || mod.modid || mod.name)}</b><small>${esc(mod.name)} · ${esc(detail)}</small>${issues.map(issue => `<small class="accent">${esc(issueText(issue))}${fixable(issue) ? ` <button class="link-button" type="button" data-fix="${esc(issue.modid)}" ${admin}>${issue.status === "outdated" ? "Обновить" : "Доустановить"}</button>` : ""}</small>`).join("")}${mod.error ? `<small class="accent">${esc(mod.error)}</small>` : ""}</span>
				<em class="${mod.enabled ? (issues.length ? "warn" : "ok") : "off"}">${mod.enabled ? (issues.length ? "ПРОБЛЕМЫ" : "ВКЛЮЧЁН") : "ОТКЛЮЧЁН"}</em>
				<span class="row-actions"><button class="button small" type="button" data-mod="toggle" data-name="${esc(mod.name)}" ${admin}>${mod.enabled ? "Отключить" : "Включить"}</button><button class="button small danger" type="button" data-mod="delete" data-name="${esc(mod.name)}" ${admin}>Удалить</button></span></div>`;
		}).join('') || '<div class="empty">Моды не найдены.</div>';
		const problems = [...(data.duplicate_ids || []).map(id => `Повторяющийся modid: ${id}`), ...mods.mods.flatMap(mod => (mod.dependency_issues || []).map(issue => `${mod.modid || mod.name}: ${issueText(issue)}`))];
		const fixes = mods.mods.flatMap(mod => (mod.dependency_issues || []).filter(fixable));
		$('#mods-warning').innerHTML = `${esc(problems.join(' · '))}${fixes.length ? ` <button class="button small" type="button" id="mods-fix-all" ${admin}>Доустановить все недостающие</button>` : ''}`;
		$('#mods-warning').classList.toggle('hidden', !problems.length);
		$('#mod-profile-select').innerHTML = mods.profiles.length ? mods.profiles.map(name => `<option>${esc(name)}</option>`).join('') : '<option value="">Нет наборов</option>';
		const rollbacks = Object.entries(mods.rollbacks).flatMap(([modid, archives]) => archives.map(archive => ({modid, archive})));
		$('#mod-rollback-select').innerHTML = rollbacks.length ? rollbacks.map(item => `<option value="${esc(JSON.stringify(item))}">${esc(item.modid)} · ${esc(item.archive)}</option>`).join('') : '<option value="">Нет версий для отката</option>';
		VS.applyRoles($('#page-content'));
	}

	async function loadModLog() {
		const data = await api('/api/logs?category=mod&lines=300');
		$('#mods-log').textContent = (data.lines || []).slice(-80).join('\n') || 'Сообщений модов не найдено.';
	}

	/** Свежий список модов и диалог зависимостей для названных модов (или для всех, если список пуст). */
	async function reloadAndOffer(names, chain = new Set()) {
		await loadMods();
		const wanted = new Set((names || []).map(name => name.toLowerCase()));
		const issues = mods.mods
			.filter(mod => !wanted.size || wanted.has(String(mod.modid || '').toLowerCase()))
			.flatMap(mod => (mod.dependency_issues || []).filter(fixable));
		const unique = [...new Map(issues.map(issue => [issue.modid.toLowerCase(), issue])).values()];
		if (unique.length) await VS.dependencies.offer(unique, chain, mods.mods);
	}
	VS.mods = {reload: reloadAndOffer};

	$('#mods-warning').addEventListener('click', event => {
		if (event.target.closest('#mods-fix-all')) reloadAndOffer([]).catch(fail);
	});

	$('#mods-list').addEventListener('click', async event => {
		const fix = event.target.closest('[data-fix]');
		if (fix) {
			const issue = mods.mods.flatMap(mod => mod.dependency_issues || []).find(item => item.modid === fix.dataset.fix);
			if (issue) VS.dependencies.offer([issue], new Set(), mods.mods).catch(fail);
			return;
		}
		const button = event.target.closest('[data-mod]');
		if (!button) return;
		const name = button.dataset.name;
		try {
			if (button.dataset.mod === 'toggle') {
				await busy(button, () => api('/api/mods/toggle', {method: 'POST', body: {name}}));
				toast('Состояние мода изменено — перезапустите сервер', 'ok');
			} else {
				// Удаление мода подтверждается именем файла без глагола; фразу сверяет сервер.
				const result = await dialog({eyebrow: 'УДАЛЕНИЕ МОДА', title: 'Удалить мод?', text: 'Архив переместится в Mods/.history и останется доступен для отката.', phrase: name, confirm: 'Удалить', danger: true});
				if (!result) return;
				await busy(button, () => api('/api/mods/delete', {method: 'POST', body: {name, confirmation: result.phrase}}));
				toast('Мод перемещён в историю — перезапустите сервер', 'ok');
			}
			await loadMods();
		} catch (error) { fail(error); }
	});

	$('#mod-upload').addEventListener('change', async event => {
		const file = event.target.files[0];
		if (!file) return;
		const body = new FormData();
		body.append('file', file);
		try { const data = await api('/api/mods/upload', {method: 'POST', body}); toast(`Установлен ${data.modid} ${data.version}`, 'ok'); await reloadAndOffer([data.modid]); }
		catch (error) { fail(error); }
		finally { event.target.value = ''; }
	});

	async function installRelease(identifier, releaseId, button) {
		if (!await VS.confirmAction('Установить выбранный релиз?', 'Текущая версия мода сохранится в истории для отката. Изменение подействует после перезапуска сервера.', 'Установить')) return false;
		const data = await busy(button, () => api('/api/mods/install-release', {method: 'POST', body: {identifier, release_id: Number(releaseId)}}));
		toast(`Установлен ${data.modid} ${data.version}`, 'ok');
		VS.catalog?.markInstalled(data.modid, data.version);
		await reloadAndOffer([data.modid]);
		return true;
	}

	$('#mods-check-updates').addEventListener('click', async event => {
		const box = $('#mods-updates');
		box.className = 'notice';
		box.textContent = 'Проверка ModDB…';
		try {
			const data = await busy(event.currentTarget, () => api('/api/mods/updates'));
			const items = data.updates || [];
			const rows = VS.modUpdates(items);
			const branch = (data.game_version || '').split('.').slice(0, 2).join('.');
			const groups = [
				[items.filter(item => item.unavailable_reason === 'no_compatible_release' && !item.available_prerelease), `ModDB не указал релиз для ветки ${branch}.x`],
				[items.filter(item => item.unavailable_reason === 'no_releases'), 'На странице ModDB нет опубликованных релизов'],
				[items.filter(item => item.error), 'Страница ModDB не найдена или недоступна'],
			].filter(([list]) => list.length);
			const admin = VS.can('admin') ? '' : 'disabled';
			box.className = `notice ${rows.length ? "ok" : ""}`;
			box.innerHTML = (rows.length ? `<strong>Доступны совместимые релизы (игра ${esc(data.game_version || "—")}):</strong><div class="data-list">${rows.map(({item, release}) => `<div><span class="grow"><b>${esc(item.modid)}</b>${release.prerelease ? ' <span class="badge warn">предварительный</span>' : ''}<small>${esc(item.installed || "—")} → ${esc(release.version)}${release.compatibility === "same_minor" ? ` · метка ${esc(release.compatible_tag)}, не точная версия — проверьте changelog` : ""}</small></span><button class="button small ${release.prerelease ? "" : "primary"}" type="button" data-release-id="${esc(release.releaseid)}" data-identifier="${esc(release.identifier)}" ${admin}>Обновить</button></div>`).join("")}</div>` : 'Новых совместимых версий не найдено.')
				+ groups.map(([list, title]) => `<p>${esc(title)}: ${list.map(item => esc(item.modid)).join(", ")}</p>`).join('');
		} catch (error) { box.className = 'notice error'; box.textContent = error.message; }
	});

	document.addEventListener('click', async event => {
		const button = event.target.closest('[data-release-id]');
		if (!button) return;
		try { if (await installRelease(button.dataset.identifier, button.dataset.releaseId, button)) { button.textContent = 'Установлено'; button.disabled = true; } }
		catch (error) { fail(error); }
	});

	$('#mod-profile-form').addEventListener('submit', async event => {
		event.preventDefault();
		try { await busy(event.submitter, () => api('/api/mods/profile/save', {method: 'POST', body: {name: $('#mod-profile-name').value.trim()}})); $('#mod-profile-name').value = ''; toast('Набор сохранён', 'ok'); await loadMods(); }
		catch (error) { fail(error); }
	});
	$('#mod-profile-apply').addEventListener('click', async event => {
		const name = $('#mod-profile-select').value;
		if (!name || !await VS.confirmAction(`Применить набор «${name}»?`, 'Моды вне набора будут отключены. Изменение подействует после перезапуска сервера.', 'Применить')) return;
		try { await busy(event.currentTarget, () => api('/api/mods/profile/apply', {method: 'POST', body: {name}})); toast('Набор применён — перезапустите сервер', 'ok'); await loadMods(); }
		catch (error) { fail(error); }
	});
	$('#mod-profile-delete').addEventListener('click', async event => {
		const name = $('#mod-profile-select').value;
		if (!name || !await VS.confirmAction(`Удалить набор «${name}»?`, 'Файлы модов останутся на месте.', 'Удалить', true)) return;
		try { await busy(event.currentTarget, () => api('/api/mods/profile/delete', {method: 'POST', body: {name}})); toast('Набор удалён', 'ok'); await loadMods(); }
		catch (error) { fail(error); }
	});
	$('#mod-rollback').addEventListener('click', async event => {
		const value = $('#mod-rollback-select').value;
		if (!value) return;
		const item = JSON.parse(value);
		if (!await VS.confirmAction(`Вернуть ${item.modid}?`, `Будет восстановлена версия из ${item.archive}; текущая уйдёт в историю.`, 'Откатить')) return;
		try { await busy(event.currentTarget, () => api('/api/mods/rollback', {method: 'POST', body: item})); toast('Версия мода восстановлена', 'ok'); await loadMods(); }
		catch (error) { fail(error); }
	});

	// ---------- ModDB ----------

	$('#moddb-search-form').addEventListener('submit', async event => {
		event.preventDefault();
		const box = $('#moddb-results');
		box.innerHTML = '<div class="empty">Поиск…</div>';
		try {
			const data = await busy(event.submitter, () => api(`/api/moddb/search?q=${encodeURIComponent($("#moddb-query").value.trim())}`));
			box.innerHTML = (data.mods || []).map(mod => `<article class="catalog-item"><h4>${esc(mod.name)}</h4><small>${esc(mod.author || "—")} · ${esc(mod.side || "—")}</small><p>${esc(mod.summary || "")}</p><div><button class="button small" type="button" data-moddb="${esc(mod.urlalias || mod.modid)}" data-ids="${esc((mod.modidstrs || []).join(","))}">Страница мода</button></div></article>`).join('') || '<div class="empty">Ничего не найдено.</div>';
		} catch (error) { box.innerHTML = `<div class="notice error">${esc(error.message)}</div>`; }
	});

	$('#moddb-results').addEventListener('click', event => {
		const button = event.target.closest('[data-moddb]');
		if (button) VS.catalog.openMod(button.dataset.moddb, (button.dataset.ids || '').split(',').filter(Boolean)).catch(fail);
	});

	// ---------- архив и корзина ----------

	async function loadRecovery() {
		const data = await api('/api/worlds/archives');
		const admin = VS.can('admin');
		$('#recovery-list').innerHTML = (data.worlds || []).map(item => `<div><span class="item-symbol">▣</span><span class="grow"><b>${esc(item.original_name)}</b><small>${item.mode === "trash" ? "Корзина" : "Архив"} · ${esc(fmtBytes(item.size))} · ${esc(fmtDateTime(item.modified))}</small></span>
			<span class="row-actions"><button class="button small" type="button" data-restore="${esc(item.stored_name)}" data-mode="${esc(item.mode)}" data-original="${esc(item.original_name)}" ${data.server_active || !admin ? "disabled" : ""} title="${data.server_active ? "Остановите сервер" : ""}">Восстановить</button><button class="button small danger" type="button" data-purge="${esc(item.stored_name)}" data-mode="${esc(item.mode)}" ${admin ? "" : "disabled"}>Удалить навсегда</button></span></div>`).join('') || '<div class="empty">Архив и корзина пусты.</div>';
	}

	$('#recovery-list').addEventListener('click', async event => {
		const restore = event.target.closest('[data-restore]'), purge = event.target.closest('[data-purge]');
		try {
			if (restore) {
				const original = restore.dataset.original;
				const result = await dialog({eyebrow: 'ВОССТАНОВЛЕНИЕ', title: 'Восстановить мир', text: 'Мир вернётся в Saves под указанным именем. Текущий мир не заменяется.', fields: [{label: 'Имя восстанавливаемого файла', value: original, phraseFrom: value => `ВОССТАНОВИТЬ ${value.trim()}`}], phrase: `ВОССТАНОВИТЬ ${original}`, confirm: 'Восстановить'});
				if (!result) return;
				await busy(restore, () => api('/api/worlds/restore', {method: 'POST', body: {mode: restore.dataset.mode, stored_name: restore.dataset.restore, target_name: result.values[0].trim(), confirmation: result.phrase}}));
				toast('Мир восстановлен', 'ok');
			} else if (purge) {
				const stored = purge.dataset.purge;
				const result = await dialog({eyebrow: 'БЕЗВОЗВРАТНО', title: 'Удалить мир навсегда?', text: 'Архив и корзина — последний слой восстановления: после удаления вернуть мир будет невозможно.', phrase: `УДАЛИТЬ ${stored}`, confirm: 'Удалить навсегда', danger: true});
				if (!result) return;
				await busy(purge, () => api('/api/worlds/archives/delete', {method: 'POST', body: {mode: purge.dataset.mode, stored_name: stored, confirmation: result.phrase}}));
				toast('Мир удалён', 'ok');
			} else return;
			await loadRecovery();
		} catch (error) { fail(error); }
	});
	$('#recovery-refresh').addEventListener('click', event => busy(event.currentTarget, loadRecovery).catch(fail));

	// ---------- раздел ----------

	VS.pages.content = {
		enter(tab) { this.tab(tab); },
		tab(view) {
			if (view === 'worlds') loadWorlds().catch(fail);
			if (view === 'mods') Promise.all([loadMods(), loadModLog()]).catch(fail);
			if (view === 'recovery') loadRecovery().catch(fail);
			if (view === 'moddb') VS.catalog.enter();
		},
	};
})();
