(function () {
    'use strict';
    const form = document.getElementById('passwordForm');
    const first = document.getElementById('newPassword');
    const second = document.getElementById('confirmPassword');
    const button = document.getElementById('submitButton');
    const message = document.getElementById('message');
    const english = new URLSearchParams(window.location.search).get('lang') === 'en';
    function text(german, translated) { return english ? translated : german; }
    document.documentElement.lang = english ? 'en' : 'de';
    document.getElementById('pageTitle').textContent = text('Jellyfin-Passwort ändern', 'Change Jellyfin password');
    document.getElementById('pageInfo').textContent = text('Dieser Link ist nur einmal und für kurze Zeit gültig.', 'This link is valid once and for a short time.');
    document.getElementById('newPasswordLabel').textContent = text('Neues Passwort', 'New password');
    document.getElementById('confirmPasswordLabel').textContent = text('Passwort wiederholen', 'Repeat password');
    button.textContent = text('Passwort ändern', 'Change password');
    let token = '';
    try { token = decodeURIComponent(window.location.hash.slice(1)); } catch (error) { token = ''; }
    history.replaceState(null, '', window.location.pathname);

    function show(text, success) {
        message.textContent = text;
        message.className = 'message ' + (success ? 'success' : 'error');
    }

    if (!token) {
        form.hidden = true;
        show(text('Dieser Link ist ungültig oder abgelaufen.', 'This link is invalid or expired.'), false);
        return;
    }

    form.addEventListener('submit', async function (event) {
        event.preventDefault();
        if (first.value !== second.value) {
            show(text('Die Passwörter stimmen nicht überein.', 'The passwords do not match.'), false);
            return;
        }

        button.disabled = true;
        try {
            const response = await fetch('Password', {
                method: 'POST',
                cache: 'no-store',
                credentials: 'omit',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ token: token, newPassword: first.value, confirmPassword: second.value })
            });
            first.value = '';
            second.value = '';
            await response.json().catch(function () { return {}; });
            if (!response.ok) {
                throw new Error(text('Das Passwort konnte nicht geändert werden. Der Link kann abgelaufen sein.', 'The password could not be changed. The link may have expired.'));
            }

            form.hidden = true;
            show(text('Dein Jellyfin-Passwort wurde erfolgreich geändert.', 'Your Jellyfin password was changed successfully.'), true);
        } catch (error) {
            show(error.message || text('Das Passwort konnte nicht geändert werden.', 'The password could not be changed.'), false);
            button.disabled = false;
        }
    });
}());
