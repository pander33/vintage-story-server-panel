const $ = selector => document.querySelector(selector);
const STEPS = ['claim', 'access', 'preflight', 'version', 'server', 'create', 'done'];
let csrf = '', current = 'claim', summary = null, releases = [], chosen = '', stream = null;

const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const fmtBytes = value => { const n = Number(value) || 0; if (!n) return '—'; const u = ['Б','КБ','МБ','ГБ','ТБ'], i = Math.min(Math.floor(Math.log(n)/Math.log(1024)), u.length-1); return `${(n/1024**i).toFixed(i?1:0)} ${u[i]}`; };

function toast(message, error = false) {
	const element = $('#toast');
	element.textContent = message;
	element.className = `toast${error ? " error" : ""}`;
	setTimeout(() => element.classList.add('hidden'), 5000);
}

async function api(path, options = {}) {
	options.headers = {...(options.headers || {})};
	if (csrf && options.method && options.method !== 'GET') options.headers['X-CSRF-Token'] = csrf;
	if (options.body) { options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(options.body); }
	const response = await fetch(path, options);
	let data = {};
	try { data = await response.json(); } catch {}
	if (response.status === 410) { location.href = '/'; throw new Error('Мастер уже завершён'); }
	if (!response.ok) throw new Error(data.detail || `HTTP ${response.status}`);
	return data;
}

function show(step) {
	current = step;
	$('#step-login').classList.add('hidden');
	document.querySelectorAll('.wizard-step').forEach(node => node.classList.add('hidden'));
	$(`#step-${step}`).classList.remove('hidden');
	const index = STEPS.indexOf(step);
	document.querySelectorAll('#wizard-steps li').forEach(node => {
		const position = STEPS.indexOf(node.dataset.step);
		node.className = position < index ? 'done' : (position === index ? 'active' : '');
	});
	$('#step-counter').textContent = `Шаг ${index + 1} из ${STEPS.length}`;
}

// ----- шаг 1: захват -------------------------------------------------------------

$('#claim-form').addEventListener('submit', async event => {
	event.preventDefault();
	$('#claim-error').textContent = '';
	if ($('#claim-password').value !== $('#claim-password2').value) {
		$('#claim-error').textContent = 'Пароли не совпадают';
		return;
	}
	try {
		const result = await api('/api/setup/claim', {method: 'POST', body: {
			token: $('#claim-token').value.trim(),
			username: $('#claim-username').value.trim(),
			password: $('#claim-password').value,
		}});
		csrf = result.csrf;
		// Сервер уже создан установщиком: мастеру больше нечего спрашивать.
		if (result.completed) { location.href = '/'; return; }
		history.replaceState(null, '', '/setup');
		await loadSummary();
		show('access');
	} catch (error) { $('#claim-error').textContent = error.message; }
});

// ----- шаг 2: доступ -------------------------------------------------------------

// Предупреждение демона о брандмауэре после сохранения доступа: показывается в отчёте проверки.
let accessNotice = '';

function updatePublishWarning() {
	$('#access-warning').classList.toggle('hidden', $('#access-bind').value === '127.0.0.1');
}
$('#access-bind').addEventListener('change', updatePublishWarning);

$('#access-form').addEventListener('submit', async event => {
	event.preventDefault();
	$('#access-error').textContent = '';
	const button = event.submitter;
	if (button) button.disabled = true;
	try {
		const saved = await api('/api/setup/access', {method: 'POST', body: {
			bind: $('#access-bind').value,
			port: Number($('#access-port').value),
			allowed_hosts: $('#access-hosts').value,
			secure_cookie: $('#access-secure').checked,
		}});
		accessNotice = (saved && saved.firewall_warning) || '';
		await runPreflight('');
		show('preflight');
	} catch (error) { $('#access-error').textContent = error.message; }
	finally { if (button) button.disabled = false; }
});

// ----- шаг 3: проверка -----------------------------------------------------------

function renderPreflight(report) {
	const runtime = report.runtime || {};
	const rows = [];
	rows.push(item(runtime.dotnet ? 'ok' : 'error', '.NET',
		runtime.dotnet ? `${escapeHtml(runtime.dotnet)} · среды выполнения: ${(runtime.majors || []).join(", ") || "не определены"}`
                   : 'не найден'));
	const writable = (report.instances || {}).writable !== false;
	rows.push(item(writable ? 'ok' : 'error', 'Реестр серверов',
		writable ? 'доступен для записи' : 'доступен только на чтение'));
	const used = (report.used_ports || []);
	rows.push(item('ok', 'Занятые порты', used.length ? used.join(', ') : 'нет'));
	if (report.free !== undefined) {
		rows.push(item(report.free >= (report.required_free || 0) ? 'ok' : 'error', 'Свободное место',
			`${fmtBytes(report.free)} из необходимых ${fmtBytes(report.required_free)}`));
	}
	for (const blocker of report.blockers || []) rows.push(item('error', 'Препятствие', escapeHtml(blocker)));
	for (const warning of report.warnings || []) rows.push(item('warn', 'Предупреждение', escapeHtml(warning)));
	if (accessNotice) rows.push(item('warn', 'Брандмауэр', escapeHtml(accessNotice)));
	$('#preflight-report').innerHTML = rows.join('');
	$('#preflight-next').disabled = !report.ok;
	return report;

	function item(kind, title, detail) {
		return `<p class="notice ${kind}"><strong>${escapeHtml(title)}</strong>: ${detail}</p>`;
	}
}

