# Jellix for Jellyfin

Jellix connects Jellyfin with Discord. It provides secure account tools, persistent playback statistics, achievements, recommendations, notifications, sticky messages, onboarding, administration features, and an optional MediaForge request integration.

For every Discord command, option, permission, visibility rule, and the complete sticky workflow, see the [command guide](COMMANDS.md).

## Requirements

- Jellyfin 12.0 or newer (use a 0.2.x release for Jellyfin 10.11)
- A Discord application with a bot
- HTTPS access to Jellyfin if password-change links are enabled outside the local machine
- MediaForge Requests 0.5.0 or newer for the optional request integration

## Dependencies

Normal Jellyfin users do not need to install Python, `discord.py`, Node.js, npm packages, or a separate Discord service. Jellix is a .NET Jellyfin plugin and uses Discord.Net, not `discord.py`. All required Discord.Net runtime libraries are included in the Jellix release archive. Jellyfin supplies the server APIs and native SQLite provider used by the plugin.

Building Jellix from source requires the .NET 10 SDK. PowerShell 7 is used by the release scripts, and Node.js is used only for JavaScript syntax validation in the automated workflow; neither PowerShell nor Node.js is a runtime dependency of the installed plugin.

## Features

### Accounts and security

- Link one Discord account to one Jellyfin account.
- Create single-use, short-lived linking codes from Jellyfin.
- Store only hashes of linking codes in the database.
- Assign account links manually from the Jellyfin administrator page.
- Change a linked user's Jellyfin password through a single-use browser link.
- Optionally revoke existing Jellyfin sessions after a password change.
- Unlock the linked Jellyfin account after failed login attempts.
- Unlink accounts through Discord or Jellyfin administration.
- Encrypt the Discord bot token outside the regular plugin configuration.
- Keep passwords, bot tokens, API keys, and selection tokens out of Discord messages, audit details, and normal logs.

### Discord account menu

The `/account` command opens a private dashboard with buttons for password changes, personal statistics, Continue Watching, MediaForge requests, achievements, privacy settings, and account unlinking.

### Playback statistics

- Persist actual watched time from the moment Jellix is installed.
- Exclude pauses, backward seeks, stalled playback, and unrealistic timeline jumps.
- Handle playback speed changes, interrupted sessions, repeated views, and concurrent sessions.
- Count watched movies, series, and episodes.
- Show total watch time, the current series, and the most-watched series.
- Provide statistics for today, the current week, month, year, or all recorded time.
- Recover interrupted playback records after a Jellyfin restart.

### Leaderboards and privacy

- Optional public leaderboards for watch time, movies, series, and episodes.
- Per-user controls for leaderboard participation, public names, Now Playing visibility, and achievement announcements.
- Anonymous display for users who participate without publishing their name.

### Achievements

- Film Fan: 50 watched movies
- Cineaste: 250 watched movies
- Series Junkie: 500 watched episodes
- Night Owl: 50 hours watched between midnight and 5 a.m.
- Binge Watcher: 10 episodes watched in one local day
- No Life: 1,000 hours of watch time
- Individually configurable achievements.
- Direct-message, channel, or disabled achievement announcements.
- Persistent unlock and notification state to prevent duplicate announcements.

### Recommendations and playback information

- Show active streams with public, administrator-only, or disabled visibility.
- Optionally hide usernames even when active content is public.
- Recommend a random movie or series.
- Filter by genre, maximum runtime, minimum rating, and unwatched status.
- Show resumable movies and episodes through Continue Watching.

### User onboarding

- Allow Discord users to request Jellyfin access.
- Limit every Discord user to one pending request.
- Apply a configurable cooldown after rejection.
- Send a review embed by direct message to the Discord server owner, with an optional configured channel fallback.
- Let only the Discord server owner approve or reject requests with buttons.
- Allow an optional rejection reason and deliver it to the requesting user.
- Create approved Jellyfin users without administrator permissions.
- Link the new account automatically and optionally assign the streaming role.
- Deliver a secure, single-use password setup link to the new user.

### MediaForge integration

- Detect the optional MediaForge bridge without making MediaForge a required dependency.
- Search for movies and series through Discord.
- Submit opaque, short-lived, user-bound selection tokens.
- Display personal request status and download progress.
- Notify the requesting Discord user by direct message when a request becomes available, fails, or is rejected; personal results are never redirected to a public channel.
- Detect MediaForge availability and invalid API-key states.
- Keep MediaForge's `RequestStore` as the only source of truth. Jellix never reads or modifies `requests.json` and does not maintain a second request database.
- Isolate MediaForge initialization, timeout, response, and compatibility failures from Jellyfin and the Discord bot.

