'use strict';

// Сборка модов: опись модов сервера и файлы их настроек одним ZIP (findings §75). Выгрузка — ссылка
// /api/mods/pack в разделе «Моды»; здесь импорт: сервер разбирает сборку и отвечает планом, оператор
// отмечает, что применить. Настройки и включение модов пишет сервер, моды ставятся из ModDB через
// диалог зависимостей — по одному и с проверкой совместимости (INV-28).

(() => {
	const {$, esc, api, toast, fail, busy, modal, can, fmtDateTime} = VS;

	const STATUS = {
		missing: mod => I18N.t('modpack.statusMissing'),
		different: mod => I18N.t('modpack.statusDifferent', {installed: mod.installed || '—'}),
		same: () => I18N.t('modpack.statusSame'),
		ambiguous: mod => I18N.t('modpack.statusAmbiguous', {installed: mod.installed || '—'}),
	};

	const branch = version => String(version || '').split('.').slice(0, 2).join('.');

	function render(plan) {
		const installs = plan.mods.filter(mod => mod.status === 'missing' || mod.status === 'different');
		const toggles = plan.mods.filter(mod => mod.toggle);
		const extra = plan.extra.filter(mod => mod.enabled);
		const gameWarning = plan.game_version && plan.current_game_version && branch(plan.game_version) !== branch(plan.current_game_version);

		const mods = plan.mods.map(mod => {
			const badge = mod.status === 'same' ? 'ok' : mod.status === 'ambiguous' ? 'warn' : '';
			const toggle = mod.toggle ? `<small class="accent">${esc(I18N.t(mod.enabled ? 'modpack.willEnable' : 'modpack.willDisable'))}</small>` : '';
			return `<div><span class="grow"><b>${esc(mod.display_name || mod.modid)}</b> <span class="badge ${badge}">${esc(STATUS[mod.status](mod))}</span><small>${esc(mod.modid)} · ${esc(mod.version || '—')}${mod.enabled ? '' : ` · ${esc(I18N.t('modpack.disabledInPack'))}`}</small>${toggle}</span></div>`;
		}).join('') || `<div class="empty">${esc(I18N.t('modpack.noMods'))}</div>`;

		const configs = plan.configs.map(config => `<label class="dependency-row"><input type="checkbox" data-config="${esc(config.name)}" ${config.status === 'same' ? 'disabled' : 'checked'}>
			<span class="grow"><b>${esc(config.name)}</b><small>${esc(I18N.t(`modpack.config_${config.status}`))}</small></span></label>`).join('')
			|| `<div class="empty">${esc(I18N.t('modpack.noConfigs'))}</div>`;

		const skipped = plan.skipped_configs.length
			? `<p class="notice">${esc(I18N.t('modpack.skippedConfigs', {names: plan.skipped_configs.map(item => `${item.name} (${item.reason})`).join(', ')}))}</p>`
			: '';

		return {
			installs, toggles, extra,
			html: `<p class="notice ${gameWarning ? 'warn' : ''}">${esc(I18N.t('modpack.source', {server: plan.server || '—', date: fmtDateTime(plan.exported_at), game: plan.game_version || '—', current: plan.current_game_version || '—'}))}${gameWarning ? ` ${esc(I18N.t('modpack.gameMismatch'))}` : ''}</p>
				<div class="eyebrow">${esc(I18N.t('modpack.modsHeading', {count: plan.mods.length}))}</div>
				<div class="data-list">${mods}</div>
				<label class="dependency-row"><input type="checkbox" id="modpack-install" ${installs.length ? 'checked' : 'disabled'}><span class="grow"><b>${esc(I18N.t('modpack.install', {count: installs.length}))}</b><small>${esc(I18N.t('modpack.installHint'))}</small></span></label>
				<label class="dependency-row"><input type="checkbox" id="modpack-toggle" ${toggles.length ? 'checked' : 'disabled'}><span class="grow"><b>${esc(I18N.t('modpack.toggle', {count: toggles.length}))}</b></span></label>
				<label class="dependency-row"><input type="checkbox" id="modpack-extra" ${extra.length ? '' : 'disabled'}><span class="grow"><b>${esc(I18N.t('modpack.disableExtra', {count: extra.length}))}</b><small>${esc(extra.length ? extra.map(mod => mod.display_name || mod.modid || mod.name).join(', ') : I18N.t('modpack.noExtra'))}</small></span></label>
				<div class="eyebrow">${esc(I18N.t('modpack.configsHeading', {count: plan.configs.length}))}</div>
				<div class="data-list dependency-list">${configs}</div>
				${skipped}`,
		};
	}

	async function open(file) {
		const body = new FormData();
		body.append('file', file);
		const plan = await api('/api/mods/pack/inspect', {method: 'POST', body});

		const {body: box, actions, close} = modal({eyebrow: I18N.t('modpack.eyebrow'), title: I18N.t('modpack.title'), text: I18N.t('modpack.intro'), wide: true});
		const view = render(plan);
		box.innerHTML = view.html;

		const cancel = document.createElement('button');
		cancel.type = 'button';
		cancel.className = 'button';
		cancel.textContent = I18N.t('core.cancel');
		cancel.addEventListener('click', close);
		const apply = document.createElement('button');
		apply.type = 'button';
		apply.className = 'button primary';
		apply.textContent = I18N.t('modpack.apply');
		actions.append(cancel, apply);

		apply.addEventListener('click', async () => {
			const configs = [...box.querySelectorAll('input[data-config]:checked')].map(input => input.dataset.config);
			const toggle = $('#modpack-toggle', box).checked;
			const extra = $('#modpack-extra', box).checked;
			const install = $('#modpack-install', box).checked;
			try {
				if (configs.length || (toggle && view.toggles.length) || (extra && view.extra.length)) {
					const form = new FormData();
					form.append('file', file);
					form.append('configs', JSON.stringify(configs));
					form.append('toggle', String(toggle));
					form.append('disable_extra', String(extra));
					const result = await busy(apply, () => api('/api/mods/pack/apply', {method: 'POST', body: form}));
					toast(I18N.t('modpack.applied', {configs: result.configs.length, toggled: result.toggled.length}), result.failures.length ? '' : 'ok');
					if (result.failures.length) toast(I18N.t('modpack.failures', {items: result.failures.map(item => `${item.name}: ${item.error}`).join('; ')}), 'error');
				}
			} catch (error) { fail(error); return; }
			close();
			await VS.mods.load().catch(fail);
			// Установленный мод с другой версией в диалоге зависимостей — «устаревший»: ставится
			// версия сборки, прежняя уходит в историю для отката.
			const issues = install
				? view.installs.map(mod => ({modid: mod.modid, status: mod.status === 'missing' ? 'missing' : 'outdated', required: mod.version, installed: mod.installed, exact: true}))
				: [];
			if (issues.length) await VS.dependencies.offer(issues, new Set(), []).catch(fail);
		});
	}

	$('#modpack-upload').addEventListener('change', async event => {
		const file = event.target.files[0];
		if (!file) return;
		try {
			if (!can('admin')) return;
			await open(file);
		} catch (error) { fail(error); }
		finally { event.target.value = ''; }
	});
})();