async function runPreflight(version) {
	try {
		return renderPreflight(await api('/api/setup/preflight', {method: 'POST', body: {version}}));
	} catch (error) {
		$('#preflight-report').innerHTML = `<p class="notice error">${escapeHtml(error.message)}</p>`;
		$('#preflight-next').disabled = true;
		return {ok: false};
	}
}

$('#preflight-again').addEventListener('click', () => runPreflight(''));
$('#preflight-next').addEventListener('click', () => { show('version'); loadCatalogue(false); });

// ----- шаг 4: версия -------------------------------------------------------------

function renderReleases() {
	const showUnstable = $('#version-unstable').checked;
	const visible = releases.filter(release => showUnstable || release.channel === 'stable');
	if (!visible.length) {
		$('#version-list').innerHTML = `<p class="muted">Список пуст.</p>`;
		return;
	}
	$('#version-list').innerHTML = visible.slice(0, 24).map(release => `
		<article class="catalog-item${release.version === chosen ? " chosen" : ""}" data-version="${escapeHtml(release.version)}">
			<h3>${escapeHtml(release.version)}</h3>
			<p>${release.channel === "stable" ? "Стабильная" : "Нестабильная"}${release.latest ? " · последняя" : ""}</p>
			<small>${escapeHtml(release.size_text || fmtBytes(release.size))}${release.md5 ? "" : " · без контрольной суммы"}</small>
			<button type="button"${release.md5 ? "" : " disabled"}>${release.version === chosen ? "Выбрана" : "Выбрать"}</button>
		</article>`).join('');
}

$('#version-list').addEventListener('click', event => {
	const card = event.target.closest('.catalog-item');
	if (!card) return;
	chosen = card.dataset.version;
	$('#version-next').disabled = false;
	renderReleases();
});
$('#version-unstable').addEventListener('change', renderReleases);
$('#version-refresh').addEventListener('click', () => loadCatalogue(true));

async function loadCatalogue(refresh) {
	$('#version-error').textContent = '';
	try {
		const data = await api(`/api/setup/catalogue?refresh=${refresh ? "true" : "false"}`);
		releases = data.versions || [];
		if (!chosen) chosen = data.latest_stable || '';
		$('#version-next').disabled = !chosen;
		const stale = $('#version-stale');
		stale.classList.toggle('hidden', !data.stale);
		if (data.stale) stale.textContent = 'Официальный сервер недоступен, показан сохранённый список. Он может быть устаревшим.';
		renderReleases();
	} catch (error) { $('#version-error').textContent = error.message; }
}

$('#version-next').addEventListener('click', async () => {
	const report = await runPreflight(chosen);
	if (!report.ok) { show('preflight'); toast('Выбранная версия не проходит проверку', true); return; }
	prepareServerForm();
	show('server');
});

// ----- шаг 5: настройки сервера --------------------------------------------------

const PLAYSTYLE_LABELS = {
	surviveandbuild: 'Выживание и строительство',
	exploration: 'Исследование',
	wildernesssurvival: 'Суровое выживание',
	homosapiens: 'Homo Sapiens',
	creativebuilding: 'Творчество',
};
const LANGUAGE_LABELS = {ru: 'Русский', en: 'English', de: 'Deutsch', fr: 'Français', es: 'Español', zh: '中文'};

function fill(select, values, labels) {
	select.innerHTML = values.map(value =>
		`<option value="${escapeHtml(value)}">${escapeHtml(labels[value] || value)}</option>`).join('');
}