The integration requires the protocol-v1 Jellix bridge included with MediaForge Requests 0.5.0 or newer. Install or update both plugins and restart Jellyfin before enabling it.

### Library notifications

- Announce new movies separately and group TV additions into one editable message per series/import in the configured news channel.
- Show the actual new content, for example **5 seasons (63 episodes)**. Individual episode and season messages are no longer sent.
- Wait for a per-series quiet period (120 seconds by default) and a running full library scan to finish before queueing the summary.
- Merge late arrivals into the same Discord message for 24 hours after the last genuinely new episode (configurable from 1 to 168 hours). Each addition extends this window; rescans do not.
- A later, separate addition after the merge window creates a new grouped announcement. The window is an import heuristic, not proof that a downloader has finished.
- Count only non-virtual local episodes, deduplicate numbered episodes and multi-episode ranges, and distinguish specials from regular seasons. A partially present season is never described as complete.
- Preserve the two existing switches: new media covers movies and new series/season arrivals; new episodes covers additions to an existing series even without a new parent event. Both switches still produce only one TV message.
- Include available movie metadata and movie/series posters. Poster preparation failure does not discard the announcement.
- Create a persistent initial baseline so existing content is not announced as new.
- Persist series membership, import revisions, message IDs and delivery intent. Recover pending items (including more than 1,000 items), additions during downtime, and uncertain Discord sends after a restart.
- Migrate pending pre-0.4 notifications before starting the outbound worker; covered children of pending series/season jobs are recovered for aggregation.

### Persistent library overview and storage

- Enable **Library overview** in the Jellix dashboard and select its Discord channel. It is disabled by default on upgrades.
- Publish one message containing **movies**, **series (total episodes)** and **used / total / free media storage**. Reuse that message across restarts rather than posting on every refresh.
- Refresh after library changes (coalesced) and periodically every five minutes by default. Unchanged snapshots verify that the message still exists without editing it; deleted messages are recreated.
- Use the optional comma-separated **Library IDs** setting to select movie, TV and mixed libraries for the overview, public `/status`, and new-media notifications. The dashboard lists available library names and IDs. Empty selects all applicable libraries; an unknown configured ID does not silently select all libraries.
- Series totals require at least one local episode. Movie versions with the same preferred provider ID count once. These are library totals, not users' watched-content statistics.
- Storage paths default to the selected libraries. Optional overrides accept one absolute directory per line, as visible to the Jellyfin process or container.
- Show filesystem capacity, including other files on that filesystem, rather than repeatedly scanning video files. Units are decimal GB/TB. If quotas apply, the operating system's reported capacity may be quota-limited.
- Resolve Windows volumes/mounted folders and accessible UNC shares. Resolve Linux mount points and deduplicate bind mounts of the same filesystem. Do not use a Docker overlay filesystem as a substitute for media capacity.
- Display unavailable storage explicitly instead of fake zero/free values. A filesystem identity change detected while running remains unavailable until the mount is checked and Jellyfin restarted. Initial startup cannot infer a missing mount if its empty directory lies on an otherwise valid local filesystem; configure and verify the media mounts before starting Jellyfin.
- Keep dynamic overview/series messages separate from static stickies. Their context menu refuses conversion into a static snapshot; ordinary stickies continue to work.
- Require **View Channels**, **Send Messages**, **Embed Links**, and **Read Message History** for managed messages. Without **Attach Files**, series summaries can still be sent without a poster.

Example overview:

```text
📚 Jellyfin-Bibliothek
🎬 1.240 Filme
📺 186 Serien (12.438 Folgen insgesamt)
💾 Medienspeicher 1
1,00 TB von 4,00 TB belegt · 3,00 TB frei (25 %)
```

### Sticky messages

- Mark a Discord message as sticky through its context menu.
- Support one sticky message per channel.
- Remove, inspect, or refresh a sticky through `/sticky`.
- Repost after channel activity using a configurable debounce delay.
- Persist sticky content and state across Jellyfin and bot restarts, and restore it to the newest channel position after reconnecting.
- Prevent deletion-event loops and unnecessary Discord requests.

### Administration and monitoring

