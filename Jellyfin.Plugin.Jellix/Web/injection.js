(function () {
  'use strict';
  const entryId = 'jellix-link-sidebar';
  const modalId = 'jellix-link-modal';
  let language = 'de';
  let enabled = false;
  function text(german, english) { return language === 'en' ? english : german; }
  function client() { return typeof ApiClient !== 'undefined' ? ApiClient : window.ApiClient; }
  function inject() {
    const existing = document.getElementById(entryId);
    if (!enabled) { if (existing) existing.remove(); return; }
    if (existing || !client()) return;
    const sidebar = document.querySelector('.mainDrawer-scrollContainer, .mainDrawer .scrollContainer');
    if (!sidebar) return;
    const entry = document.createElement('a'); entry.id = entryId; entry.href = '#'; entry.setAttribute('is', 'emby-linkbutton'); entry.className = 'navMenuOption lnkMediaFolder';
    entry.innerHTML = '<span class="material-icons navMenuOptionIcon link" aria-hidden="true"></span>';
    const label = document.createElement('span'); label.className = 'navMenuOptionText'; label.textContent = text('Discord verbinden', 'Connect Discord'); entry.appendChild(label);
    entry.addEventListener('click', function (event) { event.preventDefault(); const backdrop = document.querySelector('.mainDrawer-backdrop'); if (backdrop) backdrop.click(); openModal(); });
    const custom = sidebar.querySelector('.customMenuOptions'); const admin = sidebar.querySelector('.adminMenuOptions');
    if (custom) custom.appendChild(entry); else if (admin) sidebar.insertBefore(entry, admin); else sidebar.appendChild(entry);
  }
  function openModal() {
    const previous = document.getElementById(modalId); if (previous) previous.remove();
    const overlay = document.createElement('div'); overlay.id = modalId; overlay.style.cssText = 'position:fixed;inset:0;z-index:9999;background:rgba(0,0,0,.75);display:flex;align-items:center;justify-content:center;padding:1rem';
    const card = document.createElement('div'); card.style.cssText = 'width:min(32rem,100%);background:#202020;color:#fff;padding:1.5rem;border-radius:.5rem;box-shadow:0 1rem 3rem #000';
    const heading = document.createElement('h2'); heading.textContent = text('Discord verbinden', 'Connect Discord');
    const info = document.createElement('p'); info.textContent = text('Erzeuge einen einmaligen Code und gib ihn anschließend in Discord mit /verbinden ein.', 'Create a one-time code and enter it in Discord using /link.');
    const result = document.createElement('p'); result.style.cssText = 'font-size:1.5rem;font-weight:bold;letter-spacing:.08em;min-height:2rem';
    const create = document.createElement('button'); create.type = 'button'; create.textContent = text('Code erzeugen', 'Create code'); create.style.cssText = 'padding:.7rem 1rem;margin-right:.5rem;cursor:pointer';
    const close = document.createElement('button'); close.type = 'button'; close.textContent = text('Schließen', 'Close'); close.style.cssText = 'padding:.7rem 1rem;cursor:pointer'; close.onclick = function () { overlay.remove(); };
    create.onclick = async function () {
      create.disabled = true; result.textContent = '';
      try {
        const response = await client().ajax({ type: 'POST', url: client().getUrl('Jellix/LinkCode'), dataType: 'json' });
        result.textContent = response.code; info.textContent = text('Der Code ist bis ', 'The code is valid until ') + new Date(response.expiresUtc).toLocaleTimeString() + text(' gültig.', '.');
      } catch (error) { result.textContent = text('Code konnte nicht erzeugt werden.', 'The code could not be created.'); }
      finally { create.disabled = false; }
    };
    card.appendChild(heading); card.appendChild(info); card.appendChild(result); card.appendChild(create); card.appendChild(close); overlay.appendChild(card); document.body.appendChild(overlay);
  }
  async function start() {
    try { const status = await client().ajax({ type: 'GET', url: client().getUrl('Jellix/Status'), dataType: 'json' }); language = status.language || 'de'; enabled = status.selfLinkEnabled === true; } catch (error) { language = 'de'; enabled = false; }
    const observer = new MutationObserver(inject); observer.observe(document.body, { childList: true, subtree: true }); inject();
  }
  let attempts = 0; const timer = setInterval(function () { if (client() || attempts++ > 100) { clearInterval(timer); if (client()) document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', start) : start(); } }, 200);
}());