function prepareServerForm() {
	const draft = (summary && summary.draft && summary.draft.server) || {};
	const hint = (summary && summary.suggest) || {id: 'main', port: 42420};
	fill($('#server-language'), (summary && summary.languages) || ['ru'], LANGUAGE_LABELS);
	fill($('#server-playstyle'), (summary && summary.playstyles) || ['surviveandbuild'], PLAYSTYLE_LABELS);
	$('#server-id').value = draft.id || hint.id;
	$('#server-title').value = draft.name || '';
	$('#server-name').value = draft.server_name || '';
	$('#server-port').value = draft.port || hint.port;
	$('#server-players').value = draft.max_players || 16;
	$('#server-language').value = draft.language || 'ru';
	$('#server-world').value = draft.world_name || 'Новый мир';
	$('#server-seed').value = draft.seed || '';
	$('#server-playstyle').value = draft.playstyle || 'surviveandbuild';
	$('#server-description').value = draft.description || '';
	$('#server-whitelist').checked = Boolean(draft.whitelist);
	$('#server-pvp').checked = draft.pvp === undefined ? true : Boolean(draft.pvp);
	api('/api/settings/servers-root').then(data => {
		const root = $('#server-root');
		root.dataset.current = data.instances_root || '';
		if (!root.value) root.value = root.dataset.current;
	}).catch(() => {});
}

$('#server-form').addEventListener('submit', async event => {
	event.preventDefault();
	$('#server-error').textContent = '';
	const button = event.submitter;
	if (button) button.disabled = true;
	try {
		// Каталог — раньше описания сервера: на следующем шаге сервер создаётся уже в нём.
		const root = $('#server-root');
		const path = root.value.trim();
		if (path && path !== root.dataset.current) {
			const changed = await api('/api/settings/servers-root', {method: 'PUT', body: {path}});
			root.value = root.dataset.current = changed.instances_root;
		}
		const result = await api('/api/setup/server', {method: 'POST', body: {
			id: $('#server-id').value.trim(),
			name: $('#server-title').value.trim(),
			version: chosen,
			port: Number($('#server-port').value),
			server_name: $('#server-name').value.trim(),
			description: $('#server-description').value.trim(),
			max_players: Number($('#server-players').value),
			whitelist: $('#server-whitelist').checked,
			pvp: $('#server-pvp').checked,
			language: $('#server-language').value,
			world_name: $('#server-world').value.trim(),
			seed: $('#server-seed').value.trim(),
			playstyle: $('#server-playstyle').value,
		}});
		summary = summary || {draft: {}};
		summary.draft = {...(summary.draft || {}), server: result.spec};
		describeCreation(result.spec);
		show('create');
	} catch (error) { $('#server-error').textContent = error.message; }
	finally { if (button) button.disabled = false; }
});

// ----- шаг 6: создание -----------------------------------------------------------

const STEP_LABELS = {
	catalogue: 'Версия найдена в каталоге',
	download: 'Сборка скачана и проверена',
	create: 'Каталоги и настройки созданы',
	service: 'systemd-юнит установлен',
	start: 'Сервер запущен',
	ready: 'Игра ответила телеметрией',
	rollback: 'Откат',
};

function describeCreation(spec) {
	$('#create-summary').innerHTML =
		`Сервер <strong>${escapeHtml(spec.id)}</strong>, версия <strong>${escapeHtml(spec.version)}</strong>, ` +
		`порт ${escapeHtml(String(spec.port))}, мир «${escapeHtml(spec.world_name)}».`;
}

function renderRun(record) {
	if (!record) return;
	const order = ['catalogue', 'download', 'create', 'service', 'start', 'ready'];
	$('#create-progress').innerHTML = order.map(action => {
		const step = (record.steps || []).find(item => item.action === action);
		const state = step ? (step.status === 'ok' ? 'done' : step.status) : 'pending';
		return `<li class="${escapeHtml(state)}">${escapeHtml(STEP_LABELS[action] || action)}` +
           (step && step.detail ? ` <small>${escapeHtml(step.detail)}</small>` : '') + `</li>`;
	}).join('');
	$('#create-detail').textContent = record.detail || (record.status === 'running' ? 'Выполняется…' : '');
	if (record.status === 'success') {
		$('#create-finish').classList.remove('hidden');
		$('#create-start').classList.add('hidden');
		$('#create-retry').classList.add('hidden');
		$('#create-skip').classList.add('hidden');
	} else if (record.status && record.status !== 'running') {
		$('#create-error').textContent = record.detail || 'Создание не удалось';
		$('#create-start').classList.remove('hidden');
		$('#create-start').disabled = false;
		$('#create-retry').classList.remove('hidden');
		$('#create-skip').classList.remove('hidden');
	}
}

function watchProgress() {
	if (stream) stream.close();
	stream = new EventSource('/api/setup/stream');
	stream.onmessage = event => {
		const snapshot = JSON.parse(event.data);
		renderRun(snapshot.current || (snapshot.history || [])[0]);
	};
	stream.onerror = () => { stream.close(); stream = null; };
}

$('#create-start').addEventListener('click', async () => {
	$('#create-error').textContent = '';
	$('#create-start').disabled = true;
	try {
		await api('/api/setup/create', {method: 'POST', body: {}});
		watchProgress();
	} catch (error) {
		$('#create-error').textContent = error.message;
		$('#create-start').disabled = false;
	}
});

