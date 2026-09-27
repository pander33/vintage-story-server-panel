'use strict';

// Каталог ModDB: сетка карточек с отбором, страница мода в диалоге, описание мода из чужого HTML.
// Список держит и отбирает панель (GET /api/moddb/mods), картинки идут через её прокси — CSP пускает
// только свой хост. Установка — прежний install-release: его обработчик в content.js.

(() => {
	const {$, esc, api, fail, modal, can} = VS;

	const PAGE = 30;
	const SIDES = {both: 'клиент и сервер', server: 'сервер', client: 'клиент'};
	const catalog = {offset: 0, total: 0, loading: false, ticket: 0, tags: new Set(), known: null, gameVersion: '', allTags: false};
	const TOP_TAGS = 24;

	// ---------- описание мода: чужой HTML ----------

	const KEEP = new Set(['P', 'BR', 'A', 'STRONG', 'B', 'EM', 'I', 'U', 'CODE', 'PRE', 'UL', 'OL', 'LI', 'H3', 'H4', 'H5', 'BLOCKQUOTE', 'IMG']);
	const HEADINGS = {H1: 'h3', H2: 'h3', H6: 'h5'};
	const DROP = new Set(['SCRIPT', 'STYLE', 'IFRAME', 'VIDEO', 'AUDIO', 'OBJECT', 'EMBED', 'FORM', 'INPUT', 'BUTTON', 'SELECT', 'TEXTAREA', 'LINK', 'META', 'TEMPLATE', 'NOSCRIPT', 'SVG', 'MATH', 'CANVAS']);
	const CDN = 'moddbcdn.vintagestory.at';

	/** Адрес только http(s); всё остальное (javascript:, data:, относительные) — null. */
	function safeUrl(value) {
		try {
			const url = new URL(String(value || '').trim(), 'https://invalid.example/');
			return (url.protocol === 'http:' || url.protocol === 'https:') && url.host !== 'invalid.example' ? url.href : null;
		} catch { return null; }
	}

	const proxied = url => `/api/moddb/image?url=${encodeURIComponent(url)}`;

	function link(href, label) {
		const a = document.createElement('a');
		a.href = href;
		a.rel = 'noopener noreferrer';
		a.target = '_blank';
		a.textContent = label;
		return a;
	}

	/**
	 * Строит безопасное дерево из чужого HTML. Разбор через DOMParser инертен: скрипты в нём не
	 * исполняются. Новое дерево собирается createElement только из белого списка; строк HTML в
	 * innerHTML нет вовсе, CSP остаётся второй линией защиты.
	 */
	function sanitizeInto(target, html) {
		const source = new DOMParser().parseFromString(String(html || ''), 'text/html').body;
		const walk = (node, into) => {
			for (const child of node.childNodes) {
				if (child.nodeType === Node.TEXT_NODE) { into.append(document.createTextNode(child.textContent)); continue; }
				if (child.nodeType !== Node.ELEMENT_NODE) continue;
				const name = child.tagName.toUpperCase();
				if (name === 'IFRAME' || name === 'VIDEO') {
					const href = safeUrl(child.getAttribute('src'));
					if (href) { const p = document.createElement('p'); p.append(link(href, 'Видео (откроется на другом сайте)')); into.append(p); }
					continue;
				}
				if (DROP.has(name)) continue;
				if (name === 'IMG') { into.append(image(child)); continue; }
				if (name === 'A') {
					const href = safeUrl(child.getAttribute('href'));
					if (href) { const a = link(href, ''); walk(child, a); into.append(a); } else walk(child, into);
					continue;
				}
				const tag = HEADINGS[name] || (KEEP.has(name) ? name.toLowerCase() : null);
				if (tag) { const element = document.createElement(tag); walk(child, element); into.append(element); } else walk(child, into);
			}
		};
		target.replaceChildren();
		walk(source, target);
	}

	/** Картинка описания: с CDN ModDB — через прокси, с чужого хоста — ссылкой «картинка». */
	function image(element) {
		const href = safeUrl(element.getAttribute('src'));
		if (!href) return document.createTextNode('');
		if (new URL(href).protocol === 'https:' && new URL(href).host.toLowerCase() === CDN) {
			const img = document.createElement('img');
			img.src = proxied(href);
			img.alt = element.getAttribute('alt') || '';
			img.loading = 'lazy';
			img.className = 'mod-inline-image';
			return img;
		}
		return link(href, element.getAttribute('alt') || 'картинка');
	}

	// ---------- каталог ----------

	const fmtCount = value => Number(value || 0).toLocaleString('ru-RU');

	function tagChip(name) {
		const tag = (catalog.known || []).find(item => item.name === name);
		return `<span class="tag-chip" data-color="${esc(tag?.color || '')}">${esc(name)}</span>`;
	}

	function card(mod) {
		const logo = mod.logo ? `<img src="${esc(mod.logo)}" alt="" loading="lazy">` : '<span class="mod-logo-empty">✥</span>';
		const compatible = mod.compatible === true ? `<span class="badge ok">для ${esc(catalog.gameVersion)}</span>` : '';
		const installed = mod.installed ? `<span class="badge installed" title="${esc(mod.installed.enabled ? 'Установлен и включён' : 'Установлен, но отключён')}">установлен${mod.installed.version ? ` ${esc(mod.installed.version)}` : ''}${mod.installed.enabled ? '' : ' · откл.'}</span>` : '';
		return `<article class="mod-card${mod.installed ? ' installed' : ''}" data-open="${esc(mod.identifier)}" data-ids="${esc((mod.modidstrs || []).join(','))}" tabindex="0" role="button">
			<div class="mod-logo">${logo}</div>
			<div class="mod-body"><h4>${esc(mod.name)}</h4><small>${esc(mod.author || '—')} · ${esc(SIDES[mod.side] || mod.side || '—')}</small>
			<p>${esc(mod.summary || '')}</p>
			<div class="mod-tags">${installed}${(mod.tags || []).slice(0, 3).map(tagChip).join('')}${compatible}</div>
			<small class="mod-stats">↓ ${esc(fmtCount(mod.downloads))} · ♥ ${esc(fmtCount(mod.follows))}</small></div></article>`;
	}

	/** Цвет тега задаёт скрипт через CSSOM: атрибуты style в разметке запрещены CSP. */
	function paintTags(root) {
		root.querySelectorAll('.tag-chip[data-color]').forEach(chip => { const color = chip.dataset.color; if (/^#[0-9a-f]{3,8}$/i.test(color)) chip.style.setProperty('--tag', color); });
	}

	function parameters(offset) {
		const params = new URLSearchParams({sort: $('#catalog-sort').value, offset, limit: PAGE});
		if ($('#catalog-side').value) params.set('side', $('#catalog-side').value);
		if ($('#catalog-compatible').checked) params.set('compatible', '1');
		if ($('#catalog-hide-installed').checked) params.set('hide_installed', '1');
		const text = $('#catalog-query').value.trim();
		if (text) params.set('q', text);
		catalog.tags.forEach(tag => params.append('tag', tag));
		return params;
	}

	async function load(reset) {
		if (catalog.loading && !reset) return;
		const ticket = ++catalog.ticket;
		catalog.loading = true;
		if (reset) { catalog.offset = 0; $('#catalog-grid').innerHTML = '<div class="empty">Загрузка каталога…</div>'; }
		const state = $('#catalog-state');
		try {
			const data = await api(`/api/moddb/mods?${parameters(catalog.offset)}`);
			if (ticket !== catalog.ticket) return;
			catalog.total = data.total;
			catalog.gameVersion = data.game_version;
			if (!catalog.known || reset) { catalog.known = data.available_tags || []; renderTags(); }
			state.className = `notice ${data.stale ? 'warn' : 'hidden'}`;
			state.textContent = data.stale ? 'ModDB не ответил — показан сохранённый список.' : '';
			$('#catalog-compatible-label').textContent = `Только совместимые с ${data.game_version}`;
			$('#catalog-compatible').disabled = !data.compatible_known;
			// Список установленных даёт демон; без него пометок нет и отбор не действует.
			$('#catalog-hide-installed').disabled = !data.installed_known;
			const grid = $('#catalog-grid');
			if (reset) grid.replaceChildren();
			const html = data.mods.map(card).join('');
			grid.insertAdjacentHTML('beforeend', html);
			if (reset && !data.mods.length) grid.innerHTML = '<div class="empty">Ничего не найдено.</div>';
			paintTags(grid);
			catalog.offset += data.mods.length;
			const more = catalog.offset < data.total;
			$('#catalog-more').classList.toggle('hidden', !more);
			$('#catalog-count').textContent = `Найдено ${fmtCount(data.total)}`;
		} catch (error) {
			if (ticket !== catalog.ticket) return;
			$('#catalog-grid').innerHTML = `<div class="notice error">${esc(error.message)}</div>`;
			$('#catalog-more').classList.add('hidden');
		} finally { if (ticket === catalog.ticket) catalog.loading = false; }
	}

	/** Теги по частоте; сначала самые ходовые, остальные — по кнопке. Выбранный тег виден всегда. */
	function renderTags() {
		const box = $('#catalog-tags');
		const known = catalog.known || [];
		const shown = catalog.allTags ? known : known.filter((tag, index) => index < TOP_TAGS || catalog.tags.has(tag.name));
		const toggle = known.length > TOP_TAGS ? `<button class="link-button" type="button" data-tags-toggle>${catalog.allTags ? 'Свернуть' : `Все теги (${known.length})`}</button>` : '';
		box.innerHTML = shown.map(tag => `<button class="tag-chip selectable ${catalog.tags.has(tag.name) ? 'on' : ''}" type="button" data-tag="${esc(tag.name)}" data-color="${esc(tag.color)}" title="Модов: ${esc(tag.count ?? '')}">${esc(tag.name)}</button>`).join('') + toggle;
		paintTags(box);
	}

	$('#catalog-tags').addEventListener('click', event => {
		if (event.target.closest('[data-tags-toggle]')) { catalog.allTags = !catalog.allTags; renderTags(); return; }
		const button = event.target.closest('[data-tag]');
		if (!button) return;
		const name = button.dataset.tag;
		if (catalog.tags.has(name)) catalog.tags.delete(name); else catalog.tags.add(name);
		button.classList.toggle('on', catalog.tags.has(name));
		load(true);
	});
	['#catalog-sort', '#catalog-side', '#catalog-compatible', '#catalog-hide-installed'].forEach(selector => $(selector).addEventListener('change', () => load(true)));
	let typing = null;
	$('#catalog-query').addEventListener('input', () => { clearTimeout(typing); typing = setTimeout(() => load(true), 350); });
	$('#catalog-form').addEventListener('submit', event => { event.preventDefault(); load(true); });
	$('#catalog-more').addEventListener('click', () => load(false));
	$('#catalog-grid').addEventListener('error', event => { if (event.target.tagName === 'IMG') event.target.replaceWith(Object.assign(document.createElement('span'), {className: 'mod-logo-empty', textContent: '✥'})); }, true);

	// Прокрутка догружает следующую страницу.
	new IntersectionObserver(entries => { if (entries.some(entry => entry.isIntersecting) && !catalog.loading) load(false); }, {rootMargin: '300px'}).observe($('#catalog-more'));

	// ---------- страница мода ----------

	async function installedVersion(ids) {
		try {
			const wanted = new Set(ids.map(id => id.toLowerCase()));
			const data = await api('/api/mods');
			return (data.mods || []).find(mod => wanted.has(String(mod.modid || '').toLowerCase())) || null;
		} catch { return null; }
	}

	async function openMod(identifier, ids) {
		const {body} = modal({eyebrow: 'МОД', title: 'Загрузка…', wide: true});
		body.innerHTML = '<div class="empty">Загрузка страницы мода…</div>';
		try {
			const [data, installed] = await Promise.all([api(`/api/moddb/mod/${encodeURIComponent(identifier)}`), installedVersion([identifier, ...ids])]);
			if (!body.isConnected) return;
			$('h2', body.closest('dialog')).textContent = data.name;
			const links = [['Исходный код', data.sourcecodeurl], ['Багтрекер', data.issuetrackerurl], ['Вики', data.wikiurl], ['Сайт', data.homepageurl], ['Трейлер', data.trailervideourl]]
				.map(([label, href]) => [label, safeUrl(href)]).filter(([, href]) => href);
			const compatible = (data.releases || []).filter(release => release.compatible);
			const admin = can('admin');
			body.innerHTML = `<div class="mod-head">${data.logo ? `<img class="mod-head-logo" src="${esc(data.logo)}" alt="">` : ''}
				<div><small>${esc(data.author || '—')} · ${esc(SIDES[data.side] || data.side || '—')} · ↓ ${esc(fmtCount(data.downloads))} · ♥ ${esc(fmtCount(data.follows))}</small>
				<div class="mod-tags">${(data.tags || []).map(tagChip).join('')}</div>
				${installed ? `<small class="accent">Установлена версия ${esc(installed.version || '—')}${installed.enabled ? '' : ' (отключён)'}</small>` : ''}</div></div>
				<div class="mod-description" data-description></div>
				<div class="mod-links" data-links></div>
				<div class="mod-shots" data-shots></div>
				<h3 class="subheading">Релизы для ${esc(data.game_version)}</h3>
				<div data-releases></div>`;
			sanitizeInto($('[data-description]', body), data.text);
			const linkBox = $('[data-links]', body);
			links.forEach(([label, href]) => linkBox.append(link(href, label)));
			const shots = $('[data-shots]', body);
			(data.screenshots || []).forEach(shot => {
				const img = document.createElement('img');
				img.src = shot.thumbnail;
				img.alt = 'Скриншот';
				img.loading = 'lazy';
				img.addEventListener('click', () => showShot(shot.url));
				shots.append(img);
			});
			paintTags(body);
			$('[data-releases]', body).innerHTML = compatible.length ? compatible.slice(0, 20).map(release => {
				const same = installed && release.version === installed.version;
				const newer = installed && VS.dependencies.compare(release.version, installed.version) > 0;
				const label = same ? 'Установлена' : installed ? (newer ? 'Обновить' : 'Поставить эту версию') : 'Установить на сервер';
				return `<div class="release-row"><div class="card-header"><div><b>${esc(release.version || '—')}</b>${release.prerelease ? ' <span class="badge warn">предварительный</span>' : ''}${release.compatibility === 'same_minor' ? ` <span class="badge warn">для ${esc(release.compatible_tag)}</span>` : ''}<small class="muted mono"> ${esc(release.filename || '')}</small></div><button class="button small primary" type="button" data-release-id="${esc(release.releaseid)}" data-identifier="${esc(data.lookup_identifier || identifier)}" ${admin && !same ? '' : 'disabled'}>${esc(label)}</button></div><pre>${esc(release.changelog || 'Без changelog')}</pre></div>`;
			}).join('') : '<div class="notice warn">Нет релиза для этой ветки игры.</div>';
		} catch (error) { if (body.isConnected) { body.innerHTML = `<div class="notice error">${esc(error.message)}</div>`; } else fail(error); }
	}

	function showShot(url) {
		const {body} = modal({eyebrow: 'СКРИНШОТ', title: 'Скриншот', wide: true});
		const img = document.createElement('img');
		img.src = url;
		img.alt = 'Скриншот';
		img.className = 'mod-shot-large';
		body.append(img);
	}

	function open(target) {
		const cardElement = target.closest('[data-open]');
		if (cardElement) openMod(cardElement.dataset.open, (cardElement.dataset.ids || '').split(',').filter(Boolean)).catch(fail);
	}
	$('#catalog-grid').addEventListener('click', event => open(event.target));
	$('#catalog-grid').addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(event.target); } });

	// Поиск по строке остаётся: его результаты открываются на той же странице мода.
	/** Пометка «установлен» на уже показанных карточках — без перезагрузки сетки и потери прокрутки. */
	function markInstalled(modid, version) {
		const wanted = String(modid || '').toLowerCase();
		if (!wanted) return;
		$('#catalog-grid').querySelectorAll('[data-ids]').forEach(element => {
			if (!element.dataset.ids.toLowerCase().split(',').includes(wanted)) return;
			element.classList.add('installed');
			element.querySelector('.badge.installed')?.remove();
			element.querySelector('.mod-tags')?.insertAdjacentHTML('afterbegin', `<span class="badge installed" title="Установлен и включён">установлен${version ? ` ${esc(version)}` : ''}</span>`);
		});
	}

	VS.catalog = {enter() { if (!catalog.known) load(true); }, openMod, sanitizeInto, markInstalled};
})();
