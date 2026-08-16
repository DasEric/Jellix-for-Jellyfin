'use strict';

const fs = require('fs');

const byId = new Map();
class Element {
  constructor(tag) {
    this.tag = tag;
    this.children = [];
    this.handlers = {};
    this.className = '';
    this.style = {};
    this.textContent = '';
    this.parent = null;
    this._id = '';
  }

  set id(value) { this._id = value; if (value) byId.set(value, this); }
  get id() { return this._id; }
  appendChild(child) { child.parent = this; this.children.push(child); }
  insertBefore(child) { this.appendChild(child); }
  addEventListener(name, handler) { this.handlers[name] = handler; }
  setAttribute() {}
  remove() { if (this.id) byId.delete(this.id); if (this.parent) this.parent.children = this.parent.children.filter((value) => value !== this); }
  querySelector(selector) {
    if (selector === '.navMenuOptionText') return this.children.find((value) => value.className === 'navMenuOptionText') || null;
    if (selector === '.customMenuOptions') return this.custom || null;
    if (selector === '.adminMenuOptions') return null;
    return null;
  }
}

const body = new Element('body');
const sidebar = new Element('sidebar');
const custom = new Element('custom');
sidebar.custom = custom;
sidebar.appendChild(custom);

global.document = {
  body: body,
  readyState: 'complete',
  createElement: function (tag) { return new Element(tag); },
  getElementById: function (id) { return byId.get(id) || null; },
  querySelector: function (selector) {
    if (selector.includes('.mainDrawer-scrollContainer')) return sidebar;
    return null;
  },
  addEventListener: function () {}
};
global.MutationObserver = class { constructor(handler) { this.handler = handler; } observe() {} };
global.setInterval = function (handler) { setTimeout(handler, 0); return 1; };
global.clearInterval = function () {};

let deleteCalls = 0;
global.window = {
  ApiClient: null,
  addEventListener: function () {},
  setInterval: function () {},
  confirm: function () { return true; }
};
global.ApiClient = {
  getUrl: function (value) { return value; },
  ajax: async function (request) {
    if (request.type === 'DELETE') { deleteCalls++; return {}; }
    return { Language: 'en', SelfLinkEnabled: true, DiscordLinked: true };
  }
};

eval(fs.readFileSync('Jellyfin.Plugin.Jellix/Web/injection.js', 'utf8'));

setTimeout(async function () {
  const entry = byId.get('jellix-link-sidebar');
  const label = entry?.querySelector('.navMenuOptionText');
  if (!entry || label?.textContent !== 'Discord Connected') {
    console.error('Injected navigation did not show the connected state.');
    process.exit(1);
  }

  entry.handlers.click({ preventDefault: function () {} });
  const overlay = byId.get('jellix-link-modal');
  const primary = overlay?.children[0]?.children[3];
  if (!primary || primary.textContent !== 'Disconnect Discord') {
    console.error('Connected modal did not offer unlinking.');
    process.exit(1);
  }

  await primary.onclick();
  if (deleteCalls !== 1 || label.textContent !== 'Connect Discord') {
    console.error('Confirmed injected unlink did not update the navigation.');
    process.exit(1);
  }

  console.log('PASS injected navigation renders and removes Discord connections');
}, 50);
