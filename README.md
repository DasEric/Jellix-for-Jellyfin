# Jellix for Jellyfin

Jellix verbindet Jellyfin mit Discord: Kontoverknüpfung und Passwortänderung, Wiedergabestatistiken, Achievements, Empfehlungen, Stickies, Benachrichtigungen und optional MediaForge-Anfragen.

## Installation

1. In Jellyfin unter **Dashboard → Plugins → Repositories** diese URL hinzufügen:
   `https://daseric.github.io/Jellix-for-Jellyfin/manifest.json`
2. Jellix im Katalog installieren und Jellyfin neu starten.
3. Im [Discord Developer Portal](https://discord.com/developers/applications) eine Anwendung mit Bot erstellen.
4. Den Bot mit den Scopes `bot` und `applications.commands` einladen. Benötigt werden **Kanäle ansehen**, **Nachrichten senden**, **Links einbetten**, **Dateien anhängen** und **Nachrichtenverlauf lesen**. Für Stickies zusätzlich **Nachrichten verwalten**, für die automatische Rollenzuweisung **Rollen verwalten**.
5. In Jellyfin unter **Dashboard → Plugins → Jellix** Bot-Token, Discord-Server-ID und gewünschte Rollen/Kanäle eintragen. Danach Jellyfin neu starten.

Discord-IDs erhältst du über Discords Entwicklermodus mit **ID kopieren**. Das Bot-Token wird verschlüsselt gespeichert und nie in der normalen Plugin-Konfiguration abgelegt.

## Benutzer verbinden

Ein Jellyfin-Benutzer öffnet **Discord verbinden**, erzeugt einen einmaligen Code und gibt ihn in Discord mit `/verbinden` beziehungsweise `/link` ein. Alternativ kann ein Administrator Konten in den Jellix-Einstellungen direkt zuweisen.

## MediaForge

MediaForge ist optional. Die Anfragebefehle erscheinen nur, wenn die Integration aktiviert und eine kompatible MediaForge-Brücke installiert ist. Die nötige Connector-Anpassung steht in [MediaForge-Integration-Aenderungen.txt](MediaForge-Integration-Aenderungen.txt).

Für eine Warnung, wenn Jellyfin selbst komplett ausgefallen ist, enthält jedes Release zusätzlich den optionalen `JellixWatchdog` mit kurzer Anleitung. Alle anderen Warnungen übernimmt das Plugin selbst.

## Lizenz

Copyright © 2026 Eric (DasEric). Lizenziert unter GPL-3.0-or-later. Copyright- und Lizenzhinweise müssen bei Weitergabe erhalten bleiben.
