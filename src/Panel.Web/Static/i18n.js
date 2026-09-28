'use strict';

// Локализация интерфейса. Каталоги locales/en.js и locales/ru.js — обычные скрипты с defer перед
// этим файлом: к выполнению любого скрипта раздела они уже загружены. Интерфейс берёт строки
// из одного набора целиком; одинаковый набор ключей в обоих проверяет PageEndpointsTests.
// Английский — язык по умолчанию (решение владельца 2026-09-27); выбор хранится в localStorage.
// Русский текст в разметке — только то, что видно до apply().

const I18N = (() => {
	const STORAGE_KEY = 'vs-panel-lang';

	function readStoredLang() {
		try {
			const value = localStorage.getItem(STORAGE_KEY);
			return value === 'ru' || value === 'en' ? value : null;
		} catch { return null; }
	}

	const lang = readStoredLang() || 'en';

	// Сервер пишет свои сообщения (отказы, статусы, этапы, события) на языке из этой cookie
	// (PhraseCatalog): её получают и fetch, и EventSource, которому заголовок не задать. Ответы API
	// приходят уже переведёнными; фронтенд переводит только собственные строки.
	document.cookie = `vs_lang=${lang}; path=/; max-age=31536000; SameSite=Strict`;
	const strings = (globalThis.I18N_CATALOGS || {})[lang] || {};

	function lookup(key) {
		let node = strings;
		for (const part of key.split('.')) node = node?.[part];
		return typeof node === 'string' ? node : undefined;
	}

	function fill(text, vars) {
		if (!vars) return text;
		return text.replace(/\{(\w+)\}/g, (match, name) => (name in vars ? String(vars[name]) : match));
	}

	/** Строка текущего языка по ключу; сам ключ, если его нет в каталоге. */
	function t(key, vars) {
		return fill(lookup(key) ?? key, vars);
	}

	const ATTR_MAP = {
		'data-i18n': 'textContent',
		'data-i18n-placeholder': 'placeholder',
		'data-i18n-title': 'title',
		'data-i18n-aria-label': 'aria-label',
		'data-i18n-value': 'value',
	};

	/** Расставляет переводы по всем элементам с data-i18n* внутри root. */
	function apply(root = document) {
		for (const [attr, target] of Object.entries(ATTR_MAP)) {
			for (const element of root.querySelectorAll(`[${attr}]`)) {
				const value = t(element.getAttribute(attr));
				if (target === 'textContent' || target === 'value') element[target] = value;
				else element.setAttribute(target, value);
			}
		}
	}

	function getLang() { return lang; }

	function setLang(next) {
		if ((next !== 'en' && next !== 'ru') || next === lang) return;
		try { localStorage.setItem(STORAGE_KEY, next); } catch { /* приватный режим */ }
		location.reload();
	}

	function locale() { return lang === 'en' ? 'en-US' : 'ru-RU'; }

	document.documentElement.lang = lang;
	apply(document);

	return {t, apply, getLang, setLang, locale};
})();
