Jellix Watchdog (optional)
==========================

Der Watchdog läuft außerhalb von Jellyfin und kann deshalb melden, wenn der
gesamte Jellyfin-Server nicht erreichbar ist.

Umgebungsvariablen:
  JELLIX_JELLYFIN_URL       z. B. https://jellyfin.example.de
  JELLIX_DISCORD_WEBHOOK    Discord-Webhook-URL für den Warnungskanal
  JELLIX_INTERVAL_SECONDS   optional, Standard 60 (Minimum 15)
  JELLIX_STATE_PATH         optional, Pfad für den letzten Zustand

Start:
  pwsh ./jellix-watchdog.ps1

Webhook und URL werden nicht protokolliert. Der Zustandswert verhindert
wiederholte Meldungen. Standardmäßig liegt watchdog-state.txt neben dem Skript;
bei einem schreibgeschützten Ordner JELLIX_STATE_PATH auf einen beschreibbaren
Pfad setzen.
