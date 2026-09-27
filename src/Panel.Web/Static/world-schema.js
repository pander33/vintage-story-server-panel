'use strict';

// Схема параметров мира (GET /api/world-config/schema): один запрос на загрузку страницы, использует
// её и вкладка «Мир и моды» (стили при создании мира), и вкладка «Настройки» (шаблон нового мира).
// Смена сервера всегда перезагружает страницу (VS.selectInstance), так что живой сброс кэша не нужен.

(() => {
	const {api} = VS;
	let pending = null;

	function loadWorldSchema() {
		if (!pending) {
			pending = api('/api/world-config/schema').catch(error => { pending = null; throw error; });
		}

		return pending;
	}

	VS.loadWorldSchema = loadWorldSchema;
})();
