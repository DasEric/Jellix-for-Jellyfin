# Release erstellen

Einmalig im GitHub-Repository unter **Settings → Pages** als Quelle **GitHub Actions** wählen.

```powershell
./scripts/set-version.ps1 -Version 0.2.0 -Changelog "Kurze Änderungen"
git add .
git commit -m "Release 0.2.0"
git tag v0.2.0
git push origin main
git push origin v0.2.0
```

Der `v*`-Tag prüft Versionen und Abhängigkeiten, baut und testet Jellix, erstellt ZIP-Dateien und Prüfsummen, veröffentlicht das GitHub-Release und aktualisiert anschließend:

`https://daseric.github.io/Jellix-for-Jellyfin/manifest.json`
