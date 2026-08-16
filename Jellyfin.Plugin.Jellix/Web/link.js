(function () {
    'use strict';
    const page = document.querySelector('#JellixLinkPage');
    page.querySelector('#jellixCreateCode').addEventListener('click', async function () {
        const target = page.querySelector('#jellixCodeResult');
        target.textContent = '';
        try {
            const response = await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Jellix/LinkCode'), dataType: 'json' });
            target.textContent = response.code + ' – gültig bis ' + new Date(response.expiresUtc).toLocaleTimeString();
        } catch (error) {
            target.textContent = 'Der Code konnte nicht erzeugt werden.';
        }
    });
}());

