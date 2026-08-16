(function () {
    'use strict';
    const page = document.querySelector('#JellixLinkPage');
    const heading = page.querySelector('#jellixLinkHeading');
    const description = page.querySelector('#jellixLinkDescription');
    const create = page.querySelector('#jellixCreateCode');
    const unlink = page.querySelector('#jellixUnlink');
    const target = page.querySelector('#jellixCodeResult');
    let language = 'de';

    function text(german, english) { return language === 'en' ? english : german; }

    async function loadStatus() {
        try {
            const status = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Jellix/Status'), dataType: 'json' });
            language = status.language || status.Language || 'de';
            const linked = status.discordLinked === true || status.DiscordLinked === true;
            heading.textContent = linked ? text('Discord Verbunden', 'Discord Connected') : text('Discord Verbinden', 'Connect Discord');
            description.textContent = linked
                ? text('Dein Jellyfin-Konto ist aktuell mit Discord verbunden.', 'Your Jellyfin account is currently linked to Discord.')
                : text('Erzeuge einen einmaligen Code und gib ihn anschließend in Discord ein.', 'Create a one-time code and enter it in Discord.');
            create.style.display = linked ? 'none' : '';
            unlink.style.display = linked ? '' : 'none';
        } catch (error) {
            target.textContent = 'Jellix status unavailable.';
        }
    }

    create.addEventListener('click', async function () {
        target.textContent = '';
        try {
            const response = await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Jellix/LinkCode'), dataType: 'json' });
            target.textContent = response.code + ' – ' + text('gültig bis ', 'valid until ') + new Date(response.expiresUtc).toLocaleTimeString();
        } catch (error) {
            target.textContent = text('Der Code konnte nicht erzeugt werden.', 'The code could not be created.');
        }
    });

    unlink.addEventListener('click', async function () {
        if (!window.confirm(text('Discord-Verknüpfung wirklich aufheben?', 'Are you sure you want to disconnect Discord?'))) return;
        target.textContent = '';
        try {
            await ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('Jellix/Link') });
            await loadStatus();
        } catch (error) {
            target.textContent = text('Die Verknüpfung konnte nicht aufgehoben werden.', 'The connection could not be removed.');
        }
    });

    page.addEventListener('viewshow', loadStatus);
    loadStatus();
}());
