const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../wwwroot/admin/i18n.js'), 'utf8');
const markup = fs.readFileSync(path.join(__dirname, '../wwwroot/admin/index.html'), 'utf8');

function load({saved, languages = ['en-US'], blocked = false, nodes = {}} = {}) {
  const storage = new Map(saved ? [['pgdh_language', saved]] : []);
  const document = {documentElement: {lang: 'en'}, querySelectorAll: selector => nodes[selector] || []};
  const window = {dispatchEvent() {}};
  const context = vm.createContext({document, window, navigator: {languages}, Intl, Date, Event,
    localStorage: {getItem: key => {if(blocked) throw Error('Blocked'); return storage.get(key);},
      setItem: (key, value) => {if(blocked) throw Error('Blocked'); storage.set(key, value);}}});
  vm.runInContext(source, context);
  return {i18n: window.PgDataHubI18n, document, storage};
}

test('saved language overrides browser preference and unsupported values fall back safely', () => {
  assert.equal(load({saved: 'en', languages: ['tr-TR']}).i18n.language, 'en');
  assert.equal(load({saved: 'fr', languages: ['tr-TR']}).i18n.language, 'tr');
  assert.equal(load({languages: ['de-DE', 'en-GB']}).i18n.language, 'en');
  const {i18n, document} = load({blocked: true, languages: ['tr-TR']});
  assert.equal(i18n.t('Save role'), 'Rolü kaydet');
  i18n.setLanguage('en');
  assert.equal(document.documentElement.lang, 'en');
  assert.equal(i18n.t('Save role'), 'Save role');
});

test('switching language updates only annotated UI and keeps draft and user data intact', () => {
  const label = {dataset: {i18n: 'Role name'}, textContent: 'Role name'};
  const draft = {value: 'Create role', textContent: 'User content: Save role'};
  const select = {value: 'en'};
  const {i18n, storage} = load({nodes: {'[data-i18n]': [label], '[data-language-select]': [select]}});
  i18n.setLanguage('tr');
  assert.equal(label.textContent, 'Rol adı');
  assert.equal(draft.value, 'Create role');
  assert.equal(draft.textContent, 'User content: Save role');
  assert.equal(select.value, 'tr');
  assert.equal(storage.get('pgdh_language'), 'tr');
  i18n.setLanguage('en');
  assert.equal(label.textContent, 'Role name');
});

test('messages preserve identifiers and translate validation errors and number/date presentation', () => {
  const {i18n} = load({saved: 'tr'});
  assert.equal(i18n.t('Delete role "{name}"?', {name: 'My English role'}), '"My English role" rolü silinsin mi?');
  assert.equal(i18n.t('database.port must be between 1 and 65535.'), 'database.port, 1 ile 65535 arasında olmalıdır.');
  assert.equal(i18n.t("Unknown role 'readonly'."), "'readonly' rolü bulunamadı.");
  assert.equal(i18n.t('public.orders'), 'public.orders');
  assert.equal(i18n.t('__proto__'), '__proto__');
  assert.equal(i18n.t('constructor', {count: 1}), 'constructor');
  assert.equal(i18n.t('{count} selected', {count: 1234}), '1.234 seçildi');
  const turkishDate = i18n.formatDate('2026-11-04T12:00:00Z');
  i18n.setLanguage('en');
  assert.notEqual(i18n.formatDate('2026-11-04T12:00:00Z'), turkishDate);
  assert.equal(i18n.t('{count} selected', {count: 1234}), '1,234 selected');
  assert.equal(i18n.t('{count} row rules', {count: 1}), '1 row rule');
});

test('unknown server diagnostics use localized fallback text instead of leaking implementation details', () => {
  const {i18n} = load({saved: 'tr'});
  assert.equal(i18n.t(i18n.responseError({error: 'Invalid administrator credential.'}, 401)), 'Yönetici anahtarı geçersiz.');
  assert.equal(i18n.t(i18n.responseError({detail: 'Internal database exception'}, 500)), 'İstek tamamlanamadı.');
  assert.equal(i18n.t(i18n.responseError(null, 429)), 'Çok fazla istek gönderildi. Biraz sonra yeniden deneyin.');
});

test('all annotated static messages and dynamic UI messages have Turkish translations', () => {
  const {i18n} = load();
  const decode = value => value.replace(/&quot;/g, '"').replace(/&#x27;|&#39;/g, "'").replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>');
  const keys = [...markup.matchAll(/data-i18n(?:-placeholder|-aria-label)?="([^"]+)"/g)].map(m => decode(m[1]));
  const script = fs.readFileSync(path.join(__dirname, '../wwwroot/admin/admin.js'), 'utf8');
  keys.push(...[...script.matchAll(/\b(?:ui|notify|t)\('([^']+)'/g)].map(m => m[1]));
  for (const key of keys) assert.ok(Object.hasOwn(i18n.messages, key), 'Missing translation: ' + key);
});