- Configure all features from the Jellyfin administrator dashboard.
- Select German or English for Discord commands and messages.
- Configure streaming, request, and administrator roles separately.
- View account links, diagnostics, configuration warnings, queue size, and the audit log.
- Provide public and administrator-specific help commands.
- Show restricted public status information and additional system details to administrators.
- Warn administrators about Discord disconnects, MediaForge failures, invalid MediaForge API keys, failed downloads, failed library scans, and available Jellyfin updates.
- Deliver administrator alerts, including available Jellyfin updates, either by direct message to the Discord server owner or to a configured channel.
- Retain and prune audit records according to the configured retention period.
- Deliver background notifications through a persistent priority queue with retries and deduplication.

### Optional external watchdog

Every release includes a separate PowerShell watchdog. It runs outside Jellyfin and can send a Discord webhook alert when the entire Jellyfin server becomes unreachable or recovers.

## Installation

1. Open **Jellyfin Dashboard → Plugins → Repositories**.
2. Add `https://daseric.github.io/Jellix-for-Jellyfin/manifest.json`.
3. Install Jellix from the plugin catalog and restart Jellyfin.
4. Create an application and bot in the [Discord Developer Portal](https://discord.com/developers/applications).
5. Invite the bot with the `bot` and `applications.commands` scopes.
6. Grant **View Channels**, **Send Messages**, **Embed Links**, **Attach Files**, and **Read Message History**. Stickies additionally require **Manage Messages**. Automatic role assignment requires **Manage Roles**, with the bot role above the assigned role.
7. Open **Jellyfin Dashboard → Plugins → Jellix** and configure the bot token, Discord server ID, roles, channels, language, and desired features.
8. Configure the public Jellyfin URL when password changes or Discord onboarding are enabled.
9. Restart Jellyfin after changing the bot token, Discord server, language, or MediaForge installation.

On Jellyfin 12, Jellix registers a native **Discord** link in Jellyfin Web's
`config.json`. Jellyfin renders it in the Modern desktop app bar, the overflow
menu, and the mobile drawer. Reload already open web clients after installation
or after changing **User page enabled**. Jellix preserves unrelated custom links.
If the web configuration is mounted read-only, add
`{"name":"Discord","icon":"forum","url":"#/jellix-discord"}` to its
`menuLinks` array manually; the server log reports this condition.

Enable Discord's Developer Mode to copy server, role, channel, and user IDs.

## Linking an account

Open **Discord** in Jellyfin, create a one-time code, and enter it with `/link` in Discord. When German is selected, the command is `/verbinden`. Administrators can also create links directly from the Jellix configuration page.

After linking, the Jellyfin menu entry changes to **Discord Connected**. Opening it again allows the user to unlink after a confirmation prompt.

Manual account assignment does not require the Discord bot to be online. It requires a valid Discord server ID, Discord user ID, and an existing Jellyfin user. After updating Jellix, restart Jellyfin and reload the administrator page so the latest configuration script is used.

## Updates and releases

Pushing a version tag such as `v0.1.0` starts the GitHub Actions release workflow. It validates locked dependencies, audits packages, builds with warnings treated as errors, runs regression tests, creates the plugin and watchdog archives, publishes a GitHub release, and updates the Jellyfin repository manifest on GitHub Pages.

Release **0.4.0** uses tag **`v0.4.0`**, four-part plugin/assembly version **`0.4.0.0`**, and the unchanged Jellyfin ABI **`12.0.0.0`**. `version.json`, the project version fields and `meta.json` must agree. A normal branch push alone does not run the release workflow; the version tag does. No release tag or push is performed by the local implementation.

Before upgrading, stop Jellyfin and save the previous plugin directory, plugin configuration and a consistent Jellix database backup (include the WAL/SHM files if they still exist). Version 0.4 adds schema 6 tables without deleting links or playback history. Test in a separate Discord channel, including a restart and a slow multi-season import, before enabling public announcements. A downgrade must use a matching pre-upgrade backup: older versions do not understand the managed-message queue. Restoring a backup also restores its point-in-time links and playback state; retain newer data before choosing that recovery path.

## Roadmap

Planned areas for future development include:

- Live end-to-end validation with additional Jellyfin, Discord, and MediaForge installations
- Top movies, genres, devices, monthly summaries, and yearly statistics recaps
- Additional configurable achievements and custom thresholds
- More recommendation filters and improved recommendation history
- Better audit-log filtering, pagination, and export options
- Customizable notification templates and richer administrator diagnostics
- More automated migration, concurrency, and large-library performance tests
- Accessibility improvements and continued German and English translation review

Roadmap items are plans, not guarantees, and may change as Jellyfin, Discord, and MediaForge evolve.

## License

Copyright © 2026 Eric (DasEric).

Jellix is licensed under GPL-3.0-or-later. Copyright, attribution, and license notices must be retained when the software is redistributed.
