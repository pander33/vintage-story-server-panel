'use strict';

// Резервные копии: список, создание, восстановление, извлечение мира, очистка и история.

(() => {
	const {$, esc, fmtBytes, fmtDateTime, fmtDay, fmtShortTime, api, toast, fail, busy, dialog} = VS;

	const STORES = {'mod-history': 'Прежние версии модов', 'config-backup': 'Копии serverconfig.json', 'mod-config-backup': 'Копии конфигураций модов'};
	let backups = [];
	let history = null;

	// ---------- список ----------

	async function loadBackups() {
		const [data, metrics] = await Promise.all([api('/api/backups'), api('/api/metrics?hours=1').catch(() => ({}))]);
		backups = (data.backups || []).slice().sort((a, b) => b.modified - a.modified);
		const last = backups[0];
		$('#backup-last').textContent = last ? `${fmtDay(last.modified)}, ${fmtShortTime(last.modified)}` : 'Копий нет';
		$('#backup-last-detail').textContent = last ? `${fmtBytes(last.size)} · ${last.name}` : 'Создайте первую копию';
		$('#backup-count').textContent = `${backups.length} ${backups.length === 1 ? "архив" : (backups.length > 1 && backups.length < 5 ? "архива" : "архивов")}`;
		$('#backup-total').textContent = `Занято ${fmtBytes(backups.reduce((sum, item) => sum + (Number(item.size) || 0), 0))}`;
		const current = metrics.current || {};
		$('#backup-free').textContent = current.disk_free ? fmtBytes(current.disk_free) : '—';
		$('#backup-free-detail').textContent = current.disk_warning ? 'Мало места: копия может не поместиться' : (current.disk_total ? `из ${fmtBytes(current.disk_total)}` : '—');
		const operator = VS.can('operator'), admin = VS.can('admin'), onlyOne = backups.length <= 1;
		$('#backups-list').innerHTML = backups.map((item, index) => `<div><span class="item-symbol">▣</span><span class="grow"><b class="mono">${esc(item.name)}</b><small>${esc(fmtDateTime(item.modified))} · ${esc(fmtBytes(item.size))}${index === 0 ? " · последняя" : ""}</small></span>
			<span class="row-actions"><button class="button small" type="button" data-backup-inspect="${esc(item.name)}">Содержимое</button><button class="button small" type="button" data-backup-restore="${esc(item.name)}" ${admin ? "" : "disabled"}>Восстановить</button><button class="button small danger" type="button" data-backup-delete="${esc(item.name)}" ${admin && !onlyOne ? "" : "disabled"} title="${onlyOne ? "Последнюю копию удалить нельзя" : ""}">Удалить</button></span></div>`).join('') || '<div class="empty">Резервных копий пока нет.</div>';
		$('#extract-backup').innerHTML = backups.map(item => `<option value="${esc(item.name)}">${esc(item.name)} · ${esc(fmtBytes(item.size))}</option>`).join('') || '<option value="">Нет копий</option>';
		if (!operator) $('#backup-create').disabled = true;
	}

	$('#backups-list').addEventListener('click', async event => {
		const inspect = event.target.closest('[data-backup-inspect]'), restore = event.target.closest('[data-backup-restore]'), remove = event.target.closest('[data-backup-delete]');
		try {
			if (inspect) {
				const data = await busy(inspect, () => api(`/api/backups/${encodeURIComponent(inspect.dataset.backupInspect)}/inspect`));
				await dialog({eyebrow: 'СОДЕРЖИМОЕ', title: inspect.dataset.backupInspect, text: `Архив проверен: ${data.files} файлов.\n\nМиры в копии:\n${(data.worlds || []).map(world => `• ${world.name} — ${fmtBytes(world.size)}`).join("\n") || "не найдены"}`, confirm: 'Закрыть', cancel: 'Назад'});
			} else if (restore) {
				const name = restore.dataset.backupRestore;
				const result = await dialog({eyebrow: 'ВОССТАНОВЛЕНИЕ', title: 'Восстановить резервную копию?', text: 'Сервер будет остановлен, текущие данные заменены содержимым копии. Перед заменой панель создаст страховочную копию.', phrase: `ВОССТАНОВИТЬ ${name}`, confirm: 'Восстановить', danger: true});
				if (!result) return;
				await busy(restore, () => api('/api/backups/restore', {method: 'POST', body: {name, confirmation: result.phrase}}));
				toast('Копия восстановлена', 'ok');
				await loadBackups();
				VS.refreshStatus();
			} else if (remove) {
				const name = remove.dataset.backupDelete;
				const result = await dialog({eyebrow: 'БЕЗВОЗВРАТНО', title: 'Удалить резервную копию?', text: 'У копии нет корзины: архив будет стёрт, восстановить его будет невозможно.', phrase: `УДАЛИТЬ ${name}`, confirm: 'Удалить', danger: true});
				if (!result) return;
				await busy(remove, () => api('/api/backups/delete', {method: 'POST', body: {name, confirmation: result.phrase}}));
				toast('Копия удалена', 'ok');
				await loadBackups();
			}
		} catch (error) { fail(error); }
	});

	$('#backup-create').addEventListener('click', async event => {
		if (!await VS.confirmAction('Создать резервную копию?', 'На время архивации сервер корректно остановится и затем запустится снова: так копия мира будет целостной.', 'Создать копию')) return;
				try { const data = await busy(event.currentTarget, () => api('/api/backups', {method: 'POST'})); toast(`Создана ${data.name}`, 'ok'); await loadBackups(); VS.refreshStatus(); }
		catch (error) { fail(error); }
	});
	$('#backups-refresh').addEventListener('click', event => busy(event.currentTarget, loadBackups).catch(fail));

	// ---------- извлечение мира ----------

	$('#extract-inspect').addEventListener('click', async event => {
		const name = $('#extract-backup').value;
		if (!name) return;
		try {
			const data = await busy(event.currentTarget, () => api(`/api/backups/${encodeURIComponent(name)}/inspect`));
			$('#extract-world').innerHTML = (data.worlds || []).map(world => `<option value="${esc(world.name)}">${esc(world.name)} · ${esc(fmtBytes(world.size))}</option>`).join('') || '<option value="">В копии миров нет</option>';
			toast(`Архив проверен: ${data.files} файлов`);
		} catch (error) { fail(error); }
	});
	$('#extract-backup').addEventListener('change', () => { $('#extract-world').innerHTML = '<option value="">Сначала покажите миры копии</option>'; });

	$('#extract-form').addEventListener('submit', async event => {
		event.preventDefault();
		const backup = $('#extract-backup').value, world = $('#extract-world').value, target = $('#extract-target').value.trim();
		if (!world) { toast('Сначала выберите мир из копии', 'error'); return; }
		const result = await dialog({eyebrow: 'ИЗВЛЕЧЕНИЕ', title: `Извлечь ${world}?`, text: `Мир появится в Saves как ${target}. Активный мир не меняется.`, phrase: `ИЗВЛЕЧЬ ${target}`, confirm: 'Извлечь'});
		if (!result) return;
		try {
			await busy(event.submitter, () => api('/api/backups/restore-world', {method: 'POST', body: {backup, world, target_name: target, confirmation: result.phrase}}));
			const box = $('#extract-result');
			box.className = 'notice ok';
			box.textContent = `Мир извлечён как ${target}. Его можно сделать активным в разделе «Миры и моды».`;
		} catch (error) { fail(error); }
	});

	// ---------- очистка ----------

	async function retention(apply) {
		const body = {keep_count: Number($('#retention-count').value), keep_days: Number($('#retention-days').value), minimum_free_gb: Number($('#retention-free').value), apply, confirmation: ''};
		if (apply) {
			const result = await dialog({eyebrow: 'БЕЗВОЗВРАТНО', title: 'Удалить показанные копии?', text: 'Удаление необратимо. Панель сохранит минимум две проверенные копии.', phrase: 'ОЧИСТИТЬ СТАРЫЕ КОПИИ', confirm: 'Удалить', danger: true});
			if (!result) return;
			body.confirmation = result.phrase;
		}
		const data = await api('/api/backups/retention', {method: 'POST', body});
		const box = $('#retention-result');
		box.classList.remove('hidden');
		if (apply) {
			box.className = 'notice ok';
			box.textContent = `Удалено копий: ${(data.removed || []).length}. Свободно теперь ${fmtBytes(data.free_after || 0)}.`;
			await loadBackups();
		} else {
			const candidates = data.candidates || [];
			box.className = `notice ${candidates.length ? "warn" : "ok"}`;
			box.innerHTML = candidates.length
				? `<strong>Будет удалено: ${candidates.length}, освободится ${esc(fmtBytes(data.bytes || 0))}.</strong> Цель свободного места ${data.target_met ? "будет достигнута" : "не достигается без нарушения минимального срока или числа копий"}.<p class="mono">${candidates.map(esc).join("<br>")}</p>`
				: 'Под условия очистки ничего не попадает.';
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
		$('#history-summary').innerHTML = `<span>Хранилище <b>${esc(STORES[store])}</b></span><span>Записей <b>${items.length}</b></span><span>Занято <b>${esc(fmtBytes((history.bytes || {})[store] || 0))}</b></span>`;
		$('#history-list').innerHTML = items.map(item => `<div><span class="grow"><b>${esc(item.name)}</b><small>${esc(item.group)} · ${esc(fmtDateTime(item.modified))} · ${esc(fmtBytes(item.size))}</small></span><button class="button small danger" type="button" data-history-key="${esc(item.key)}">Удалить</button></div>`).join('') || '<div class="empty">Записей нет — чистить нечего.</div>';
	}

	$('#history-list').addEventListener('click', async event => {
		const button = event.target.closest('[data-history-key]');
		if (!button) return;
		if (!await VS.confirmAction('Удалить запись безвозвратно?', button.dataset.historyKey, 'Удалить', true)) return;
		try { await busy(button, () => api('/api/history/delete', {method: 'POST', body: {store: $('#history-store').value, key: button.dataset.historyKey}})); toast('Запись удалена', 'ok'); await loadHistory(); }
		catch (error) { fail(error); }
	});
	$('#history-prune').addEventListener('click', async event => {
		const keep = Number($('#history-keep').value);
		const result = await dialog({eyebrow: 'ОЧИСТКА ИСТОРИИ', title: 'Очистить историю?', text: `Останутся ${keep} новейших записей на каждый файл, остальные будут стёрты безвозвратно.`, phrase: 'ОЧИСТИТЬ ИСТОРИЮ', confirm: 'Очистить', danger: true});
		if (!result) return;
		try { const data = await busy(event.currentTarget, () => api('/api/history/prune', {method: 'POST', body: {store: $('#history-store').value, keep, confirmation: result.phrase}})); toast(`Удалено записей: ${(data.removed || []).length}`, 'ok'); await loadHistory(); }
		catch (error) { fail(error); }
	});
	$('#history-store').addEventListener('change', renderHistory);
	$('#history-refresh').addEventListener('click', event => busy(event.currentTarget, loadHistory).catch(fail));

	// ---------- перенос сервера ----------

	let inspectedPackage = null;
	$('#package-export').addEventListener('click', async event => {
		const server = VS.currentServer();
		if (!VS.can('admin') || !server) { toast('Выберите сервер для экспорта', 'error'); return; }
		if (!await VS.confirmAction('Экспортировать сервер?', 'Сервер будет остановлен на время создания согласованного пакета. Сохраните скачанный архив в безопасном месте.', 'Экспортировать')) return;
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
			toast('Пакет сервера скачан', 'ok');
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
		notice.textContent = `Загружается и проверяется ${file.name} (${fmtBytes(file.size)})…`;
		try {
			const data = await busy(event.submitter, () => api('/api/provision/transfer', {method: 'POST', body: file}));
			if (!data.plan || !data.sha256 || !Number.isSafeInteger(data.length)) throw new Error('Панель не вернула данные проверенного пакета');
			inspectedPackage = data;
			notice.className = 'notice ok';
			notice.textContent = `Пакет проверен: ${file.name}, ${fmtBytes(data.length)}. Активный мир: ${data.active_world || 'не указан'}. Версия игры в пакете: ${data.game_version || 'неизвестна'}. SHA-256: ${data.sha256}.`;
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
		const confirmation = await dialog({eyebrow: 'ИМПОРТ СЕРВЕРА', title: `Создать сервер «${server.name}»?`, text: `Пакет: ${fmtBytes(packageData.length)}. Активный мир: ${packageData.active_world || 'не указан'}. Версия игры в пакете: ${packageData.game_version || 'неизвестна'}. Новый идентификатор: ${server.id}; версия игры: ${server.version}; порт: ${server.port}. Импорт создаст отдельный сервер из проверенного архива.`, phrase: `ИМПОРТИРОВАТЬ ${server.id}`, confirm: 'Импортировать', danger: true});
		if (!confirmation || packageData !== inspectedPackage) return;
		try {
			await busy(event.submitter, () => api('/api/provision/import', {method: 'POST', body: {plan: packageData.plan, server, confirmation: confirmation.phrase, accept_upgrade: $('#package-upgrade').checked}}));
			inspectedPackage = null;
			$('#package-import-form').classList.add('hidden');
			$('#package-upload-form').reset();
			$('#package-inspection').className = 'notice ok';
			$('#package-inspection').textContent = `Импорт сервера «${server.name}» запущен. Ход операции показан выше.`;
			VS.selectPage('overview');
		} catch (error) { fail(error); }
	});

	// ---------- раздел ----------

	VS.pages.backups = {
		enter(tab) { loadBackups().catch(fail); this.tab(tab); },
		tab(view) { if (view === 'history' && VS.can('admin')) loadHistory().catch(fail); },
	};
})();
