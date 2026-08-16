# Jellix command guide

Jellix uses either English or German command names, selected in the Jellyfin Jellix settings. Discord shows the available options while you type. Commands disabled by the server administrator report that the feature is unavailable.

Normal results such as help, statistics, achievements, recommendations, leaderboards, and the public server status remain visible in the channel. Password links, account management, privacy settings, request searches, access setup, and internal administrator details are private.

## User commands

| English | German | What it does | Visibility and requirements |
| --- | --- | --- | --- |
| `/help` | `/hilfe` | Shows the complete user command overview. | Public. |
| `/account` | `/konto` | Opens the account dashboard with password, statistics, Continue Watching, requests, achievements, privacy, and unlink buttons. | Private. Requires a linked account. |
| `/link code:7F3K-92MX` | `/verbinden code:7F3K-92MX` | Links Discord to the Jellyfin account that created the one-time code. | Private. The code is single-use and expires quickly. |
| `/stats period:month` | `/statistik zeitraum:monat` | Shows watched movies, series, episodes, actual watch time, current series, and top series. | Public. Requires a linked account. Period: today, week, month, year, or all. |
| `/leaderboard category:watchtime period:month` | `/bestenliste kategorie:watchtime zeitraum:monat` | Shows the optional community ranking. | Public. Only opted-in users appear. Categories: watch time, movies, series, or episodes. |
| `/achievements` | `/erfolge` | Shows permanently unlocked achievements. | Public. Requires a linked account. |
| `/privacy` | `/datenschutz` | Controls leaderboard participation, public names, Now Playing visibility, and achievement announcements. | Private. Requires a linked account. |
| `/now-playing` | `/aktuelle-streams` | Shows active Jellyfin streams. | Public, administrator-only, or disabled according to server settings and user privacy. |
| `/random` | `/zufall` | Recommends a random movie or series. | Public. Optional type, genre, unseen-only, maximum runtime, and minimum rating filters. |
| `/jellyfin-access name:Eric` | `/jellyfin-zugang name:Eric` | Requests a new Jellyfin account. | Private acknowledgement. The Discord server owner reviews the request by DM. |
| `/unlock-account` | `/konto-entsperren` | Clears failed-login lockout attempts for the linked Jellyfin account. | Private. Must be enabled by an administrator. |
| `/status` | `/status` | Shows Jellyfin availability, stream count, and library totals. | Public for users. Administrators receive private technical details. |

The server administrator can require a configured streaming role for general user commands. Leaving that role empty allows everyone to use them. Account-specific actions still require a Discord-to-Jellyfin link.

## MediaForge commands

These commands appear only when the optional MediaForge integration is enabled and its Jellix bridge is available. A linked account and any configured request role are required.

| English | German | What it does | Visibility |
| --- | --- | --- | --- |
| `/request-movie query:Interstellar` | `/film-anfrage suche:Interstellar` | Searches MediaForge for a movie and lets the user choose the correct result. | Private search and confirmation. |
| `/request-series query:Dexter` | `/serien-anfrage suche:Dexter` | Searches MediaForge for a series and lets the user choose the correct result. | Private search and confirmation. |
| `/requests` | `/anfragen` | Shows the user's MediaForge requests, states, and available progress. | Private because it is personal request history. |

MediaForge remains the only source of truth for requests. Jellix does not maintain a second request database.

## Administrator commands

| Command | What it does |
| --- | --- |
| `/admin-help` or `/admin-hilfe` | Shows the private administrator command overview. |
| `/sticky action:status` | Reports whether this channel has an active sticky. |
| `/sticky action:refresh` | Reposts the saved sticky immediately. |
| `/sticky action:remove` | Removes the sticky from the current channel and deletes its saved state. |

Access request approval is handled through the **Accept** and **Reject** buttons in the DM sent to the Discord server owner. Only that owner can make the decision. Rejecting opens a form where a reason can be entered or left empty. Approval creates a non-administrator Jellyfin account, links it to Discord, optionally assigns the streaming role, and sends a one-time password setup link.

## Creating a sticky

1. Post the message or embed that should stay at the bottom of the channel.
2. Right-click that Discord message.
3. Select **Apps**.
4. Select **Set as sticky** or **Als Sticky markieren**.

Jellix stores the content and allows one sticky per channel. After new messages arrive, it waits for the configured debounce period, removes the old sticky copy, and posts one fresh copy. The sticky is restored after a bot or Jellyfin restart. The bot needs **Manage Messages**, **Read Message History**, **View Channel**, **Send Messages**, and **Embed Links** in that channel.

## Account linking and unlinking

Open **Connect Discord** in Jellyfin, create a code, and use `/link` or `/verbinden` in Discord. Once linked, the Jellyfin entry changes to **Discord Connected** or **Discord Verbunden**. Selecting it again offers a confirmed unlink action. Unlinking disables account, statistics, password, and request actions until a new link is created.
