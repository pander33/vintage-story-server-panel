'use strict';

// Обслуживание: расписания и история, наблюдатель готовности, обновления модов и игры.

(() => {
	const {$, $$, esc, fmtDateTime, fmtBytes, api, toast, fail, busy, dialog} = VS;

	const RUNS = {running: 'Выполняется', success: 'Успешно', failed: 'Ошибка', cancelled: 'Отменено', skipped: 'Пропущено', interrupted: 'Прервано'};
	const RUN_LEVEL = {running: 'warn', success: 'ok', failed: 'error', cancelled: 'leave', skipped: 'leave', interrupted: 'error'};
	const DAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
	let modUpdates = [];

	// ---------- расписание ----------

	function describe(job) {
		if (job.schedule_type === 'once') return `Один раз · ${fmtDateTime(job.run_at)}`;
		if (job.schedule_type === 'daily') return `Каждый день · ${job.time}`;
		return `${(job.weekdays || []).map(day => DAYS[day]).join(", ")} · ${job.time}`;
	}

	function renderAutomation(data, hydrate) {
		const running = data.running;
		const box = $('#maintenance-running');
		box.classList.toggle('hidden', !running);
		box.textContent = running ? `Сейчас выполняется: ${running.name}. Начато ${fmtDateTime(running.started_at)}. Изменения сервера до окончания отвечают «занято».` : '';
		const admin = VS.can('admin') ? '' : 'disabled';
		$('#schedule-list').innerHTML = (data.jobs || []).map(job => {
			const plan = [job.backup ? 'копия' : '', job.restart ? 'перезапуск' : '', job.warning_minutes?.length ? `предупреждения ${job.warning_minutes.join("/")} мин` : ''].filter(Boolean).join(' · ');
			return `<div><span class="item-symbol">◷</span><span class="grow"><b>${esc(job.name)}</b><small>${esc(describe(job))}${plan ? ` · ${esc(plan)}` : ""}</small><small>Следующий: ${esc(job.next_run ? fmtDateTime(job.next_run) : "—")} · последний: ${esc(RUNS[job.last_result] || "ещё не запускалось")}${job.last_run_at ? ` (${esc(fmtDateTime(job.last_run_at))})` : ""}</small></span>
				<em class="${job.enabled ? "ok" : "off"}">${job.enabled ? "ВКЛЮЧЕНО" : "ВЫКЛЮЧЕНО"}</em>
				<span class="row-actions"><button class="button small" type="button" data-job="run" data-id="${esc(job.id)}" ${admin}>Запустить</button><button class="button small" type="button" data-job="toggle" data-id="${esc(job.id)}" data-enabled="${job.enabled ? "0" : "1"}" ${admin}>${job.enabled ? "Отключить" : "Включить"}</button><button class="button small danger" type="button" data-job="delete" data-id="${esc(job.id)}" ${admin}>Удалить</button></span></div>`;
		}).join('') || '<div class="empty">Расписаний пока нет.</div>';
		$('#automation-runs').innerHTML = (data.runs || []).map(run => `<div><time>${esc(fmtDateTime(run.started_at))}</time><span class="event-kind ${esc(RUN_LEVEL[run.status] || "info")}">${esc(RUNS[run.status] || run.status)}</span><span><strong>${esc(run.name)}</strong> ${esc(run.detail || "")}</span></div>`).join('') || '<div class="empty">Запусков пока нет.</div>';
		renderWatchdog(data, hydrate);
	}

	function renderWatchdog(data, hydrate) {
		const health = data.health || {}, watchdog = data.watchdog || {};
		const stateName = health.state || 'unknown';
		const level = VS.healthLevel(stateName);
		$('#watchdog-health').innerHTML = `<div class="apply-state ${level === "ok" ? "applied" : (level === "warn" ? "restart_required" : "unknown")}"><div><strong>${esc(VS.healthTitles[stateName] || stateName)}</strong><p>${esc(health.detail || "Проверка ещё не выполнялась")}</p></div></div>
			<div class="metric-mini"><span>Проверено <b>${esc(health.checked_at ? fmtDateTime(health.checked_at) : "—")}</b></span><span>Неудач подряд <b>${esc(health.failures ?? 0)} / ${esc(watchdog.failure_threshold ?? "—")}</b></span><span>Автоперезапуск <b>${watchdog.auto_restart ? "включён" : "выключен"}</b></span></div>
			${health.recovery_blocked ? `<div class="notice warn">${esc(health.recovery_blocked)}</div>` : ""}`;
		if (!hydrate) return;
		$('#watchdog-enabled').checked = !!watchdog.enabled;
		$('#watchdog-auto-restart').checked = !!watchdog.auto_restart;
		$('#watchdog-startup-timeout').value = watchdog.startup_timeout ?? 300;
		$('#watchdog-stale-seconds').value = watchdog.stale_seconds ?? 45;
		$('#watchdog-failures').value = watchdog.failure_threshold ?? 3;
		$('#watchdog-cooldown').value = watchdog.cooldown_seconds ?? 900;
		$('#watchdog-max-restarts').value = watchdog.max_restarts_hour ?? 3;
	}

	async function loadAutomation(hydrate = true) { renderAutomation(await api('/api/automation'), hydrate); }

	$('#schedule-list').addEventListener('click', async event => {
		const button = event.target.closest('[data-job]');
		if (!button) return;
		const action = button.dataset.job, id = button.dataset.id;
		if (action === 'delete' && !await VS.confirmAction('Удалить расписание?', 'История запусков останется.', 'Удалить', true)) return;
		if (action === 'run' && !await VS.confirmAction('Запустить обслуживание сейчас?', 'Если заданы предупреждения, панель сначала выдержит весь обратный отсчёт, затем остановит сервер.', 'Запустить')) return;
		try {
			await busy(button, () => action === 'delete' ? api(`/api/automation/schedules/${encodeURIComponent(id)}`, {method: 'DELETE'})
				: action === 'toggle' ? api(`/api/automation/schedules/${encodeURIComponent(id)}/toggle`, {method: 'POST', body: {enabled: button.dataset.enabled === '1'}})
					: api(`/api/automation/schedules/${encodeURIComponent(id)}/run`, {method: 'POST'}));
			toast(action === 'run' ? 'Обслуживание запущено' : 'Расписание изменено', 'ok');
			await loadAutomation(false);
		} catch (error) { fail(error); }
	});

	function updateScheduleFields() {
		const type = $('#schedule-type').value;
		$('#schedule-time-field').classList.toggle('hidden', type === 'once');
		$('#schedule-once-field').classList.toggle('hidden', type !== 'once');
		$('#schedule-weekdays').classList.toggle('hidden', type !== 'weekly');
	}
	$('#schedule-type').addEventListener('change', updateScheduleFields);
	updateScheduleFields();

	$('#schedule-form').addEventListener('submit', async event => {
		event.preventDefault();
		const body = {
			name: $('#schedule-name').value.trim(), enabled: true, schedule_type: $('#schedule-type').value,
			time: $('#schedule-time').value, run_at: $('#schedule-run-at').value,
			weekdays: $$('#schedule-weekdays input:checked').map(input => Number(input.value)),
			warning_minutes: $('#schedule-warnings').value.split(',').map(value => value.trim()).filter(Boolean).map(Number),
			backup: $('#schedule-backup').checked, restart: $('#schedule-restart').checked,
			readiness_timeout: Number($('#schedule-ready-timeout').value),
		};
		try { await busy(event.submitter, () => api('/api/automation/schedules', {method: 'POST', body})); toast('Расписание добавлено', 'ok'); await loadAutomation(false); }
		catch (error) { fail(error); }
	});
	$('#automation-refresh').addEventListener('click', event => busy(event.currentTarget, () => loadAutomation(false)).catch(fail));

	// ---------- наблюдатель ----------

	$('#watchdog-check').addEventListener('click', async event => {
		try { await busy(event.currentTarget, () => api('/api/automation/watchdog/check', {method: 'POST'})); await loadAutomation(false); toast('Проверка завершена', 'ok'); }
		catch (error) { fail(error); }
	});

	$('#watchdog-form').addEventListener('submit', async event => {
		event.preventDefault();
		const body = {
			enabled: $('#watchdog-enabled').checked, auto_restart: $('#watchdog-auto-restart').checked,
			startup_timeout: Number($('#watchdog-startup-timeout').value), stale_seconds: Number($('#watchdog-stale-seconds').value),
			failure_threshold: Number($('#watchdog-failures').value), cooldown_seconds: Number($('#watchdog-cooldown').value),
			max_restarts_hour: Number($('#watchdog-max-restarts').value),
		};
		if (body.auto_restart && !await VS.confirmAction('Разрешить автоматический перезапуск?', 'Наблюдатель перезапустит сервер только после нескольких подтверждений зависания, с паузой и часовым пределом. Без телеметрии перезапуска не будет никогда.', 'Разрешить')) return;
		try { await busy(event.submitter, () => api('/api/automation/watchdog', {method: 'PUT', body})); toast('Правила наблюдения сохранены', 'ok'); await loadAutomation(); }
		catch (error) { fail(error); }
	});

	// ---------- обновления ----------

	async function loadUpdates() {
		const data = await api('/api/updates');
		const current = data.current;
		const box = $('#update-running');
		box.classList.toggle('hidden', !current);
		box.textContent = current ? `Идёт операция: ${current.title} · ${current.status}${current.phase ? ` · этап ${current.phase}` : ""}` : '';
		$('#update-history').innerHTML = (data.history || []).map(item => `<div><time>${esc(fmtDateTime(item.started_at))}</time><span class="event-kind ${esc(RUN_LEVEL[item.status] || "info")}">${esc(RUNS[item.status] || item.status)}</span><span><strong>${esc(item.title)}</strong> ${esc(item.detail || "")}</span></div>`).join('') || '<div class="empty">Обновлений ещё не выполнялось.</div>';
		const admin = VS.can('admin') ? '' : 'disabled';
		$('#game-history').innerHTML = (data.game_history || []).map(item => `<div><span class="grow"><b class="mono">${esc(item.name)}</b><small>${esc(fmtDateTime(item.modified))} · ${esc(fmtBytes(item.size))}</small></span><button class="button small" type="button" data-game-rollback="${esc(item.name)}" ${admin}>Откатить</button></div>`).join('') || '<div class="empty">Снимков программы пока нет.</div>';
		if (current) VS.every('updates', 5000, () => loadUpdates(), {page: 'maintenance', now: false});
		else VS.stopTimer('updates');
	}

	$('#batch-plan').addEventListener('click', async event => {
		try {
			const data = await busy(event.currentTarget, () => api('/api/mods/updates'));
			// Предварительный релиз предлагается рядом со стабильным, но не отмечается сам.
			modUpdates = VS.modUpdates(data.updates || []);
			$('#batch-list').innerHTML = modUpdates.map(({item, release}, index) => `<div><label class="check-row grow"><input type="checkbox" data-update-index="${index}" data-update-mod="${esc(item.modid)}" ${release.prerelease ? "" : "checked"}><span><b>${esc(item.modid)}</b>${release.prerelease ? ' <span class="badge warn">предварительный</span>' : ''}<small>${esc(item.installed || "—")} → ${esc(release.version)}${release.compatibility === "same_minor" ? ` · метка ${esc(release.compatible_tag)}` : ""}</small></span></label></div>`).join('') || '<div class="empty">Совместимых обновлений нет.</div>';
			$('#batch-apply').disabled = !modUpdates.length;
		} catch (error) { fail(error); }
	});

	// У мода отмечается одна версия: пакет заменяет каждый мод один раз.
	$('#batch-list').addEventListener('change', event => {
		const input = event.target.closest('[data-update-mod]');
		if (input?.checked) $$('[data-update-mod]').forEach(other => { if (other !== input && other.dataset.updateMod === input.dataset.updateMod) other.checked = false; });
	});

	$('#batch-apply').addEventListener('click', async event => {
		const items = $$('[data-update-index]:checked').map(input => { const release = modUpdates[Number(input.dataset.updateIndex)].release; return {identifier: release.identifier, release_id: Number(release.releaseid)}; });
		if (!items.length) return;
		const result = await dialog({eyebrow: 'ОБНОВЛЕНИЕ МОДОВ', title: `Обновить модов: ${items.length}?`, text: 'Панель скачает релизы, остановит сервер, создаст копию офлайн, заменит моды и дождётся готовности. При ошибке вернёт прежние версии.', phrase: 'ОБНОВИТЬ МОДЫ', confirm: 'Обновить'});
		if (!result) return;
		try { await busy(event.currentTarget, () => api('/api/updates/mods', {method: 'POST', body: {items, readiness_timeout: 900, confirmation: result.phrase}})); toast('Обновление модов запущено; следите за историей', 'ok'); await loadUpdates(); }
		catch (error) { fail(error); }
	});

	async function planGame() {
		const version = $('#game-version').value.trim();
		if (!version) { toast('Укажите версию пакета', 'error'); return null; }
		const data = await api(`/api/updates/game/plan?version=${encodeURIComponent(version)}`);
		const blockers = (data.blockers || []).map(item => item.modid), unknown = (data.warnings || []).map(item => item.modid);
		const box = $('#game-plan-result');
		box.className = `notice ${data.allowed ? (unknown.length || data.branch_change ? "warn" : "ok") : "error"}`;
		box.innerHTML = `<strong>${data.allowed ? "План проверен" : "Обновление заблокировано"}</strong><p>Совместимы: ${(data.mods || []).filter(item => item.status === "compatible").length}; несовместимы: ${blockers.length}; не проверены: ${unknown.length}.${data.branch_change ? " Меняется ветка игры." : ""}</p>${blockers.length ? `<p>Несовместимы: ${esc(blockers.join(", "))}</p>` : ""}${unknown.length ? `<p>Не проверены: ${esc(unknown.join(", "))}</p>` : ""}`;
		return data;
	}
	$('#game-plan').addEventListener('click', event => busy(event.currentTarget, planGame).catch(fail));

	$('#game-file').addEventListener('change', async event => {
		const file = event.target.files[0], version = $('#game-version').value.trim();
		if (!file) return;
		try {
			if (!version) { toast('Сначала укажите версию пакета', 'error'); return; }
			const plan = await planGame();
			if (!plan?.allowed) { toast('План обновления содержит несовместимые моды', 'error'); return; }
			if (((plan.warnings || []).length || plan.branch_change) && !$('#game-risk').checked) { toast('Подтвердите риск непроверенных модов или смены ветки', 'error'); return; }
			const result = await dialog({eyebrow: 'ОБНОВЛЕНИЕ СЕРВЕРА', title: `Установить ${version}?`, text: `Файл ${file.name} (${fmtBytes(file.size)}). Сервер будет остановлен, программа заменена, данные сохранены для отката.`, phrase: 'ОБНОВИТЬ СЕРВЕР', confirm: 'Установить', danger: true});
			if (!result) return;
			const body = new FormData();
			body.append('file', file);
			body.append('confirmation', result.phrase);
			body.append('target_version', version);
			body.append('allow_unverified', $('#game-risk').checked ? 'true' : 'false');
			toast('Загрузка пакета…');
			await api('/api/updates/game', {method: 'POST', body});
			toast('Обновление сервера запущено; следите за историей', 'ok');
			await loadUpdates();
		} catch (error) { fail(error); }
		finally { event.target.value = ''; }
	});

	$('#game-history').addEventListener('click', async event => {
		const button = event.target.closest('[data-game-rollback]');
		if (!button) return;
		const name = button.dataset.gameRollback;
		const result = await dialog({eyebrow: 'ОТКАТ ПРОГРАММЫ', title: 'Откатить программу сервера?', text: 'Сервер будет остановлен, программа и связанные данные вернутся к снимку.', phrase: `ОТКАТИТЬ ${name}`, confirm: 'Откатить', danger: true});
		if (!result) return;
		try { await busy(button, () => api('/api/updates/game/rollback', {method: 'POST', body: {name, confirmation: result.phrase}})); toast('Откат запущен', 'ok'); await loadUpdates(); }
		catch (error) { fail(error); }
	});

	// ---------- раздел ----------

	VS.pages.maintenance = {
		enter(tab) {
			loadAutomation().catch(fail);
			VS.every('automation', 10000, () => loadAutomation(false), {page: 'maintenance', now: false});
			this.tab(tab);
		},
		tab(view) { if (view === 'updates') loadUpdates().catch(fail); },
	};
})();
