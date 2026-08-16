'use strict';

const fs = require('fs');

let asynchronousFailure = null;
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
global.window = { alert: function () {}, confirm: function () { return true; } };
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
  updatePluginConfiguration: async function () { return {}; },
  ajax: async function (request) {
    if (request.url.includes('Admin/Token')) return { Configured: true };
    if (request.url.includes('Admin/Diagnostics')) {
      return { DiscordReady: false, MediaForgeInstalled: false, PendingNotifications: 0, ConfigurationIssues: [] };
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

setTimeout(function () {
  const links = element('jellixLinks');
  const label = links.children[0]?.children[0]?.textContent;
  if (asynchronousFailure || links.children.length !== 1 || !label?.includes('Alice')) {
    console.error(asynchronousFailure || 'The mixed-case account link was not rendered.');
    process.exit(1);
  }

  console.log('PASS config page accepts mixed-case Jellyfin API fields');
}, 100);
