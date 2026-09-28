'use strict';

// Миры и моды: файлы миров, установленные моды и их обновления, ModDB, наборы и откат,
// архив и корзина миров.

(() => {
	const {$, $$, esc, fmtBytes, fmtDateTime, api, toast, fail, busy, dialog} = VS;

	const PLAYSTYLES = {surviveandbuild: I18N.t('setup.playstyleSurvive'), exploration: I18N.t('setup.playstyleExploration'), wildernesssurvival: I18N.t('setup.playstyleWilderness'), homosapiens: 'Homo Sapiens', creativebuilding: I18N.t('setup.playstyleCreative')};
	let serverActive = false;
	let mods = {mods: [], rollbacks: {}, profiles: []};

	// ---------- миры ----------

	async function loadWorlds() {
		const data = await api('/api/worlds');
		serverActive = !!data.server_active;
		const notice = $('#worlds-state');
		notice.className = `notice ${serverActive ? 'warn' : 'ok'}`;
		notice.textContent = I18N.t(serverActive ? 'contentDynamic.worldsRunning' : 'contentDynamic.worldsStopped');
		const admin = VS.can('admin');
		const off = serverActive || !admin ? 'disabled' : '';
		$('#worlds-list').innerHTML = (data.worlds || []).map(world => `<div>
			<span class="item-symbol">${world.active ? '◈' : '◇'}</span>
			<span class="grow"><b>${esc(world.name)}</b><small>${esc(fmtBytes(world.size))} · ${esc(I18N.t('contentDynamic.modified', {date: fmtDateTime(world.modified)}))}</small></span>
			${world.active ? `<em class="ok">${esc(I18N.t('contentDynamic.active'))}</em>` : ''}
			<span class="row-actions">
				<a class="button small" href="/api/worlds/download/${encodeURIComponent(world.name)}" download>${esc(I18N.t('common.download'))}</a>
				${world.active ? '' : `<button class="button small" type="button" data-world="activate" data-name="${esc(world.name)}" ${off}>${esc(I18N.t('contentDynamic.makeActive'))}</button>`}
				<button class="button small" type="button" data-world="clone" data-name="${esc(world.name)}" ${off}>${esc(I18N.t('contentDynamic.clone'))}</button>
				${world.active ? '' : `<button class="button small" type="button" data-world="archive" data-name="${esc(world.name)}" ${off}>${esc(I18N.t('contentDynamic.toArchive'))}</button><button class="button small danger" type="button" data-world="trash" data-name="${esc(world.name)}" ${off}>${esc(I18N.t('contentDynamic.toTrash'))}</button>`}
			</span></div>`).join('') || `<div class="empty">${esc(I18N.t('contentDynamic.noWorldFiles'))}</div>`;
		$$('#world-create-form input, #world-create-form select, #world-create-form button').forEach(control => { if (!control.dataset.roleLocked) control.disabled = serverActive; });
	}

	$('#worlds-list').addEventListener('click', async event => {
		const button = event.target.closest('[data-world]');
		if (!button) return;
		const action = button.dataset.world, name = button.dataset.name;
		try {
			if (action === 'clone') {
				const result = await dialog({eyebrow: I18N.t('contentDynamic.cloneEyebrow'), title: I18N.t('contentDynamic.cloneTitle', {name}), text: I18N.t('contentDynamic.cloneDetail'), fields: [{label: I18N.t('contentDynamic.cloneFileName'), value: `copy-${name}`}], confirm: I18N.t('contentDynamic.clone')});
				if (!result) return;
				await busy(button, () => api('/api/worlds/clone', {method: 'POST', body: {source: name, name: result.values[0].trim()}}));
				toast(I18N.t('contentDynamic.worldCloned'), 'ok');
			} else if (action === 'activate') {
				if (!await VS.confirmAction(I18N.t('contentDynamic.activateTitle', {name}), I18N.t('contentDynamic.activateDetail'), I18N.t('contentDynamic.makeActive'))) return;
				await busy(button, () => api('/api/worlds/action', {method: 'POST', body: {name, action}}));
				toast(I18N.t('contentDynamic.activeChanged'), 'ok');
			} else {
				const archive = action === 'archive';
				const phrase = I18N.t(archive ? 'core.phraseArchive' : 'core.phraseDelete', {name});
				const result = await dialog({eyebrow: I18N.t(archive ? 'contentDynamic.archiveEyebrow' : 'contentDynamic.trashEyebrow'), title: I18N.t(archive ? 'contentDynamic.archiveTitle' : 'contentDynamic.trashTitle'), text: I18N.t('contentDynamic.moveWorldDetail', {folder: archive ? '.archive' : '.trash'}), phrase, confirm: I18N.t(archive ? 'contentDynamic.toArchive' : 'contentDynamic.toTrash'), danger: !archive});
				if (!result) return;
				await busy(button, () => api('/api/worlds/action', {method: 'POST', body: {name, action, confirmation: result.phrase}}));
				toast(I18N.t('contentDynamic.operationDone'), 'ok');
			}
			await loadWorlds();
		} catch (error) { fail(error); }
	});

	$('#world-upload').addEventListener('change', async event => {
		const file = event.target.files[0];
		if (!file) return;
		const body = new FormData();
		body.append('file', file);
		toast(I18N.t('contentDynamic.uploadingWorld', {name: file.name, size: fmtBytes(file.size)}));
		try { await api('/api/worlds/upload', {method: 'POST', body}); toast(I18N.t('contentDynamic.worldUploaded'), 'ok'); await loadWorlds(); }
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
			toast(I18N.t('contentDynamic.worldPrepared'), 'ok');
			await loadWorlds();
		} catch (error) { fail(error); }
	});

	// ---------- моды ----------

	function issueText(issue) {
		const required = issue.required ? ` ≥ ${issue.required}` : '';
		if (issue.status === 'missing') return I18N.t('contentDynamic.issueMissing', {mod: issue.modid, required});
		if (issue.status === 'outdated') return I18N.t('contentDynamic.issueOutdated', {mod: issue.modid, installed: issue.installed || '—', required: issue.required});
		return I18N.t('contentDynamic.issueUnknown', {mod: issue.modid, installed: issue.installed || '—', required: issue.required || '—'});
	}

	/** Проблемы зависимостей, которые можно решить установкой. */
	const fixable = issue => issue.status === 'missing' || issue.status === 'outdated';

	async function loadMods() {
		const data = await api('/api/mods');
		mods = {mods: data.mods || [], rollbacks: data.rollbacks || {}, profiles: data.profiles || []};
		$('#mods-title').textContent = I18N.t('contentDynamic.installedMods', {count: mods.mods.length, enabled: data.enabled ?? '—'});
		const admin = VS.can('admin') ? '' : 'disabled';
		$('#mods-list').innerHTML = mods.mods.map(mod => {
			const issues = mod.dependency_issues || [];
			const deps = Object.entries(mod.dependencies || {}).map(([id, version]) => `${id}${version ? ` ≥ ${version}` : ''}`);
			const detail = [mod.version ? `v${mod.version}` : '', fmtBytes(mod.size), deps.length ? I18N.t('contentDynamic.dependsOn', {names: deps.join(', ')}) : I18N.t('contentDynamic.noDependencies')].filter(Boolean).join(' · ');
			return `<div><span class="item-symbol">✥</span><span class="grow"><b>${esc(mod.display_name || mod.modid || mod.name)}</b><small>${esc(mod.name)} · ${esc(detail)}</small>${issues.map(issue => `<small class="accent">${esc(issueText(issue))}${fixable(issue) ? ` <button class="link-button" type="button" data-fix="${esc(issue.modid)}" ${admin}>${issue.status === 'outdated' ? I18N.t('common.update') : I18N.t('contentDynamic.installMissing')}</button>` : ''}</small>`).join('')}${mod.error ? `<small class="accent">${esc(mod.error)}</small>` : ''}</span>
				<em class="${mod.enabled ? (issues.length ? 'warn' : 'ok') : 'off'}">${mod.enabled ? (issues.length ? I18N.t('contentDynamic.problems') : I18N.t('contentDynamic.enabled')) : I18N.t('contentDynamic.disabled')}</em>
				<span class="row-actions"><button class="button small" type="button" data-mod="toggle" data-name="${esc(mod.name)}" ${admin}>${mod.enabled ? I18N.t('contentDynamic.disable') : I18N.t('contentDynamic.enable')}</button><button class="button small danger" type="button" data-mod="delete" data-name="${esc(mod.name)}" ${admin}>${esc(I18N.t('common.delete'))}</button></span></div>`;
		}).join('') || `<div class="empty">${esc(I18N.t('contentDynamic.noMods'))}</div>`;
		const problems = [...(data.duplicate_ids || []).map(id => I18N.t('contentDynamic.duplicateId', {id})), ...mods.mods.flatMap(mod => (mod.dependency_issues || []).map(issue => `${mod.modid || mod.name}: ${issueText(issue)}`))];
		const fixes = mods.mods.flatMap(mod => (mod.dependency_issues || []).filter(fixable));
		$('#mods-warning').innerHTML = `${esc(problems.join(' · '))}${fixes.length ? ` <button class="button small" type="button" id="mods-fix-all" ${admin}>${esc(I18N.t('contentDynamic.installAllMissing'))}</button>` : ''}`;
		$('#mods-warning').classList.toggle('hidden', !problems.length);
		$('#mod-profile-select').innerHTML = mods.profiles.length ? mods.profiles.map(name => `<option>${esc(name)}</option>`).join('') : `<option value="">${esc(I18N.t('contentDynamic.noProfiles'))}</option>`;
		const rollbacks = Object.entries(mods.rollbacks).flatMap(([modid, archives]) => archives.map(archive => ({modid, archive})));
		$('#mod-rollback-select').innerHTML = rollbacks.length ? rollbacks.map(item => `<option value="${esc(JSON.stringify(item))}">${esc(item.modid)} · ${esc(item.archive)}</option>`).join('') : `<option value="">${esc(I18N.t('contentDynamic.noRollbackVersions'))}</option>`;
		VS.applyRoles($('#page-content'));
	}

	async function loadModLog() {
		const data = await api('/api/logs?category=mod&lines=300');
		$('#mods-log').textContent = (data.lines || []).slice(-80).join('\n') || I18N.t('contentDynamic.noModMessages');
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
				toast(I18N.t('contentDynamic.modStateChanged'), 'ok');
			} else {
				// Удаление мода подтверждается именем файла без глагола; фразу сверяет сервер.
				const result = await dialog({eyebrow: I18N.t('contentDynamic.deleteModEyebrow'), title: I18N.t('contentDynamic.deleteModTitle'), text: I18N.t('contentDynamic.deleteModDetail'), phrase: name, confirm: I18N.t('common.delete'), danger: true});
				if (!result) return;
				await busy(button, () => api('/api/mods/delete', {method: 'POST', body: {name, confirmation: result.phrase}}));
				toast(I18N.t('contentDynamic.modMovedToHistory'), 'ok');
			}
			await loadMods();
		} catch (error) { fail(error); }
	});

	$('#mod-upload').addEventListener('change', async event => {
		const file = event.target.files[0];
		if (!file) return;
		const body = new FormData();
		body.append('file', file);
		try { const data = await api('/api/mods/upload', {method: 'POST', body}); toast(I18N.t('contentDynamic.modInstalled', {mod: data.modid, version: data.version}), 'ok'); await reloadAndOffer([data.modid]); }
		catch (error) { fail(error); }
		finally { event.target.value = ''; }
	});

	async function installRelease(identifier, releaseId, button) {
		if (!await VS.confirmAction(I18N.t('contentDynamic.installReleaseTitle'), I18N.t('contentDynamic.installReleaseDetail'), I18N.t('common.install'))) return false;
		const data = await busy(button, () => api('/api/mods/install-release', {method: 'POST', body: {identifier, release_id: Number(releaseId)}}));
		toast(I18N.t('contentDynamic.modInstalled', {mod: data.modid, version: data.version}), 'ok');
		VS.catalog?.markInstalled(data.modid, data.version);
		await reloadAndOffer([data.modid]);
		return true;
	}

	$('#mods-check-updates').addEventListener('click', async event => {
		const box = $('#mods-updates');
		box.className = 'notice';
		box.textContent = I18N.t('contentDynamic.checkingModdb');
		try {
			const data = await busy(event.currentTarget, () => api('/api/mods/updates'));
			const items = data.updates || [];
			const rows = VS.modUpdates(items);
			const branch = (data.game_version || '').split('.').slice(0, 2).join('.');
			const groups = [
				[items.filter(item => item.unavailable_reason === 'no_compatible_release' && !item.available_prerelease), I18N.t('contentDynamic.noBranchRelease', {branch})],
				[items.filter(item => item.unavailable_reason === 'no_releases'), I18N.t('contentDynamic.noPublishedReleases')],
				[items.filter(item => item.error), I18N.t('contentDynamic.moddbUnavailable')],
			].filter(([list]) => list.length);
			const admin = VS.can('admin') ? '' : 'disabled';
			box.className = `notice ${rows.length ? 'ok' : ''}`;
			box.innerHTML = (rows.length ? `<strong>${esc(I18N.t('contentDynamic.compatibleReleases', {version: data.game_version || '—'}))}</strong><div class="data-list">${rows.map(({item, release}) => `<div><span class="grow"><b>${esc(item.modid)}</b>${release.prerelease ? ` <span class="badge warn">${esc(I18N.t('contentDynamic.prerelease'))}</span>` : ''}<small>${esc(item.installed || '—')} → ${esc(release.version)}${release.compatibility === 'same_minor' ? I18N.t('contentDynamic.compatibleTagWarning', {tag: esc(release.compatible_tag)}) : ''}</small></span><button class="button small ${release.prerelease ? '' : 'primary'}" type="button" data-release-id="${esc(release.releaseid)}" data-identifier="${esc(release.identifier)}" ${admin}>${esc(I18N.t('common.update'))}</button></div>`).join('')}</div>` : I18N.t('contentDynamic.noCompatibleUpdates'))
				+ groups.map(([list, title]) => `<p>${esc(title)}: ${list.map(item => esc(item.modid)).join(', ')}</p>`).join('');
		} catch (error) { box.className = 'notice error'; box.textContent = error.message; }
	});

	document.addEventListener('click', async event => {
		const button = event.target.closest('[data-release-id]');
		if (!button) return;
		try { if (await installRelease(button.dataset.identifier, button.dataset.releaseId, button)) { button.textContent = I18N.t('contentDynamic.installed'); button.disabled = true; } }
		catch (error) { fail(error); }
	});

	$('#mod-profile-form').addEventListener('submit', async event => {
		event.preventDefault();
		try { await busy(event.submitter, () => api('/api/mods/profile/save', {method: 'POST', body: {name: $('#mod-profile-name').value.trim()}})); $('#mod-profile-name').value = ''; toast(I18N.t('contentDynamic.profileSaved'), 'ok'); await loadMods(); }
		catch (error) { fail(error); }
	});
	$('#mod-profile-apply').addEventListener('click', async event => {
		const name = $('#mod-profile-select').value;
		if (!name || !await VS.confirmAction(I18N.t('contentDynamic.applyProfileTitle', {name}), I18N.t('contentDynamic.applyProfileDetail'), I18N.t('common.apply'))) return;
		try { await busy(event.currentTarget, () => api('/api/mods/profile/apply', {method: 'POST', body: {name}})); toast(I18N.t('contentDynamic.profileApplied'), 'ok'); await loadMods(); }
		catch (error) { fail(error); }
	});
	$('#mod-profile-delete').addEventListener('click', async event => {
		const name = $('#mod-profile-select').value;
		if (!name || !await VS.confirmAction(I18N.t('contentDynamic.deleteProfileTitle', {name}), I18N.t('contentDynamic.deleteProfileDetail'), I18N.t('common.delete'), true)) return;
		try { await busy(event.currentTarget, () => api('/api/mods/profile/delete', {method: 'POST', body: {name}})); toast(I18N.t('contentDynamic.profileDeleted'), 'ok'); await loadMods(); }
		catch (error) { fail(error); }
	});
	$('#mod-rollback').addEventListener('click', async event => {
		const value = $('#mod-rollback-select').value;
		if (!value) return;
		const item = JSON.parse(value);
		if (!await VS.confirmAction(I18N.t('contentDynamic.rollbackTitle', {mod: item.modid}), I18N.t('contentDynamic.rollbackDetail', {archive: item.archive}), I18N.t('contentDynamic.rollback'))) return;
		try { await busy(event.currentTarget, () => api('/api/mods/rollback', {method: 'POST', body: item})); toast(I18N.t('contentDynamic.modRestored'), 'ok'); await loadMods(); }
		catch (error) { fail(error); }
	});

	// ---------- ModDB ----------

	$('#moddb-search-form').addEventListener('submit', async event => {
		event.preventDefault();
		const box = $('#moddb-results');
		box.innerHTML = `<div class="empty">${esc(I18N.t('common.searching'))}</div>`;
		try {
			const data = await busy(event.submitter, () => api(`/api/moddb/search?q=${encodeURIComponent($('#moddb-query').value.trim())}`));
			box.innerHTML = (data.mods || []).map(mod => `<article class="catalog-item"><h4>${esc(mod.name)}</h4><small>${esc(mod.author || '—')} · ${esc(mod.side || '—')}</small><p>${esc(mod.summary || '')}</p><div><button class="button small" type="button" data-moddb="${esc(mod.urlalias || mod.modid)}" data-ids="${esc((mod.modidstrs || []).join(','))}">${esc(I18N.t('contentDynamic.modPage'))}</button></div></article>`).join('') || `<div class="empty">${esc(I18N.t('common.nothingFound'))}</div>`;
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
		$('#recovery-list').innerHTML = (data.worlds || []).map(item => `<div><span class="item-symbol">▣</span><span class="grow"><b>${esc(item.original_name)}</b><small>${item.mode === 'trash' ? esc(I18N.t('contentDynamic.trash')) : esc(I18N.t('contentDynamic.archive'))} · ${esc(fmtBytes(item.size))} · ${esc(fmtDateTime(item.modified))}</small></span>
			<span class="row-actions"><button class="button small" type="button" data-restore="${esc(item.stored_name)}" data-mode="${esc(item.mode)}" data-original="${esc(item.original_name)}" ${data.server_active || !admin ? 'disabled' : ''} title="${data.server_active ? esc(I18N.t('contentDynamic.stopServer')) : ''}">${esc(I18N.t('common.restore'))}</button><button class="button small danger" type="button" data-purge="${esc(item.stored_name)}" data-mode="${esc(item.mode)}" ${admin ? '' : 'disabled'}>${esc(I18N.t('contentDynamic.deleteForever'))}</button></span></div>`).join('') || `<div class="empty">${esc(I18N.t('contentDynamic.recoveryEmpty'))}</div>`;
	}

	$('#recovery-list').addEventListener('click', async event => {
		const restore = event.target.closest('[data-restore]'), purge = event.target.closest('[data-purge]');
		try {
			if (restore) {
				const original = restore.dataset.original;
				const result = await dialog({eyebrow: I18N.t('contentDynamic.restoreEyebrow'), title: I18N.t('contentDynamic.restoreWorldTitle'), text: I18N.t('contentDynamic.restoreWorldDetail'), fields: [{label: I18N.t('contentDynamic.restoreFilename'), value: original, phraseFrom: value => I18N.t('core.phraseRestore', {name: value.trim()})}], phrase: I18N.t('core.phraseRestore', {name: original}), confirm: I18N.t('common.restore')});
				if (!result) return;
				await busy(restore, () => api('/api/worlds/restore', {method: 'POST', body: {mode: restore.dataset.mode, stored_name: restore.dataset.restore, target_name: result.values[0].trim(), confirmation: result.phrase}}));
				toast(I18N.t('contentDynamic.worldRestored'), 'ok');
			} else if (purge) {
				const stored = purge.dataset.purge;
				const result = await dialog({eyebrow: I18N.t('contentDynamic.irreversibleEyebrow'), title: I18N.t('contentDynamic.deleteWorldForeverTitle'), text: I18N.t('contentDynamic.deleteWorldForeverDetail'), phrase: I18N.t('core.phraseDelete', {name: stored}), confirm: I18N.t('contentDynamic.deleteForever'), danger: true});
				if (!result) return;
				await busy(purge, () => api('/api/worlds/archives/delete', {method: 'POST', body: {mode: purge.dataset.mode, stored_name: stored, confirmation: result.phrase}}));
				toast(I18N.t('contentDynamic.worldDeleted'), 'ok');
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
