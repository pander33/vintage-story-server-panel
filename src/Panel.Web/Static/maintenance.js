'use strict';

// Обслуживание: расписания и история, наблюдатель готовности, обновления модов и игры.

(() => {
	const {$, $$, esc, fmtDateTime, fmtBytes, api, toast, fail, busy, dialog} = VS;

	const RUNS = {running: I18N.t('maintenanceDynamic.m01'), success: I18N.t('maintenanceDynamic.m02'), failed: I18N.t('maintenanceDynamic.m03'), cancelled: I18N.t('maintenanceDynamic.m04'), skipped: I18N.t('maintenanceDynamic.m05'), interrupted: I18N.t('maintenanceDynamic.m06')};
	const RUN_LEVEL = {running: 'warn', success: 'ok', failed: 'error', cancelled: 'leave', skipped: 'leave', interrupted: 'error'};
	const DAYS = [I18N.t('maintenanceDynamic.m07'), I18N.t('maintenanceDynamic.m08'), I18N.t('maintenanceDynamic.m09'), I18N.t('maintenanceDynamic.m10'), I18N.t('maintenanceDynamic.m11'), I18N.t('maintenanceDynamic.m12'), I18N.t('maintenanceDynamic.m13')];
	let modUpdates = [];

	// ---------- расписание ----------

	function describe(job) {
		if (job.schedule_type === 'once') return `${I18N.t('maintenanceDynamic.m14')}${fmtDateTime(job.run_at)}`;
		if (job.schedule_type === 'daily') return `${I18N.t('maintenanceDynamic.m15')}${job.time}`;
		return `${(job.weekdays || []).map(day => DAYS[day]).join(', ')} · ${job.time}`;
	}

	function renderAutomation(data, hydrate) {
		const running = data.running;
		const box = $('#maintenance-running');
		box.classList.toggle('hidden', !running);
		box.textContent = running ? `${I18N.t('maintenanceDynamic.m16')}${running.name}${I18N.t('maintenanceDynamic.m17')}${fmtDateTime(running.started_at)}${I18N.t('maintenanceDynamic.m18')}` : '';
		const admin = VS.can('admin') ? '' : 'disabled';
		$('#schedule-list').innerHTML = (data.jobs || []).map(job => {
			const plan = [job.backup ? I18N.t('maintenanceDynamic.m19') : '', job.restart ? I18N.t('maintenanceDynamic.m20') : '', job.warning_minutes?.length ? `${I18N.t('maintenanceDynamic.m21')}${job.warning_minutes.join('/')}${I18N.t('maintenanceDynamic.m22')}` : ''].filter(Boolean).join(' · ');
			return `<div><span class="item-symbol">◷</span><span class="grow"><b>${esc(job.name)}</b><small>${esc(describe(job))}${plan ? ` · ${esc(plan)}` : ''}${I18N.t('maintenanceDynamic.m23')}${esc(job.next_run ? fmtDateTime(job.next_run) : '—')}${I18N.t('maintenanceDynamic.m24')}${esc(RUNS[job.last_result] || I18N.t('maintenanceDynamic.m25'))}${job.last_run_at ? ` (${esc(fmtDateTime(job.last_run_at))})` : ''}</small></span>
				<em class="${job.enabled ? 'ok' : 'off'}">${job.enabled ? I18N.t('maintenanceDynamic.m26') : I18N.t('maintenanceDynamic.m27')}</em>
				<span class="row-actions"><button class="button small" type="button" data-job="run" data-id="${esc(job.id)}" ${admin}${I18N.t('maintenanceDynamic.m28')}${esc(job.id)}" data-enabled="${job.enabled ? '0' : '1'}" ${admin}>${job.enabled ? I18N.t('maintenanceDynamic.m29') : I18N.t('maintenanceDynamic.m30')}</button><button class="button small danger" type="button" data-job="delete" data-id="${esc(job.id)}" ${admin}${I18N.t('maintenanceDynamic.m31')}`;
		}).join('') || I18N.t('maintenanceDynamic.m32');
		$('#automation-runs').innerHTML = (data.runs || []).map(run => `<div><time>${esc(fmtDateTime(run.started_at))}</time><span class="event-kind ${esc(RUN_LEVEL[run.status] || 'info')}">${esc(RUNS[run.status] || run.status)}</span><span><strong>${esc(run.name)}</strong> ${esc(run.detail || '')}</span></div>`).join('') || I18N.t('maintenanceDynamic.m33');
		renderAnnouncements(data.announcements || []);
		renderWatchdog(data, hydrate);
	}

	// ---------- объявления по расписанию ----------

	const ANNOUNCEMENT_RESULTS = {sent: 'maintenance.announcementSent', skipped: 'maintenance.announcementSkipped', failed: 'maintenance.announcementFailed'};

	function renderAnnouncements(items) {
		const admin = VS.can('admin') ? '' : 'disabled';
		$('#announcement-list').innerHTML = items.map(item => {
			const result = item.last_result ? I18N.t(ANNOUNCEMENT_RESULTS[item.last_result] || 'maintenance.announcementFailed') : I18N.t('maintenance.announcementNever');
			const plan = [
				I18N.t('maintenance.announcementEvery', {minutes: item.interval_minutes}),
				item.players_only ? I18N.t('maintenance.announcementWithPlayers') : '',
				`${I18N.t('maintenance.announcementNext')} ${item.next_run ? fmtDateTime(item.next_run) : '—'}`,
				`${I18N.t('maintenance.announcementLast')} ${result}${item.last_sent_at ? ` (${fmtDateTime(item.last_sent_at)})` : ''}`,
			].filter(Boolean).join(' · ');
			return `<div><span class="item-symbol">✉</span><span class="grow"><b>${esc(item.text)}</b><small>${esc(plan)}</small></span>
				<em class="${item.enabled ? 'ok' : 'off'}">${item.enabled ? I18N.t('maintenanceDynamic.m26') : I18N.t('maintenanceDynamic.m27')}</em>
				<span class="row-actions"><button class="button small" type="button" data-announcement="toggle" data-id="${esc(item.id)}" data-enabled="${item.enabled ? '0' : '1'}" ${admin}>${item.enabled ? I18N.t('maintenanceDynamic.m29') : I18N.t('maintenanceDynamic.m30')}</button><button class="button small danger" type="button" data-announcement="delete" data-id="${esc(item.id)}" ${admin}>${esc(I18N.t('common.delete'))}</button></span></div>`;
		}).join('') || `<div class="empty">${esc(I18N.t('maintenance.announcementsEmpty'))}</div>`;
	}

	$('#announcement-list').addEventListener('click', async event => {
		const button = event.target.closest('[data-announcement]');
		if (!button) return;
		const action = button.dataset.announcement, id = encodeURIComponent(button.dataset.id);
		if (action === 'delete' && !await VS.confirmAction(I18N.t('maintenance.announcementDeleteTitle'), I18N.t('maintenance.announcementDeleteText'), I18N.t('common.delete'), true)) return;
		try {
			await busy(button, () => action === 'delete' ? api(`/api/automation/announcements/${id}`, {method: 'DELETE'})
				: api(`/api/automation/announcements/${id}/toggle`, {method: 'POST', body: {enabled: button.dataset.enabled === '1'}}));
			toast(I18N.t('maintenance.announcementChanged'), 'ok');
			await loadAutomation(false);
		} catch (error) { fail(error); }
	});

	$('#announcement-form').addEventListener('submit', async event => {
		event.preventDefault();
		const body = {text: $('#announcement-text').value.trim(), enabled: true, interval_minutes: Number($('#announcement-interval').value), players_only: $('#announcement-players-only').checked};
		try {
			await busy(event.submitter, () => api('/api/automation/announcements', {method: 'POST', body}));
			$('#announcement-text').value = '';
			toast(I18N.t('maintenance.announcementAdded'), 'ok');
			await loadAutomation(false);
		} catch (error) { fail(error); }
	});

	function renderWatchdog(data, hydrate) {
		const health = data.health || {}, watchdog = data.watchdog || {};
		const stateName = health.state || 'unknown';
		const level = VS.healthLevel(stateName);
		$('#watchdog-health').innerHTML = `<div class="apply-state ${level === 'ok' ? 'applied' : (level === 'warn' ? 'restart_required' : 'unknown')}"><div><strong>${esc(VS.healthTitles[stateName] || stateName)}</strong><p>${esc(health.detail || I18N.t('maintenanceDynamic.m34'))}${I18N.t('maintenanceDynamic.m35')}${esc(health.checked_at ? fmtDateTime(health.checked_at) : '—')}${I18N.t('maintenanceDynamic.m36')}${esc(health.failures ?? 0)} / ${esc(watchdog.failure_threshold ?? '—')}${I18N.t('maintenanceDynamic.m37')}${watchdog.auto_restart ? I18N.t('maintenanceDynamic.m38') : I18N.t('maintenanceDynamic.m39')}</b></span></div>
			${health.recovery_blocked ? `<div class="notice warn">${esc(health.recovery_blocked)}</div>` : ''}`;
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
		if (action === 'delete' && !await VS.confirmAction(I18N.t('maintenanceDynamic.m40'), I18N.t('maintenanceDynamic.m41'), I18N.t('maintenanceDynamic.m42'), true)) return;
		if (action === 'run' && !await VS.confirmAction(I18N.t('maintenanceDynamic.m43'), I18N.t('maintenanceDynamic.m44'), I18N.t('maintenanceDynamic.m45'))) return;
		try {
			await busy(button, () => action === 'delete' ? api(`/api/automation/schedules/${encodeURIComponent(id)}`, {method: 'DELETE'})
				: action === 'toggle' ? api(`/api/automation/schedules/${encodeURIComponent(id)}/toggle`, {method: 'POST', body: {enabled: button.dataset.enabled === '1'}})
					: api(`/api/automation/schedules/${encodeURIComponent(id)}/run`, {method: 'POST'}));
			toast(action === 'run' ? I18N.t('maintenanceDynamic.m46') : I18N.t('maintenanceDynamic.m47'), 'ok');
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
		try { await busy(event.submitter, () => api('/api/automation/schedules', {method: 'POST', body})); toast(I18N.t('maintenanceDynamic.m48'), 'ok'); await loadAutomation(false); }
		catch (error) { fail(error); }
	});
	$('#automation-refresh').addEventListener('click', event => busy(event.currentTarget, () => loadAutomation(false)).catch(fail));

	// ---------- наблюдатель ----------

	$('#watchdog-check').addEventListener('click', async event => {
		try { await busy(event.currentTarget, () => api('/api/automation/watchdog/check', {method: 'POST'})); await loadAutomation(false); toast(I18N.t('maintenanceDynamic.m49'), 'ok'); }
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
		if (body.auto_restart && !await VS.confirmAction(I18N.t('maintenanceDynamic.m50'), I18N.t('maintenanceDynamic.m51'), I18N.t('maintenanceDynamic.m52'))) return;
		try { await busy(event.submitter, () => api('/api/automation/watchdog', {method: 'PUT', body})); toast(I18N.t('maintenanceDynamic.m53'), 'ok'); await loadAutomation(); }
		catch (error) { fail(error); }
	});

	// ---------- обновления ----------

	async function loadUpdates() {
		const data = await api('/api/updates');
		const current = data.current;
		const box = $('#update-running');
		box.classList.toggle('hidden', !current);
		box.textContent = current ? `${I18N.t('maintenanceDynamic.m54')}${current.title} · ${current.status}${current.phase ? `${I18N.t('maintenanceDynamic.m55')}${current.phase}` : ''}` : '';
		$('#update-history').innerHTML = (data.history || []).map(item => `<div><time>${esc(fmtDateTime(item.started_at))}</time><span class="event-kind ${esc(RUN_LEVEL[item.status] || 'info')}">${esc(RUNS[item.status] || item.status)}</span><span><strong>${esc(item.title)}</strong> ${esc(item.detail || '')}</span></div>`).join('') || I18N.t('maintenanceDynamic.m56');
		const admin = VS.can('admin') ? '' : 'disabled';
		$('#game-history').innerHTML = (data.game_history || []).map(item => `<div><span class="grow"><b class="mono">${esc(item.name)}</b><small>${esc(fmtDateTime(item.modified))} · ${esc(fmtBytes(item.size))}</small></span><button class="button small" type="button" data-game-rollback="${esc(item.name)}" ${admin}${I18N.t('maintenanceDynamic.m57')}`).join('') || I18N.t('maintenanceDynamic.m58');
		if (current) VS.every('updates', 5000, () => loadUpdates(), {page: 'maintenance', now: false});
		else VS.stopTimer('updates');
	}

	$('#batch-plan').addEventListener('click', async event => {
		try {
			const data = await busy(event.currentTarget, () => api('/api/mods/updates'));
			// Предварительный релиз предлагается рядом со стабильным, но не отмечается сам.
			modUpdates = VS.modUpdates(data.updates || []);
			$('#batch-list').innerHTML = modUpdates.map(({item, release}, index) => `<div><label class="check-row grow"><input type="checkbox" data-update-index="${index}" data-update-mod="${esc(item.modid)}" ${release.prerelease ? '' : 'checked'}><span><b>${esc(item.modid)}</b>${release.prerelease ? I18N.t('maintenanceDynamic.m59') : ''}<small>${esc(item.installed || '—')} → ${esc(release.version)}${release.compatibility === 'same_minor' ? `${I18N.t('maintenanceDynamic.m60')}${esc(release.compatible_tag)}` : ''}</small></span></label></div>`).join('') || I18N.t('maintenanceDynamic.m61');
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
		const result = await dialog({eyebrow: I18N.t('maintenanceDynamic.m62'), title: `${I18N.t('maintenanceDynamic.m63')}${items.length}?`, text: I18N.t('maintenanceDynamic.m64'), phrase: I18N.t('core.phraseUpdateMods'), confirm: I18N.t('maintenanceDynamic.m66')});
		if (!result) return;
		try { await busy(event.currentTarget, () => api('/api/updates/mods', {method: 'POST', body: {items, readiness_timeout: 900, confirmation: result.phrase}})); toast(I18N.t('maintenanceDynamic.m67'), 'ok'); await loadUpdates(); }
		catch (error) { fail(error); }
	});

	async function planGame() {
		const version = $('#game-version').value.trim();
		if (!version) { toast(I18N.t('maintenanceDynamic.m68'), 'error'); return null; }
		const data = await api(`/api/updates/game/plan?version=${encodeURIComponent(version)}`);
		const blockers = (data.blockers || []).map(item => item.modid), unknown = (data.warnings || []).map(item => item.modid);
		const box = $('#game-plan-result');
		box.className = `notice ${data.allowed ? (unknown.length || data.branch_change ? 'warn' : 'ok') : 'error'}`;
		box.innerHTML = `<strong>${data.allowed ? I18N.t('maintenanceDynamic.m69') : I18N.t('maintenanceDynamic.m70')}${I18N.t('maintenanceDynamic.m71')}${(data.mods || []).filter(item => item.status === 'compatible').length}${I18N.t('maintenanceDynamic.m72')}${blockers.length}${I18N.t('maintenanceDynamic.m73')}${unknown.length}.${data.branch_change ? I18N.t('maintenanceDynamic.m74') : ''}</p>${blockers.length ? `${I18N.t('maintenanceDynamic.m75')}${esc(blockers.join(', '))}</p>` : ''}${unknown.length ? `${I18N.t('maintenanceDynamic.m76')}${esc(unknown.join(', '))}</p>` : ''}`;
		return data;
	}
	$('#game-plan').addEventListener('click', event => busy(event.currentTarget, planGame).catch(fail));

	$('#game-file').addEventListener('change', async event => {
		const file = event.target.files[0], version = $('#game-version').value.trim();
		if (!file) return;
		try {
			if (!version) { toast(I18N.t('maintenanceDynamic.m77'), 'error'); return; }
			const plan = await planGame();
			if (!plan?.allowed) { toast(I18N.t('maintenanceDynamic.m78'), 'error'); return; }
			if (((plan.warnings || []).length || plan.branch_change) && !$('#game-risk').checked) { toast(I18N.t('maintenanceDynamic.m79'), 'error'); return; }
			const result = await dialog({eyebrow: I18N.t('maintenanceDynamic.m80'), title: `${I18N.t('maintenanceDynamic.m81')}${version}?`, text: `${I18N.t('maintenanceDynamic.m82')}${file.name} (${fmtBytes(file.size)}${I18N.t('maintenanceDynamic.m83')}`, phrase: I18N.t('core.phraseUpdateServer'), confirm: I18N.t('maintenanceDynamic.m85'), danger: true});
			if (!result) return;
			const body = new FormData();
			body.append('file', file);
			body.append('confirmation', result.phrase);
			body.append('target_version', version);
			body.append('allow_unverified', $('#game-risk').checked ? 'true' : 'false');
			toast(I18N.t('maintenanceDynamic.m86'));
			await api('/api/updates/game', {method: 'POST', body});
			toast(I18N.t('maintenanceDynamic.m87'), 'ok');
			await loadUpdates();
		} catch (error) { fail(error); }
		finally { event.target.value = ''; }
	});

	$('#game-history').addEventListener('click', async event => {
		const button = event.target.closest('[data-game-rollback]');
		if (!button) return;
		const name = button.dataset.gameRollback;
		const result = await dialog({eyebrow: I18N.t('maintenanceDynamic.m88'), title: I18N.t('maintenanceDynamic.m89'), text: I18N.t('maintenanceDynamic.m90'), phrase: I18N.t('core.phraseRollBack', {name}), confirm: I18N.t('maintenanceDynamic.m92'), danger: true});
		if (!result) return;
		try { await busy(button, () => api('/api/updates/game/rollback', {method: 'POST', body: {name, confirmation: result.phrase}})); toast(I18N.t('maintenanceDynamic.m93'), 'ok'); await loadUpdates(); }
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
