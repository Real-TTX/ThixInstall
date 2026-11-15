# ThixInstall – Logo

Ein Pfeil fällt in eine geöffnete Box (installieren) auf einer petrolfarbenen Kachel. Akzentfarbe **#0F7B6C**, dunkles
Petrol **#0A4F45**, Verlauf der Kachel **#36C7A8 → #0B7566**, Etikett der Box **#13917C**.

| Datei | Detailstufe | Einsatz |
|---|---|---|
| `logo-icon.svg` | voll (ab 48 px): Pfeil, offener Deckel, Box mit Etikett | App-Icon (Windows .ico ab 48 px, macOS .icns), Fenster, Über-Seite |
| `logo-small.svg` | reduziert (16–32 px): dicker Pfeil in eine Schale, Kachel randlos, kein Deckel, kein Etikett | Favicon, kleine .ico-Größen, Taskleiste, Tray (Windows) |
| `logo-tray.svg` | einfarbig, ohne Kachel | macOS-Menüleiste (Template-Bild, das System färbt es) |
| `logo-cloud.svg` | voll, randlose Kachel mit Wolke | ThixInstall.Cloud: PWA-Icons (auch „maskable“), Apple-Touch-Icon |
| `logo-full.svg` | Icon + Wortmarke „Thix**Install**“ | README, Kopfzeilen, Über-Seiten |

`png/` enthält alle gerenderten Größen. Neu erzeugen (nach jeder Änderung an einer SVG):

```
cd tools && npm install && node render-icons.js
```

Das Skript rendert mit dem installierten Microsoft Edge (headless) und schreibt zusätzlich
`src/ThixInstall/Assets/*` (thixinstall.ico, Tray-Icons, Logos) und `src/ThixInstall.Cloud/wwwroot/img/*` (favicon.ico, PWA-Icons).
Die macOS-Varianten `png/thixinstall-mac-*.png` halten Apples Rand um die Kachel ein; `scripts/package-macos.sh` baut daraus das `.icns`.
