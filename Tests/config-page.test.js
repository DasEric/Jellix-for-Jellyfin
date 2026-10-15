'use strict';

const fs = require('fs');

let asynchronousFailure = null;
let savedConfiguration = null;
let lastAlert = null;
process.on('unhandledRejection', function (error) { asynchronousFailure = error; });

class Element {
  constructor() {
    this.value = '';
    this.checked = false;
    this.disabled = false;
    this.placeholder = '';
    this.children = [];
    this.handlers = {};
    this.text = '';
    this.className = '';
  }

  set textContent(value) {
    this.text = String(value);
    if (value === '') this.children = [];
  }

  get textContent() { return this.text; }

  addEventListener(name, handler) { this.handlers[name] = handler; }

  appendChild(child) { this.children.push(child); }

  setAttribute() {}
}

const elements = new Map();
function element(id) {
  if (!elements.has(id)) elements.set(id, new Element());
  return elements.get(id);
}

const page = element('page');
page.querySelector = function (selector) {
  return selector === '#jellixLinkForm button[type="submit"]'
    ? element('linkSubmit')
    : element(selector.replace(/^#/, ''));
};
page.addEventListener = function () {};

global.document = {
  querySelector: function () { return page; },
  createElement: function () { return new Element(); }
};
global.window = { alert: function (message) { lastAlert = message; }, confirm: function () { return true; } };
global.Dashboard = {
  showLoadingMsg: function () {},
  hideLoadingMsg: function () {},
  processPluginConfigurationUpdateResult: function () {}
};
global.ApiClient = {
  getPluginConfiguration: async function () { return {}; },
  getUsers: async function () {
    return [{ Id: '11111111-1111-1111-1111-111111111111', Name: 'Alice' }];
  },
  getUrl: function (value) { return value; },
  updatePluginConfiguration: async function (id, config) { savedConfiguration = config; return {}; },
  ajax: async function (request) {
    if (request.url.includes('Admin/Token')) return { Configured: true };
    if (request.url.includes('Admin/Diagnostics')) {
      return { DiscordReady: false, H0melabInstalled: true, H0melabBridgeAvailable: true, H0melabVersion: '1.3.2', PendingNotifications: 0, ConfigurationIssues: [], MediaLibraries: [{ Name: 'Series', Id: '11111111111111111111111111111111' }] };
    }
    if (request.url.includes('Admin/Links')) {
      return [{ GuildId: '123456789012345678', DiscordUserId: '223456789012345678', JellyfinUserId: '11111111111111111111111111111111' }];
    }
    if (request.url.includes('Admin/Audit')) {
      return [{ CreatedUtc: '2026-08-16T00:00:00Z', Action: 'test', Success: true, ActorType: 'test', ActorId: '1', Details: '' }];
    }
    return {};
  }
};

const script = fs.readFileSync('Jellyfin.Plugin.Jellix/Web/config.js', 'utf8');
eval(script);

setTimeout(async function () {
  try {
  const links = element('jellixLinks');
  const label = links.children[0]?.children[0]?.textContent;
  if (asynchronousFailure || links.children.length !== 1 || !label?.includes('Alice')) {
    console.error(asynchronousFailure || 'The mixed-case account link was not rendered.');
    process.exit(1);
  }

  console.log('PASS config page accepts mixed-case Jellyfin API fields');
  if (element('SeriesQuietSeconds').value !== 120 || element('SeriesMergeHours').value !== 24 || element('LibraryOverviewMinutes').value !== 5 || !element('jellixLibraries').textContent.includes('Series')) {
    throw new Error('Library defaults or selection hints are missing.');
  }
  element('LibraryOverviewEnabled').checked = true;
  element('H0melabEnabled').checked = true;
  element('H0melabPollSeconds').value = 90;
  if (!element('jellixStatus').textContent.includes('H0melab-Connector: bereit (1.3.2)')) throw new Error('Connector diagnostics are missing.');
  element('LibraryOverviewChannelId').value = '123456789012345678';
  element('LibraryIds').value = '11111111111111111111111111111111';
  element('MediaStoragePaths').value = '/media/movies\n/media/series';
  await element('jellixConfigForm').handlers.submit({ preventDefault: function () {} });
  if (lastAlert || !savedConfiguration?.LibraryOverviewEnabled || savedConfiguration.SeriesQuietSeconds !== 120 || savedConfiguration.MediaStoragePaths !== '/media/movies\n/media/series') {
    throw new Error('Library settings were not saved correctly: ' + lastAlert);
  }
  if (!savedConfiguration.H0melabEnabled || savedConfiguration.H0melabPollSeconds !== 90) throw new Error('H0melab configuration was not saved.');
  savedConfiguration = null;
  element('LibraryOverviewEnabled').checked = true;
  element('LibraryOverviewChannelId').value = '';
  await element('jellixConfigForm').handlers.submit({ preventDefault: function () {} });
  if (!lastAlert || savedConfiguration) throw new Error('Overview channel validation was bypassed.');
  console.log('PASS config page saves library settings and validates required channel');
  } catch (error) { console.error(error); process.exit(1); }
}, 100);
