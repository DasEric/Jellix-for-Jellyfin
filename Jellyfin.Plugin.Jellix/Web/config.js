(function () {
  'use strict';
  const pluginId = 'bea64f51-00f3-4535-8fd3-88bcd2785f24';
  const page = document.querySelector('#JellixConfigPage');
  const booleans = ['BotEnabled','SelfLinkEnabled','PasswordChangeEnabled','RevokeSessionsAfterPasswordChange','UnlockAccountEnabled','UserPageEnabled','StatisticsEnabled','LeaderboardEnabled','AchievementsEnabled','AchievementFilmFanEnabled','AchievementCineasteEnabled','AchievementSeriesJunkieEnabled','AchievementNightOwlEnabled','AchievementBingeWatcherEnabled','AchievementNoLifeEnabled','MediaForgeEnabled','NewMediaNotificationsEnabled','NewEpisodeNotificationsEnabled','NowPlayingShowUsernames','RandomEnabled','AccessRequestsEnabled','AssignStreamingRoleAfterApproval','StickyEnabled','AdminAlertsEnabled','CheckJellyfinUpdates'];
  const numbers = ['LinkCodeLifetimeMinutes','PasswordTicketLifetimeMinutes','CompletedPlaybackPercent','MediaForgePollSeconds','AccessRequestCooldownHours','StickyDebounceSeconds','HealthCheckMinutes','AuditRetentionDays'];
  const strings = ['GuildId','JellyfinPublicUrl','Language','StreamingRoleId','RequestRoleId','AdminRoleId','AchievementChannelId','AchievementNotificationMode','TimeZoneId','RequestNotificationMode','RequestNotificationChannelId','NewMediaChannelId','NowPlayingMode','AccessRequestChannelId','AdminAlertChannelId'];
  let users = [];

  function loading(value) {
    if (typeof Dashboard === 'undefined') return;
    value ? Dashboard.showLoadingMsg() : Dashboard.hideLoadingMsg();
  }

  function validateSnowflake(value, label, required) {
    if (!value && !required) return;
    if (!/^\d{15,22}$/.test(value)) throw new Error(label + ' ist keine gültige Discord-ID.');
  }

  function validatePublicUrl(value) {
    if (!value) return;
    let parsed;
    try { parsed = new URL(value); } catch (error) { throw new Error('Die öffentliche Jellyfin-URL ist ungültig.'); }
    const localHttp = parsed.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(parsed.hostname);
    if (parsed.protocol !== 'https:' && !localHttp) throw new Error('Die öffentliche Jellyfin-URL muss HTTPS verwenden.');
    if (parsed.username || parsed.password || parsed.search || parsed.hash) throw new Error('Die öffentliche Jellyfin-URL darf keine Zugangsdaten, Abfrage oder Fragment enthalten.');
  }

  async function load() {
    loading(true);
    try {
      const results = await Promise.all([
        ApiClient.getPluginConfiguration(pluginId),
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Jellix/Admin/Token'), dataType: 'json' }),
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Jellix/Admin/Diagnostics'), dataType: 'json' }),
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Jellix/Admin/Links'), dataType: 'json' }),
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Jellix/Admin/Audit?limit=100'), dataType: 'json' }),
        ApiClient.getUsers()
      ]);
      const config = results[0];
      booleans.forEach(function (key) { page.querySelector('#' + key).checked = !!config[key]; });
      numbers.forEach(function (key) { page.querySelector('#' + key).value = config[key]; });
      strings.forEach(function (key) { page.querySelector('#' + key).value = config[key] || ''; });
      page.querySelector('#DiscordToken').placeholder = results[1].configured ? 'Gespeichert' : 'Noch nicht gesetzt';
      users = results[5] || [];
      renderStatus(results[2]);
      renderUsers();
      renderLinks(results[3]);
      renderAudit(results[4]);
    } finally {
      loading(false);
    }
  }

  function renderStatus(status) {
    const target = page.querySelector('#jellixStatus');
    const bridge = status.mediaForgeBridgeAvailable ? 'bereit' : (status.mediaForgeInstalled ? 'Änderung erforderlich' : 'nicht installiert');
    target.textContent = 'Discord: ' + (status.discordReady ? 'verbunden' : 'offline') +
      ' · MediaForge: ' + bridge + (status.mediaForgeVersion ? ' (' + status.mediaForgeVersion + ')' : '') +
      ' · Wartende Meldungen: ' + status.pendingNotifications +
      (status.configurationIssues && status.configurationIssues.length ? ' · Hinweise: ' + status.configurationIssues.join(' ') : '');
  }

  function renderUsers() {
    const select = page.querySelector('#LinkJellyfinUser');
    select.textContent = '';
    users.slice().sort(function (a, b) { return a.Name.localeCompare(b.Name); }).forEach(function (user) {
      const option = document.createElement('option'); option.value = user.Id; option.textContent = user.Name; select.appendChild(option);
    });
  }

  function userName(id) {
    const user = users.find(function (value) { return value.Id.replace(/-/g, '').toLowerCase() === id.replace(/-/g, '').toLowerCase(); });
    return user ? user.Name : id;
  }

  function renderLinks(links) {
    const target = page.querySelector('#jellixLinks'); target.textContent = '';
    if (!links.length) { target.textContent = 'Noch keine Konten zugewiesen.'; return; }
    links.forEach(function (link) {
      const row = document.createElement('div'); row.className = 'listItem';
      const text = document.createElement('div'); text.className = 'listItemBody'; text.textContent = userName(link.jellyfinUserId) + ' ↔ Discord ' + link.discordUserId;
      const button = document.createElement('button'); button.setAttribute('is', 'emby-button'); button.type = 'button'; button.textContent = 'Lösen';
      button.addEventListener('click', async function () {
        if (!window.confirm('Diese Verknüpfung lösen?')) return;
        await ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('Jellix/Admin/Links/' + encodeURIComponent(link.guildId) + '/' + encodeURIComponent(link.discordUserId)) });
        await load();
      });
      row.appendChild(text); row.appendChild(button); target.appendChild(row);
    });
  }

  function renderAudit(records) {
    const target = page.querySelector('#jellixAudit'); target.textContent = '';
    if (!records.length) { target.textContent = 'Noch keine Einträge.'; return; }
    records.forEach(function (record) {
      const row = document.createElement('div'); row.className = 'listItem';
      const body = document.createElement('div'); body.className = 'listItemBody';
      const title = document.createElement('div'); title.textContent = new Date(record.createdUtc).toLocaleString() + ' · ' + record.action + (record.success ? '' : ' · fehlgeschlagen');
      const detail = document.createElement('div'); detail.className = 'secondary'; detail.textContent = record.actorType + ': ' + record.actorId + (record.details ? ' · ' + record.details : '');
      body.appendChild(title); body.appendChild(detail); row.appendChild(body); target.appendChild(row);
    });
  }

  page.querySelector('#jellixConfigForm').addEventListener('submit', async function (event) {
    event.preventDefault(); loading(true);
    try {
      const config = await ApiClient.getPluginConfiguration(pluginId);
      booleans.forEach(function (key) { config[key] = page.querySelector('#' + key).checked; });
      numbers.forEach(function (key) { config[key] = Number(page.querySelector('#' + key).value); });
      strings.forEach(function (key) { config[key] = page.querySelector('#' + key).value.trim(); });
      validateSnowflake(config.GuildId, 'Discord-Server-ID', config.BotEnabled);
      ['StreamingRoleId','RequestRoleId','AdminRoleId','AchievementChannelId','RequestNotificationChannelId','NewMediaChannelId','AccessRequestChannelId','AdminAlertChannelId'].forEach(function (key) { validateSnowflake(config[key], key, false); });
      validatePublicUrl(config.JellyfinPublicUrl);
      const token = page.querySelector('#DiscordToken').value.trim();
      const result = await ApiClient.updatePluginConfiguration(pluginId, config);
      if (typeof Dashboard !== 'undefined') Dashboard.processPluginConfigurationUpdateResult(result);
      if (token) {
        await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Jellix/Admin/Token'), contentType: 'application/json', data: JSON.stringify({ token: token }) });
        page.querySelector('#DiscordToken').value = '';
      }
      await load();
    } catch (error) {
      window.alert(error.message || 'Die Einstellungen konnten nicht gespeichert werden.');
    } finally { loading(false); }
  });

  page.querySelector('#jellixLinkForm').addEventListener('submit', async function (event) {
    event.preventDefault(); loading(true);
    try {
      const guildId = page.querySelector('#GuildId').value.trim();
      const discordUserId = page.querySelector('#LinkDiscordUser').value.trim();
      validateSnowflake(guildId, 'Discord-Server-ID', true); validateSnowflake(discordUserId, 'Discord-Benutzer-ID', true);
      await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Jellix/Admin/Links'), contentType: 'application/json', data: JSON.stringify({ guildId: guildId, discordUserId: discordUserId, jellyfinUserId: page.querySelector('#LinkJellyfinUser').value }) });
      page.querySelector('#LinkDiscordUser').value = ''; await load();
    } catch (error) { window.alert(error.message || 'Die Zuweisung konnte nicht gespeichert werden.'); }
    finally { loading(false); }
  });

  page.querySelector('#DeleteDiscordToken').addEventListener('click', async function () {
    if (!window.confirm('Das gespeicherte Discord-Token wirklich löschen?')) return;
    loading(true);
    try { await ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('Jellix/Admin/Token') }); await load(); }
    finally { loading(false); }
  });

  page.addEventListener('viewshow', load);
  load();
}());
