'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const staticRoot = path.resolve(__dirname, '../../src/Panel.Web/Static');
const issued = {token: 'synthetic-admin-secret', summary: {name: 'monitor'}};

function deferred() {
	let resolve;
	const promise = new Promise(done => { resolve = done; });
	return {promise, resolve};
}

function setup(api = async (_, options) => options?.method === 'POST' ? issued : {tokens: [], totp_enabled: false}) {
	const elements = new Map(), listeners = new Map();
	function $(selector) {
		if (!elements.has(selector)) {
			const element = {
				value: '', className: 'hidden', selectedOptions: [{disabled: false}], handlers: {},
				classList: {add() {}, remove() {}, toggle() {}},
				addEventListener(event, action) { this.handlers[event] = action; },
				focus() {}, select() {},
			};
			let html = '';
			Object.defineProperties(element, {
				innerHTML: {get: () => html, set: value => { html = value; }},
				textContent: {get: () => html, set: value => { html = value; }},
			});
			elements.set(selector, element);
		}
		return elements.get(selector);
	}
	const state = {user: 'admin', role: 'admin', csrf: 'session-a'};
	const emit = (event, payload) => (listeners.get(event) || []).forEach(action => action(payload));
	const context = {
		$, state, emit, releaseAll() {}, I18N: {t: key => key},
		VS: {
			$, $$: () => [], state, api, esc: value => value, fmtBytes: value => value, fmtDateTime: value => value,
			toast() {}, fail(error) { throw error; }, busy: (_, action) => action(), dialog() {},
			can: () => false, roleTitles: {}, pages: {},
			on(event, action) { listeners.set(event, [...(listeners.get(event) || []), action]); },
		},
	};
	vm.createContext(context);
	vm.runInContext(fs.readFileSync(path.join(staticRoot, 'settings.js'), 'utf8'), context);
	const core = fs.readFileSync(path.join(staticRoot, 'core.js'), 'utf8');
	vm.runInContext(core.slice(core.indexOf('function showLogin()'), core.indexOf('async function showPanel(')), context);
	function submit() {
		$('#token-name').value = 'monitor';
		$('#token-role').value = 'admin';
		$('#token-expires').value = '90';
		$('#token-password').value = 'synthetic-password';
		$('#token-code').value = '123456';
		return $('#token-form').handlers.submit({preventDefault() {}, submitter: {}});
	}
	function login(user = 'viewer') {
		state.user = user;
		state.role = 'viewer';
		state.csrf = 'session-b';
		emit('session', {user, role: 'viewer'});
	}
	return {$, state, submit, login, context, logout: () => vm.runInContext('showLogin()', context)};
}

function assertCleared(ui) {
	assert.equal(ui.$('#token-issued').innerHTML, '');
	assert.equal(ui.$('#token-issued').className, 'hidden');
	for (const selector of ['#token-password', '#token-code', '#token-name']) assert.equal(ui.$(selector).value, '');
}

test('successful issuance displays the secret only in the current session', async () => {
	const ui = setup();
	await ui.submit();
	assert.ok(ui.$('#token-issued').innerHTML.includes(issued.token));
	assert.equal(ui.$('#token-issued').className, 'notice');
});

test('logout removes the secret and confirmation fields before another user logs in', async () => {
	const ui = setup();
	await ui.submit();
	ui.$('#token-password').value = 'entered-again';
	ui.$('#token-code').value = '654321';
	ui.logout();
	assertCleared(ui);
	ui.login();
	ui.context.VS.pages.settings.enter('access');
	await new Promise(resolve => setImmediate(resolve));
	assertCleared(ui);
});

test('a new session clears an already displayed secret even without explicit logout', async () => {
	const ui = setup();
	await ui.submit();
	ui.login();
	assertCleared(ui);
});

test('issuance finishing after logout cannot restore the previous secret', async () => {
	const response = deferred();
	const ui = setup(async (_, options) => options?.method === 'POST' ? response.promise : {tokens: []});
	const submitting = ui.submit();
	ui.logout();
	ui.login();
	response.resolve(issued);
	await submitting;
	assertCleared(ui);
});

test('a changed CSRF identity rejects an issuance reply before the session event arrives', async () => {
	const response = deferred();
	const ui = setup(async (_, options) => options?.method === 'POST' ? response.promise : {tokens: []});
	const submitting = ui.submit();
	ui.state.csrf = 'session-b';
	response.resolve(issued);
	await submitting;
	assert.equal(ui.$('#token-issued').innerHTML, '');
});

test('a token list reply from the old session cannot populate the new session', async () => {
	const response = deferred();
	const ui = setup(async () => response.promise);
	ui.context.VS.pages.settings.enter('access');
	ui.logout();
	ui.login();
	response.resolve({tokens: [{id: 'old', user: 'admin', name: 'private', role: 'admin'}], totp_enabled: true});
	await new Promise(resolve => setImmediate(resolve));
	assert.equal(ui.$('#token-list').innerHTML, '');
});
