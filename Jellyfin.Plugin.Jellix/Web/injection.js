(function () {
  'use strict';
  const entryId = 'jellix-link-sidebar';
  const modalId = 'jellix-link-modal';
  let language = 'de';
  let enabled = false;
  let linked = false;

  function text(german, english) { return language === 'en' ? english : german; }
  function client() { return typeof ApiClient !== 'undefined' ? ApiClient : window.ApiClient; }
  function linkLabel() { return linked ? text('Discord Verbunden', 'Discord Connected') : text('Discord Verbinden', 'Connect Discord'); }

  function inject() {
    const existing = document.getElementById(entryId);
    if (!enabled) { if (existing) existing.remove(); return; }
    if (existing) {
      const label = existing.querySelector('.navMenuOptionText');
      if (label) {
        const newLabel = linkLabel();
        if (label.textContent !== newLabel) label.textContent = newLabel;
      }
      return;
    }
    if (!client()) return;
    const sidebar = document.querySelector('.mainDrawer-scrollContainer, .mainDrawer .scrollContainer');
    if (!sidebar) return;
    const entry = document.createElement('a');
    entry.id = entryId;
    entry.href = '#';
    entry.setAttribute('is', 'emby-linkbutton');
    entry.className = 'navMenuOption lnkMediaFolder';
    entry.innerHTML = '<span class="material-icons navMenuOptionIcon link" aria-hidden="true"></span>';
    const label = document.createElement('span');
    label.className = 'navMenuOptionText';
    label.textContent = linkLabel();
    entry.appendChild(label);
    entry.addEventListener('click', function (event) {
      event.preventDefault();
      const backdrop = document.querySelector('.mainDrawer-backdrop');
      if (backdrop) backdrop.click();
      openModal();
    });
    const custom = sidebar.querySelector('.customMenuOptions');
    const admin = sidebar.querySelector('.adminMenuOptions');
    if (custom) custom.appendChild(entry); else if (admin) sidebar.insertBefore(entry, admin); else sidebar.appendChild(entry);
  }

  function button(label, danger) {
    const value = document.createElement('button');
    value.type = 'button';
    value.textContent = label;
    value.style.cssText = 'padding:.75rem 1rem;margin-right:.5rem;border:0;border-radius:.35rem;cursor:pointer;color:#fff;background:' + (danger ? '#c0392b' : '#00a4dc');
    return value;
  }

  function openModal() {
    const previous = document.getElementById(modalId); if (previous) previous.remove();
    const overlay = document.createElement('div');
    overlay.id = modalId;
    overlay.style.cssText = 'position:fixed;inset:0;z-index:9999;background:rgba(0,0,0,.78);display:flex;align-items:center;justify-content:center;padding:1rem';
    const card = document.createElement('div');
    card.style.cssText = 'width:min(32rem,100%);background:linear-gradient(145deg,#20242b,#17191e);color:#fff;padding:1.5rem;border-radius:.75rem;border-top:4px solid #00a4dc;box-shadow:0 1rem 3rem #000';
    const heading = document.createElement('h2');
    heading.textContent = linkLabel();
    const info = document.createElement('p');
    const result = document.createElement('p');
    result.style.cssText = 'font-size:1.5rem;font-weight:bold;letter-spacing:.08em;min-height:2rem';
    const primary = linked ? button(text('Verknüpfung aufheben', 'Disconnect Discord'), true) : button(text('Code erzeugen', 'Create code'), false);
    const close = button(text('Schließen', 'Close'), false);
    close.style.background = '#555';
    close.onclick = function () { overlay.remove(); };

    if (linked) {
      info.textContent = text('Dein Jellyfin-Konto ist mit Discord verbunden. Nach dem Trennen funktionieren Konto-, Statistik- und Anfragefunktionen erst wieder nach einer neuen Verknüpfung.', 'Your Jellyfin account is linked to Discord. After disconnecting, account, statistics, and request features require a new link.');
      primary.onclick = async function () {
        if (!window.confirm(text('Discord-Verknüpfung wirklich aufheben?', 'Are you sure you want to disconnect Discord?'))) return;
        primary.disabled = true;
        try {
          await client().ajax({ type: 'DELETE', url: client().getUrl('Jellix/Link') });
          linked = false;
          overlay.remove();
          inject();
        } catch (error) {
          result.textContent = text('Die Verknüpfung konnte nicht aufgehoben werden.', 'The connection could not be removed.');
        } finally { primary.disabled = false; }
      };
    } else {
      info.textContent = text('Erzeuge einen einmaligen Code und gib ihn anschließend in Discord mit /verbinden ein.', 'Create a one-time code and enter it in Discord using /link.');
      primary.onclick = async function () {
        primary.disabled = true; result.textContent = '';
        try {
          const response = await client().ajax({ type: 'POST', url: client().getUrl('Jellix/LinkCode'), dataType: 'json' });
          result.textContent = response.code;
          info.textContent = text('Der Code ist bis ', 'The code is valid until ') + new Date(response.expiresUtc).toLocaleTimeString() + text(' gültig.', '.');
        } catch (error) { result.textContent = text('Code konnte nicht erzeugt werden.', 'The code could not be created.'); }
        finally { primary.disabled = false; }
      };
    }

    card.appendChild(heading); card.appendChild(info); card.appendChild(result); card.appendChild(primary); card.appendChild(close); overlay.appendChild(card); document.body.appendChild(overlay);
  }

  async function refreshStatus() {
    try {
      const status = await client().ajax({ type: 'GET', url: client().getUrl('Jellix/Status'), dataType: 'json' });
      language = status.language || status.Language || 'de';
      linked = status.discordLinked === true || status.DiscordLinked === true;
      enabled = linked || status.selfLinkEnabled === true || status.SelfLinkEnabled === true;
    } catch (error) { language = 'de'; enabled = false; linked = false; }
    inject();
  }

  async function start() {
    await refreshStatus();
    const observer = new MutationObserver(inject);
    observer.observe(document.body, { childList: true, subtree: true });
    window.addEventListener('focus', refreshStatus);
    window.setInterval(refreshStatus, 30000);
  }

  let attempts = 0;
  const timer = setInterval(function () {
    if (client() || attempts++ > 100) {
      clearInterval(timer);
      if (client()) document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', start) : start();
    }
  }, 200);
}());
