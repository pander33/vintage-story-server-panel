'use strict';

// Доустановка зависимостей: диалог по проблемам из /api/mods. Панель ничего не ставит сама (INV-28) —
// оператор отмечает строки, и каждая уходит отдельным install-release со своей проверкой совместимости.

(() => {
	const {esc, api, toast, fail, busy, modal, can} = VS;

	// Порядок как у ModVersion панели: dev и alpha раньше pre, preview и beta, те раньше rc, релиз
	// позже своих предрелизов. Только для подсказки — совместимость решает сервер.
	const RANKS = {dev: 0, alpha: 0, pre: 1, preview: 1, beta: 1, rc: 2};

	/** «1.2.3-rc.1» → [1, 2, 3, 2, 1]: три числа, ранг предрелиза (релиз — 3) и его номер. */
	function parts(version) {
		const text = String(version || '').trim().replace(/^v/i, '');
		const numbers = (text.split(/[-+]/)[0].match(/\d+/g) || []).slice(0, 3).map(Number);
		while (numbers.length < 3) numbers.push(0);
		const marker = text.match(/[-.+_](dev|alpha|preview|pre|beta|rc)[.-]?(\d*)(?=$|[.+_-])/i);
		return marker ? [...numbers, RANKS[marker[1].toLowerCase()], Number(marker[2] || 0)] : [...numbers, 3, 0];
	}

	function compare(left, right) {
		const a = parts(left), b = parts(right);
		for (let index = 0; index < a.length; index++) {
			const difference = a[index] - b[index];
			if (difference) return difference;
		}
		return 0;
	}

	const newest = releases => releases.reduce((best, release) => compare(release.version, best.version) > 0 ? release : best);
	const satisfies = (release, required) => !required || compare(release.version, required) >= 0;

	/** Строка предложения: что нужно и что нашлось на ModDB. */
	async function lookup(issue) {
		const row = {issue, state: 'none', release: null, identifier: issue.modid, note: ''};
		try {
			const data = await api(`/api/moddb/mod/${encodeURIComponent(issue.modid)}`);
			row.identifier = data.lookup_identifier || issue.modid;
			row.title = data.name;
			const compatible = (data.releases || []).filter(release => release.compatible);
			if (!compatible.length) {
				row.state = 'incompatible';
				row.note = (data.releases || []).length ? `нет релиза для игры ${data.game_version}` : 'на ModDB нет релизов';
				return row;
			}
			// Стабильный, подходящий по версии, — лучший выбор; предварительный берётся, только если
			// стабильного нужной версии нет, и сам не отмечается.
			const stable = compatible.filter(release => !release.prerelease);
			const fitting = list => list.filter(release => satisfies(release, issue.required));
			const pool = fitting(stable).length ? fitting(stable) : fitting(compatible).length ? fitting(compatible) : stable.length ? stable : compatible;
			row.release = newest(pool);
			row.state = 'found';
			if (!satisfies(row.release, issue.required)) {
				row.note = `найдена ${row.release.version}, нужна ≥ ${issue.required}: требуемая версия для этой игры не опубликована`;
			} else if (row.release.prerelease) {
				row.note = 'подходит только предварительный релиз — отметьте, если согласны';
			}
		} catch (error) {
			row.state = 'missing';
			row.note = error.message;
		}
		return row;
	}

	function describe(issue) {
		const required = issue.required ? ` ≥ ${issue.required}` : '';
		return issue.status === 'outdated' ? `установлена ${issue.installed || '—'}, нужна${required}` : `не установлен${required}`;
	}

	/**
	 * Открыть диалог. issues — [{modid, status, required, installed}]; chain — уже предложенные в этой
	 * цепочке идентификаторы (защита от циклов); present — установленные моды: отключённая зависимость
	 * считается отсутствующей, но её нужно включить, а не скачивать второй раз.
	 * Возвращает после закрытия диалога.
	 */
	async function offer(issues, chain = new Set(), present = []) {
		const fresh = issues.filter(issue => issue.status !== 'unverified' && !chain.has(issue.modid.toLowerCase()));
		fresh.forEach(issue => chain.add(issue.modid.toLowerCase()));
		if (!fresh.length) return;

		let finished;
		const closed = new Promise(resolve => { finished = resolve; });
		const {body, actions, close} = modal({eyebrow: 'ЗАВИСИМОСТИ', title: 'Что нужно установить', text: 'Панель ничего не ставит сама: отметьте нужное. Каждый релиз проверяется на совместимость с игрой сервера ещё раз перед скачиванием. Изменения подействуют после перезапуска сервера.', onClose: finished});
		body.innerHTML = '<div class="empty">Поиск на ModDB…</div>';
		const disabled = new Map(present.filter(mod => !mod.enabled && mod.modid).map(mod => [mod.modid.toLowerCase(), mod]));
		const rows = await Promise.all(fresh.map(issue => disabled.has(issue.modid.toLowerCase())
			? {issue, state: 'disabled', title: disabled.get(issue.modid.toLowerCase()).display_name || issue.modid, note: `установлен, но отключён (${disabled.get(issue.modid.toLowerCase()).name}) — включите его в списке модов`}
			: lookup(issue)));
		if (!body.isConnected) return;

		const admin = can('admin');
		body.innerHTML = `<div class="data-list dependency-list">${rows.map((row, index) => {
			const installable = row.state === 'found';
			const label = installable ? `${row.title || row.issue.modid} ${row.release.version}` : (row.title || row.issue.modid);
			const pre = installable && row.release.prerelease ? ' <span class="badge warn">предварительный</span>' : '';
			return `<label class="dependency-row"><input type="checkbox" data-index="${index}" ${installable && admin && !row.note ? 'checked' : ''} ${installable && admin ? '' : 'disabled'}>
				<span class="grow"><b>${esc(label)}</b>${pre}<small>${esc(describe(row.issue))}</small>${row.note ? `<small class="accent">${esc(row.note)}</small>` : ''}</span></label>`;
		}).join('')}</div>`;

		const cancel = document.createElement('button');
		cancel.type = 'button';
		cancel.className = 'button';
		cancel.textContent = 'Закрыть';
		cancel.addEventListener('click', close);
		const install = document.createElement('button');
		install.type = 'button';
		install.className = 'button primary';
		install.textContent = 'Установить отмеченные';
		install.disabled = !admin || !rows.some(row => row.state === 'found');
		actions.append(cancel, install);

		const installed = [];
		install.addEventListener('click', async () => {
			const chosen = [...body.querySelectorAll('input:checked')].map(input => rows[Number(input.dataset.index)]);
			if (!chosen.length) { toast('Ничего не отмечено'); return; }
			try {
				await busy(install, async () => {
					for (const row of chosen) {
						const data = await api('/api/mods/install-release', {method: 'POST', body: {identifier: row.identifier, release_id: Number(row.release.releaseid)}});
						installed.push(data.modid || row.issue.modid);
						toast(`Установлен ${data.modid} ${data.version}`, 'ok');
					}
				});
			} catch (error) { fail(error); }
			close();
		});

		await closed;
		if (installed.length) await VS.mods.reload(installed, chain);
	}

	/**
	 * Строки предложений из /api/mods/updates: стабильный релиз, если он не установлен, и рядом
	 * предварительный, если ModDB знает более новый и он новее установленного.
	 */
	VS.modUpdates = items => items.flatMap(item => [
		item.available && item.available.version !== item.installed ? {item, release: item.available} : null,
		item.available_prerelease && item.available_prerelease.version !== item.installed && compare(item.available_prerelease.version, item.installed) > 0 ? {item, release: item.available_prerelease} : null,
	].filter(Boolean));

	VS.dependencies = {offer, compare};
})();
