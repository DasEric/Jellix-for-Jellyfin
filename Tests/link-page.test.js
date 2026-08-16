'use strict';

const fs = require('fs');

class Element {
  constructor() {
    this.textContent = '';
    this.style = {};
    this.handlers = {};
  }

  addEventListener(name, handler) { this.handlers[name] = handler; }
}

const elements = new Map();
function element(id) {
  if (!elements.has(id)) elements.set(id, new Element());
  return elements.get(id);
}

const page = element('page');
page.querySelector = function (selector) { return element(selector.replace(/^#/, '')); };
page.addEventListener = function () {};

global.document = { querySelector: function () { return page; } };
global.window = { confirm: function () { return true; } };

let linked = true;
let deleteCalls = 0;
global.ApiClient = {
  getUrl: function (value) { return value; },
  ajax: async function (request) {
    if (request.type === 'DELETE' && request.url === 'Jellix/Link') {
      deleteCalls++;
      linked = false;
      return {};
    }
    if (request.url === 'Jellix/Status') return { Language: 'en', DiscordLinked: linked };
    return { code: '7F3K-92MX', expiresUtc: '2026-08-16T12:00:00Z' };
  }
};

const script = fs.readFileSync('Jellyfin.Plugin.Jellix/Web/link.js', 'utf8');
eval(script);

setTimeout(async function () {
  const create = element('jellixCreateCode');
  const unlink = element('jellixUnlink');
  if (element('jellixLinkHeading').textContent !== 'Discord Connected' || create.style.display !== 'none' || unlink.style.display !== '') {
    console.error('Linked state was not rendered correctly.');
    process.exit(1);
  }

  await unlink.handlers.click();
  if (deleteCalls !== 1 || create.style.display !== '' || unlink.style.display !== 'none') {
    console.error('Confirmed unlink did not update the UI.');
    process.exit(1);
  }

  console.log('PASS link page renders and removes Discord connections');
}, 50);
