# Änderungen

## 0.5.0

- H0melab Downloader über die bestehende Protokoll-v1-Schnittstelle des Jellyfin-Connectors „AniWorld Requests“, statt MediaForge. Der Connector verwaltet Freigaben, Direktdownloads, Benutzerzuordnung, Quellen, Limits, Wartungsmodus und Downloadvorgaben; Jellix hält keine zweite Anfragewarteschlange und benötigt keinen Downloader-API-Key.
- Ausstehende Anfragen warten auf die Admin-Freigabe im Connector. Direktdownloads starten nur bei dessen entsprechender Einstellung. Offene Discord-Auswahlen prüfen beim Absenden die aktuelle Einstellung.
- Lesbare Footer „Bibliotheksübersicht“ und „Neu auf Jellyfin“, ohne interne IDs. Gespeicherte Nachrichten werden am vorhandenen Nachrichtenplatz korrigiert, auch abgeschlossene Serienmeldungen. Gelöschte historische Meldungen werden dabei nicht neu veröffentlicht.
- Datenbankschema 7 ergänzt den Inhalt eines begonnenen Sendeversuchs. Nach Neustarts/verlorenen Discord-Antworten bleiben Nachrichten auffindbar, selbst wenn inzwischen weitere Folgen hinzugekommen sind. Mehrdeutige Funde führen nicht zu blindem Neusenden.
- Film-/Serien-/Folgenzahlen, belegter/freier Medienspeicher und gebündelte Serienmeldungen aus 0.4.0 bleiben erhalten. Downloadabschluss wird nicht mit bereits indexierter Jellyfin-Verfügbarkeit gleichgesetzt.
- Bestehende XML-Einstellungen für Integration/Statusintervall bleiben lesbar. Alte MediaForge-Ergebnisse werden nicht als H0melab-Ergebnisse wiederverwendet; Kontoverknüpfungen und Wiedergabestatistiken bleiben erhalten.

### Einführung

1. Jellyfin beenden und Pluginordner, Konfiguration sowie konsistente Jellix-Datenbank sichern (einschließlich vorhandener WAL-/SHM-Dateien). Auch Connector-Konfiguration und Anfragedaten sichern.
2. Connector auf 1.3.2 aktualisieren. H0melab Downloader 2.1.1 bleibt unverändert. URL/API-Key, Quellen, Downloadvorgaben und Freigabemodus nur im Connector konfigurieren.
3. Jellix 0.5.0 installieren und Jellyfin neu starten. Konfigurationsseite vollständig neu laden; H0melab-Integration aktivieren und Rollen/Kanäle prüfen.
4. Mit verknüpftem Testbenutzer im Freigabemodus eine Anfrage absenden: Sie muss im Connector ausstehend bleiben. Dort freigeben und den Status prüfen. Anschließend mit einem anderen Titel den Direktmodus prüfen.
5. In einem Testkanal bestehende Übersicht, Neustart und mehrstaffeligen Import prüfen. Es bleibt bei einer aktualisierten Serienmeldung; Footer enthalten keine technischen IDs. Der Bot benötigt Nachrichtenverlauf lesen und Embeds senden.
6. Öffentliche Kanäle erst danach verwenden. Ein parallel laufender nativer Downloader-Bot verwendet eine andere Discord-Anwendung.

### Release und Rückweg

Jellix-Version: 0.5.0 / 0.5.0.0, Jellyfin-ABI: 12.0.0.0. Nur der Push von `v0.5.0` löst den Jellix-Release-/Repository-Workflow aus. Der Connector veröffentlicht seine vorbereitete Version mit `v1.3.2`.

Für den Rückweg Jellyfin beenden und den zusammengehörigen Plugin-/Konfigurations-/Datenbanksicherungsstand wiederherstellen. Jellix 0.4.0 verwendet die frühere MediaForge-Schnittstelle; ein Downgrade stellt keine H0melab-Anbindung bereit. Aktuelle Anfragedaten des Connectors vor Rücksicherung separat erhalten. Ältere Jellix-Versionen verstehen die lesbaren Footer nicht zuverlässig; deshalb kein bloßer DLL-Tausch im laufenden Betrieb.