$('#create-retry').addEventListener('click', () => { prepareServerForm(); show('server'); });

$('#create-finish').addEventListener('click', async () => {
	$('#create-finish').disabled = true;
	try {
		const result = await api('/api/setup/finish', {method: 'POST', body: {restart: true}});
		if (stream) { stream.close(); stream = null; }
		$('#done-detail').textContent =
			`Сервер ${result.instance} создан. ${result.detail}. Панель откроется по адресу ${result.url}`;
		$('#done-link').href = result.url;
		show('done');
	} catch (error) {
		$('#create-error').textContent = error.message;
		$('#create-finish').disabled = false;
	}
});

// ----- пропуск создания сервера --------------------------------------------------

// Мастер закрывается без сервера: панель откроется с пустым списком серверов, первый
// создаётся из «Обзора». Каталог серверов и доступ уже записаны своими шагами.
document.querySelectorAll('.skip-server').forEach(button => button.addEventListener('click', async () => {
	if (!confirm('Завершить установку без сервера? Сервер можно будет создать позже в панели: «Обзор» → «Новый сервер».')) return;
	const error = $(`#${current}-error`);
	if (error) error.textContent = '';
	button.disabled = true;
	try {
		const result = await api('/api/setup/finish', {method: 'POST', body: {restart: true, without_server: true}});
		if (stream) { stream.close(); stream = null; }
		$('#done-detail').textContent = `Установка завершена без сервера. ${result.detail}. Панель откроется по адресу ${result.url}`;
		$('#done-link').href = result.url;
		$('#done-no-server').classList.remove('hidden');
		show('done');
	} catch (failure) {
		if (error) error.textContent = failure.message; else toast(failure.message, true);
		button.disabled = false;
	}
}));

// ----- вход при возобновлении ----------------------------------------------------

$('#login-form').addEventListener('submit', async event => {
	event.preventDefault();
	$('#login-error').textContent = '';
	try {
		const result = await api('/api/login', {method: 'POST', body: {
			username: $('#login-username').value.trim(),
			password: $('#login-password').value,
			otp: $('#login-otp').value.trim(),
		}});
		csrf = result.csrf;
		await resume();
	} catch (error) { $('#login-error').textContent = error.message; }
});

// ----- общее ---------------------------------------------------------------------

document.querySelectorAll('[data-goto]').forEach(button =>
	button.addEventListener('click', () => show(button.dataset.goto)));

async function loadSummary() {
	summary = await api('/api/setup/summary');
	const access = summary.access || {};
	const draft = summary.draft || {};
	$('#access-bind').value = (draft.access && draft.access.PANEL_BIND) || access.bind || '0.0.0.0';
	$('#access-port').value = (draft.access && draft.access.PANEL_PORT) || access.port || 8080;
	$('#access-hosts').value = (draft.access && draft.access.PANEL_ALLOWED_HOSTS) || access.allowed_hosts || '*';
	$('#access-secure').checked = (draft.access ? draft.access.PANEL_SECURE_COOKIE === 'true' : Boolean(access.secure_cookie));
	updatePublishWarning();
	if (draft.server && draft.server.version) chosen = draft.server.version;
	return summary;
}

async function boot() {
	let state;
	try { state = await api('/api/setup/state'); }
	catch { toast('Панель недоступна', true); return; }
	if (!state.required) { location.href = '/'; return; }
	if (state.needs_token) {
		$('#claim-missing').classList.toggle('hidden', state.token_present);
		const token = new URLSearchParams(location.search).get('token');
		if (token) $('#claim-token').value = token;
		show('claim');
		return;
	}
	const session = await api('/api/session');
	if (!session.authenticated) {
		// Токен потрачен, а сессии нет: дальше только обычным входом.
		document.querySelectorAll('.wizard-step').forEach(node => node.classList.add('hidden'));
		$('#step-login').classList.remove('hidden');
		$('#step-counter').textContent = '';
		return;
	}
	csrf = session.csrf || '';
	await resume();
}

async function resume() {
	const state = await api('/api/setup/state');
	await loadSummary();
	const step = STEPS.includes(state.step) && state.step !== 'claim' ? state.step : 'access';
	if (step === 'version' || step === 'server' || step === 'create') await loadCatalogue(false);
	if (step === 'server') prepareServerForm();
	if (step === 'create') {
		const record = summary.provision && (summary.provision.current || (summary.provision.history || [])[0]);
		if (summary.draft && summary.draft.server) describeCreation(summary.draft.server);
		renderRun(record);
		if (record && record.status === 'running') watchProgress();
	}
	show(step);
}

boot();
