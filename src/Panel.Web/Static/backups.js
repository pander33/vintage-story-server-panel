'use strict';

// Резервные копии: список, создание, восстановление, извлечение мира, очистка и история.

(() => {
	const {$, esc, fmtBytes, fmtDateTime, fmtDay, fmtShortTime, api, toast, fail, busy, dialog} = VS;

	const STORES = {'mod-history': I18N.t('backups.storeModHistory'), 'config-backup': I18N.t('backups.storeConfigBackup'), 'mod-config-backup': I18N.t('backups.storeModConfigBackup')};
	let backups = [];
	let history = null;

	// ---------- список ----------

	async function loadBackups() {
		const [data, metrics] = await Promise.all([api('/api/backups'), api('/api/metrics?hours=1').catch(() => ({}))]);
		backups = (data.backups || []).slice().sort((a, b) => b.modified - a.modified);
		const last = backups[0];
		$('#backup-last').textContent = last ? `${fmtDay(last.modified)}, ${fmtShortTime(last.modified)}` : I18N.t('backupsDynamic.noBackupsShort');
		$('#backup-last-detail').textContent = last ? `${fmtBytes(last.size)} · ${last.name}` : I18N.t('backupsDynamic.createFirst');
		const countForm = new Intl.PluralRules(I18N.locale()).select(backups.length);
		$('#backup-count').textContent = I18N.t(`backupsDynamic.archiveCount.${countForm}`, {count: backups.length});
		$('#backup-total').textContent = I18N.t('backupsDynamic.occupied', {size: fmtBytes(backups.reduce((sum, item) => sum + (Number(item.size) || 0), 0))});
		const current = metrics.current || {};
		$('#backup-free').textContent = current.disk_free ? fmtBytes(current.disk_free) : '—';
		$('#backup-free-detail').textContent = current.disk_warning ? I18N.t('backupsDynamic.lowSpace') : (current.disk_total ? I18N.t('backupsDynamic.ofTotal', {size: fmtBytes(current.disk_total)}) : '—');
		const operator = VS.can('operator'), admin = VS.can('admin'), onlyOne = backups.length <= 1;
		$('#backups-list').innerHTML = backups.map((item, index) => `<div><span class="item-symbol">▣</span><span class="grow"><b class="mono">${esc(item.name)}</b><small>${esc(fmtDateTime(item.modified))} · ${esc(fmtBytes(item.size))}${index === 0 ? ` · ${esc(I18N.t('backupsDynamic.latest'))}` : ''}</small></span>
			<span class="row-actions"><button class="button small" type="button" data-backup-inspect="${esc(item.name)}">${esc(I18N.t('backupsDynamic.contents'))}</button><button class="button small" type="button" data-backup-restore="${esc(item.name)}" ${admin ? '' : 'disabled'}>${esc(I18N.t('backupsDynamic.restore'))}</button><button class="button small danger" type="button" data-backup-delete="${esc(item.name)}" ${admin && !onlyOne ? '' : 'disabled'} title="${onlyOne ? esc(I18N.t('backupsDynamic.cannotDeleteLast')) : ''}">${esc(I18N.t('common.delete'))}</button></span></div>`).join('') || `<div class="empty">${esc(I18N.t('backupsDynamic.noBackups'))}</div>`;
		$('#extract-backup').innerHTML = backups.map(item => `<option value="${esc(item.name)}">${esc(item.name)} · ${esc(fmtBytes(item.size))}</option>`).join('') || `<option value="">${esc(I18N.t('backupsDynamic.noBackupsShort'))}</option>`;
		if (!operator) $('#backup-create').disabled = true;
	}

	$('#backups-list').addEventListener('click', async event => {
		const inspect = event.target.closest('[data-backup-inspect]'), restore = event.target.closest('[data-backup-restore]'), remove = event.target.closest('[data-backup-delete]');
		try {
			if (inspect) {
				const data = await busy(inspect, () => api(`/api/backups/${encodeURIComponent(inspect.dataset.backupInspect)}/inspect`));
				await dialog({eyebrow: I18N.t('backupsDynamic.contentsEyebrow'), title: inspect.dataset.backupInspect, text: I18N.t('backupsDynamic.inspectionDetail', {files: data.files, worlds: (data.worlds || []).map(world => `• ${world.name} — ${fmtBytes(world.size)}`).join('\n') || I18N.t('backupsDynamic.notFound')}), confirm: I18N.t('common.close'), cancel: I18N.t('setup.back')});
			} else if (restore) {
				const name = restore.dataset.backupRestore;
				const result = await dialog({eyebrow: I18N.t('backupsDynamic.restoreEyebrow'), title: I18N.t('backupsDynamic.restoreTitle'), text: I18N.t('backupsDynamic.restoreDetail'), phrase: I18N.t('core.phraseRestore', {name}), confirm: I18N.t('backupsDynamic.restore'), danger: true});
				if (!result) return;
				await busy(restore, () => api('/api/backups/restore', {method: 'POST', body: {name, confirmation: result.phrase}}));
				toast(I18N.t('backupsDynamic.restored'), 'ok');
				await loadBackups();
				VS.refreshStatus();
			} else if (remove) {
				const name = remove.dataset.backupDelete;
				const result = await dialog({eyebrow: I18N.t('backupsDynamic.irreversible'), title: I18N.t('backupsDynamic.deleteTitle'), text: I18N.t('backupsDynamic.deleteDetail'), phrase: I18N.t('core.phraseDelete', {name}), confirm: I18N.t('common.delete'), danger: true});
				if (!result) return;
				await busy(remove, () => api('/api/backups/delete', {method: 'POST', body: {name, confirmation: result.phrase}}));
				toast(I18N.t('backupsDynamic.deleted'), 'ok');
				await loadBackups();
			}
		} catch (error) { fail(error); }
	});

	$('#backup-create').addEventListener('click', async event => {
		if (!await VS.confirmAction(I18N.t('backupsDynamic.createTitle'), I18N.t('backupsDynamic.createDetail'), I18N.t('backupsDynamic.createConfirm'))) return;
				try { const data = await busy(event.currentTarget, () => api('/api/backups', {method: 'POST'})); toast(I18N.t('backupsDynamic.created', {name: data.name}), 'ok'); await loadBackups(); VS.refreshStatus(); }
		catch (error) { fail(error); }
	});
	$('#backups-refresh').addEventListener('click', event => busy(event.currentTarget, loadBackups).catch(fail));

	// ---------- извлечение мира ----------

	$('#extract-inspect').addEventListener('click', async event => {
		const name = $('#extract-backup').value;
		if (!name) return;
		try {
			const data = await busy(event.currentTarget, () => api(`/api/backups/${encodeURIComponent(name)}/inspect`));
			$('#extract-world').innerHTML = (data.worlds || []).map(world => `<option value="${esc(world.name)}">${esc(world.name)} · ${esc(fmtBytes(world.size))}</option>`).join('') || `<option value="">${esc(I18N.t('backupsDynamic.noWorlds'))}</option>`;
			toast(I18N.t('backupsDynamic.inspected', {files: data.files}));
		} catch (error) { fail(error); }
	});
	$('#extract-backup').addEventListener('change', () => { $('#extract-world').innerHTML = `<option value="">${esc(I18N.t('backupsDynamic.showWorldsFirst'))}</option>`; });

	$('#extract-form').addEventListener('submit', async event => {
		event.preventDefault();
		const backup = $('#extract-backup').value, world = $('#extract-world').value, target = $('#extract-target').value.trim();
		if (!world) { toast(I18N.t('backupsDynamic.chooseWorldFirst'), 'error'); return; }
		const result = await dialog({eyebrow: I18N.t('backupsDynamic.extractEyebrow'), title: I18N.t('backupsDynamic.extractTitle', {world}), text: I18N.t('backupsDynamic.extractDetail', {target}), phrase: I18N.t('core.phraseExtract', {name: target}), confirm: I18N.t('backupsDynamic.extractConfirm')});
		if (!result) return;
		try {
			await busy(event.submitter, () => api('/api/backups/restore-world', {method: 'POST', body: {backup, world, target_name: target, confirmation: result.phrase}}));
			const box = $('#extract-result');
			box.className = 'notice ok';
			box.textContent = I18N.t('backupsDynamic.extracted', {target});
		} catch (error) { fail(error); }
	});

	// ---------- очистка ----------

	async function retention(apply) {
		const body = {keep_count: Number($('#retention-count').value), keep_days: Number($('#retention-days').value), minimum_free_gb: Number($('#retention-free').value), apply, confirmation: ''};
		if (apply) {
			const result = await dialog({eyebrow: I18N.t('backupsDynamic.irreversible'), title: I18N.t('backupsDynamic.deleteShownTitle'), text: I18N.t('backupsDynamic.deleteShownDetail'), phrase: I18N.t('core.phraseClearBackups'), confirm: I18N.t('common.delete'), danger: true});
			if (!result) return;
			body.confirmation = result.phrase;
		}
		const data = await api('/api/backups/retention', {method: 'POST', body});
		const box = $('#retention-result');
		box.classList.remove('hidden');
		if (apply) {
			box.className = 'notice ok';
			box.textContent = I18N.t('backupsDynamic.retentionApplied', {count: (data.removed || []).length, free: fmtBytes(data.free_after || 0)});
			await loadBackups();
		} else {
			const candidates = data.candidates || [];
			box.className = `notice ${candidates.length ? 'warn' : 'ok'}`;
			box.innerHTML = candidates.length
				? `<strong>${esc(I18N.t('backupsDynamic.retentionPreview', {count: candidates.length, bytes: fmtBytes(data.bytes || 0)}))}</strong> ${esc(I18N.t(data.target_met ? 'backupsDynamic.targetMet' : 'backupsDynamic.targetNotMet'))}<p class="mono">${candidates.map(esc).join('<br>')}</p>`
				: esc(I18N.t('backupsDynamic.noCandidates'));
		}
		$('#retention-apply').disabled = apply || !(data.candidates || []).length;
	}
	$('#retention-form').addEventListener('submit', event => { event.preventDefault(); busy(event.submitter, () => retention(false)).catch(fail); });
	$('#retention-form').addEventListener('input', () => { $('#retention-apply').disabled = true; });
	$('#retention-apply').addEventListener('click', event => busy(event.currentTarget, () => retention(true)).catch(fail));

	// ---------- история ----------

	async function loadHistory() { history = await api('/api/history'); renderHistory(); }

	function renderHistory() {
		if (!history) return;
		const store = $('#history-store').value;
		const items = (history.stores || {})[store] || [];
		$('#history-summary').innerHTML = `<span>${esc(I18N.t('backupsDynamic.storage'))} <b>${esc(STORES[store])}</b></span><span>${esc(I18N.t('backupsDynamic.records'))} <b>${items.length}</b></span><span>${esc(I18N.t('backupsDynamic.occupiedLabel'))} <b>${esc(fmtBytes((history.bytes || {})[store] || 0))}</b></span>`;
		$('#history-list').innerHTML = items.map(item => `<div><span class="grow"><b>${esc(item.name)}</b><small>${esc(item.group)} · ${esc(fmtDateTime(item.modified))} · ${esc(fmtBytes(item.size))}</small></span><button class="button small danger" type="button" data-history-key="${esc(item.key)}">${esc(I18N.t('common.delete'))}</button></div>`).join('') || `<div class="empty">${esc(I18N.t('backupsDynamic.noHistoryRecords'))}</div>`;
	}

	$('#history-list').addEventListener('click', async event => {
		const button = event.target.closest('[data-history-key]');
		if (!button) return;
		if (!await VS.confirmAction(I18N.t('backupsDynamic.deleteHistoryTitle'), button.dataset.historyKey, I18N.t('common.delete'), true)) return;
		try { await busy(button, () => api('/api/history/delete', {method: 'POST', body: {store: $('#history-store').value, key: button.dataset.historyKey}})); toast(I18N.t('backupsDynamic.historyDeleted'), 'ok'); await loadHistory(); }
		catch (error) { fail(error); }
	});
	$('#history-prune').addEventListener('click', async event => {
		const keep = Number($('#history-keep').value);
		const result = await dialog({eyebrow: I18N.t('backupsDynamic.pruneEyebrow'), title: I18N.t('backupsDynamic.pruneTitle'), text: I18N.t('backupsDynamic.pruneDetail', {keep}), phrase: I18N.t('core.phraseClearHistory'), confirm: I18N.t('backupsDynamic.pruneConfirm'), danger: true});
		if (!result) return;
		try { const data = await busy(event.currentTarget, () => api('/api/history/prune', {method: 'POST', body: {store: $('#history-store').value, keep, confirmation: result.phrase}})); toast(I18N.t('backupsDynamic.recordsDeleted', {count: (data.removed || []).length}), 'ok'); await loadHistory(); }
		catch (error) { fail(error); }
	});
	$('#history-store').addEventListener('change', renderHistory);
	$('#history-refresh').addEventListener('click', event => busy(event.currentTarget, loadHistory).catch(fail));

	// ---------- перенос сервера ----------

	let inspectedPackage = null;
	$('#package-export').addEventListener('click', async event => {
		const server = VS.currentServer();
		if (!VS.can('admin') || !server) { toast(I18N.t('backupsDynamic.chooseExportServer'), 'error'); return; }
		if (!await VS.confirmAction(I18N.t('backupsDynamic.exportTitle'), I18N.t('backupsDynamic.exportDetail'), I18N.t('backupsDynamic.exportConfirm'))) return;
		try {
			const archive = await busy(event.currentTarget, () => api('/api/server/package', {method: 'POST', response: 'blob'}));
			const url = URL.createObjectURL(archive);
			try {
				const link = document.createElement('a');
				link.href = url;
				link.download = `server-${server.id}.tar.gz`;
				document.body.append(link);
				link.click();
				link.remove();
			} finally { setTimeout(() => URL.revokeObjectURL(url), 60000); }
			toast(I18N.t('backupsDynamic.packageDownloaded'), 'ok');
		} catch (error) { fail(error); }
	});

	$('#package-file').addEventListener('change', () => {
		inspectedPackage = null;
		$('#package-import-form').classList.add('hidden');
		$('#package-inspection').classList.add('hidden');
	});

	$('#package-upload-form').addEventListener('submit', async event => {
		event.preventDefault();
		if (!VS.can('admin')) return;
		const file = $('#package-file').files[0];
		if (!file) return;
		inspectedPackage = null;
		$('#package-import-form').classList.add('hidden');
		const notice = $('#package-inspection');
		notice.className = 'notice warn';
		notice.textContent = I18N.t('backupsDynamic.uploadingPackage', {name: file.name, size: fmtBytes(file.size)});
		try {
			const data = await busy(event.submitter, () => api('/api/provision/transfer', {method: 'POST', body: file}));
			if (!data.plan || !data.sha256 || !Number.isSafeInteger(data.length)) throw new Error(I18N.t('backupsDynamic.invalidInspection'));
			inspectedPackage = data;
			notice.className = 'notice ok';
			notice.textContent = I18N.t('backupsDynamic.packageInspected', {name: file.name, size: fmtBytes(data.length), world: data.active_world || I18N.t('backupsDynamic.notSpecified'), version: data.game_version || I18N.t('backupsDynamic.unknown'), sha: data.sha256});
			$('#package-id').value = '';
			$('#package-name').value = '';
			$('#package-version').value = data.game_version || '';
			$('#package-port').value = '';
			$('#package-server-name').value = '';
			$('#package-world-choice').checked = false;
			$('#package-upgrade').checked = false;
			$('#package-import-form').classList.remove('hidden');
		} catch (error) { notice.className = 'notice error'; notice.textContent = error.message; }
	});

	$('#package-import-form').addEventListener('submit', async event => {
		event.preventDefault();
		if (!VS.can('admin') || !inspectedPackage) return;
		const packageData = inspectedPackage;
		const server = {
			id: $('#package-id').value.trim(), name: $('#package-name').value.trim(), version: $('#package-version').value.trim(),
			port: Number($('#package-port').value), server_name: $('#package-server-name').value.trim(),
		};
		const confirmation = await dialog({eyebrow: I18N.t('backupsDynamic.importEyebrow'), title: I18N.t('backupsDynamic.importTitle', {name: server.name}), text: I18N.t('backupsDynamic.importDetail', {size: fmtBytes(packageData.length), world: packageData.active_world || I18N.t('backupsDynamic.notSpecified'), packageVersion: packageData.game_version || I18N.t('backupsDynamic.unknown'), id: server.id, version: server.version, port: server.port}), phrase: I18N.t('core.phraseImport', {name: server.id}), confirm: I18N.t('backupsDynamic.importConfirm'), danger: true});
		if (!confirmation || packageData !== inspectedPackage) return;
		try {
			await busy(event.submitter, () => api('/api/provision/import', {method: 'POST', body: {plan: packageData.plan, server, confirmation: confirmation.phrase, accept_upgrade: $('#package-upgrade').checked}}));
			inspectedPackage = null;
			$('#package-import-form').classList.add('hidden');
			$('#package-upload-form').reset();
			$('#package-inspection').className = 'notice ok';
			$('#package-inspection').textContent = I18N.t('backupsDynamic.importStarted', {name: server.name});
			VS.selectPage('overview');
		} catch (error) { fail(error); }
	});

	// ---------- раздел ----------

	VS.pages.backups = {
		enter(tab) { loadBackups().catch(fail); this.tab(tab); },
		tab(view) { if (view === 'history' && VS.can('admin')) loadHistory().catch(fail); },
	};
})();
