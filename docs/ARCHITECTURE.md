# ThixInstall – Architektur

ThixInstall ist eine Alternative zu Ninite: auf der Startseite der **ThixInstall.Cloud** klickt man sich – auch ohne
Konto – ein Paket aus Apps und Skripten zusammen und lädt es als **ein Programm** herunter, das alles still installiert.
Mit Konto kann man zusätzlich **ThixInstall selbst** mitinstallieren: das Gerät meldet sich dann bei der Cloud an,
zeigt Updates, installiert aus dem Katalog und – mit eingeschaltetem **Sync** – führt es Aktionen aus, die man aus der
Cloud schickt (eine einfache Version von Intune/Entra). **Profile** bündeln Apps und Skripte für Geräte und erlauben
Auto-Enrollment mit einem Registrierungsschlüssel.

Sprache der Oberfläche: Deutsch. Code, Kommentare, Bezeichner: Englisch.

## Bausteine

```
src/ThixInstall.Shared   der Vertrag (ApiContract, DTOs, BundleTicket, AppVersion) – Cloud und Desktop
src/ThixInstall.Core     Desktop ohne UI: Agent (Jobs, Inventar, Sync), Installer-Engines, Setup, Updates, Plattform
src/ThixInstall          Desktop-App (Avalonia): Hauptfenster, Tray, Bundle-Fenster, Setup-Assistent, Windows-Dienst-Host
src/ThixInstall.Cloud    Server (Docker, Port 4955): Razor Pages + MatUi, API /api/v1, PostgreSQL, Download-Cache
```

## Begriffe

| Begriff | Bedeutung |
|---|---|
| **Paket (Package)** | Eine App im Katalog. Quellen je Betriebssystem: Windows `winget`-Id, Microsoft-Store-Id, `choco`-Id, direkter Download, eine hochgeladene Datei oder eine **Thix-App**; macOS Homebrew-Cask oder -Formel, direkter Download, eine hochgeladene Datei oder eine Thix-App. „App hinzufügen“ sucht in allen Quellen und legt die App mit einem Klick an. |
| **Skript (Script)** | Ein „Makro“: PowerShell/Cmd (Windows) oder Bash/Zsh (macOS), Kontext *System* (Administratorrechte) oder *Benutzer*. |
| **Bundle** | Die Auswahl von der Startseite (Token = Geheimnis). Download = **eine** ThixInstall-EXE, in der die ganze Konfiguration steckt (`BundleTicket`: Cloud, Token, Apps mit Installationsplänen, Skripte, Agent, Profil). |
| **Gerät (Device)** | Ein Rechner mit installiertem ThixInstall, angemeldet an einem Konto (Token). |
| **Sync** | Schalter am Gerät: nur dann meldet es sein Inventar und nimmt Aktionen aus der Cloud an. |
| **Aktion (Action)** | Ein Auftrag an ein Gerät: installieren, aktualisieren, deinstallieren, Skript ausführen, alles aktualisieren. |
| **Profil (Profile)** | Apps + Skripte für Geräte; Registrierungsschlüssel für Auto-Enrollment. Ein Gerät hat höchstens ein Profil. |
| **Release** | Die aufgelöste Version eines Pakets je OS/Architektur: Installer-URL, SHA-256, Installer-Art, stille Parameter. |
| **Cache** | Die Cloud lädt jeden Installer einmal von der Quelle und liefert ihn danach selbst aus. |

## Installieren: winget/choco/brew im Unterbau, die Cloud als Cache

1. Die Cloud löst Pakete regelmäßig auf (alle 6 h und auf Knopfdruck):
   - **winget**: Manifest aus `microsoft/winget-pkgs` (GitHub): höchste Version, Installer je Architektur
     (x64 → x64/x86, arm64 → arm64/x64/x86), bevorzugt Scope *machine*. Aus `InstallerType` werden Art und stille
     Parameter: `msi`/`wix` → MSI `/qn /norestart`, `nullsoft` → `/S`, `inno` → `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`,
     `burn` → `/quiet /norestart`, `exe` → `InstallerSwitches.Silent` des Manifests, `msix`/`appx` → MSIX. Nicht
     unterstützt (zip/portable, exe ohne Silent-Schalter): Plan ohne Download → das Gerät ruft `winget install` auf.
   - **Homebrew-Cask**: `formulae.brew.sh/api/cask/<name>.json`: Version, URL, SHA-256, `.app`-Name (`artifacts`),
     dmg/zip/pkg. Intel-Macs nehmen die passende `variations`-URL.
   - **Chocolatey**: die neueste Version aus dem Feed von community.chocolatey.org; installiert wird mit `choco install -y` (fehlt
     Chocolatey, richtet das Gerät es ein). **Homebrew-Formel**: `brew install`, Version aus der API.
   - **Microsoft Store**: Version (oft keine: die App aktualisiert sich selbst) und Paketfamilie aus dem Store-Manifest; das Gerät ruft
     `winget install --id <Store-Id> --source msstore` als angemeldeter Benutzer auf (Store-Apps gehören einem Benutzer).
   - **Direkt**: URL + stille Parameter aus dem Katalog, SHA-256 optional; macOS dmg/zip (mit dem Namen der App) oder pkg.
   - **Thix-App** (ThixFred, ThixClip, ThixNote …): das Update-Manifest des Release-Kanals der App auf GitHub
     (`<repo>/releases/download/release-channel/<app>-update.json`): Version, Setup (Windows, auch für Arm) bzw. gezipptes Bundle je
     Prozessor (macOS) mit SHA-256, über den Cache. Dazu der Block `installer` (MatDesign SETUP-08): was das Setup anbietet –
     Desktop-Verknüpfung, Autostart, automatische Updates, Dateitypen, je mit Vorgabe; die Cloud speichert ihn bei der App.
   - **Eigene Datei**: ein Administrator lädt den Installer hoch (Windows msi/exe/msix, macOS dmg/pkg/zip, bis 2 GB); er liegt in
     `/data/uploads` (nie von der Cache-Bereinigung entfernt) und wird wie ein Cache-Eintrag ausgeliefert. Version und Parameter gibt der
     Administrator an.
2. Der `InstallPlan` im Katalog zeigt auf `/api/v1/downloads/{releaseId}`. Beim ersten Abruf lädt die Cloud die
   Datei von der Quelle, prüft die SHA-256 und legt sie unter `/data/cache` ab; danach kommt sie aus dem Cache
   (LRU-Bereinigung bei Überschreiten der eingestellten Größe).
3. Das Gerät lädt, prüft die SHA-256 und startet den Installer still; ohne Download-URL nimmt es den Paketmanager.

**Beide Wege, jeder der Ausweichweg des anderen** (`InstallPlan.Route`, `InstallRoutes` im Core): der Plan trägt den Download aus dem Cache
*und* die Paketmanager (Windows: `WingetId` und `ChocoId`, soweit bekannt; macOS: der Cask). Welcher Weg zuerst kommt, legt die Cloud fest
(Einstellungen › Allgemein: „Cloud-Cache zuerst“ oder „winget/Chocolatey/Homebrew zuerst“; je App unter Windows/macOS überschreibbar).
Schlägt ein Weg fehl (Cloud nicht erreichbar, Prüfsumme falsch, Fehlercode des Installers), nimmt das Gerät den nächsten und schreibt den
Wechsel in die Ausgabe des Auftrags. Windows-Reihenfolge der Paketmanager: der der Quelle zuerst (winget bzw. Chocolatey), dann der andere;
winget fällt weg, wenn das Gerät keins hat. Ältere Clouds senden weder Ids noch Reihenfolge: dann gilt Cache, dann der Paketmanager der Quelle.

**App hinzufügen**: die Cloud durchsucht winget (offizieller Index `source2.msix`, als SQLite in `/data/sources`), den Microsoft Store
(Store-Dienst von winget), Chocolatey (OData-Feed) und Homebrew (vollständige Listen in `/data/sources`) parallel, schlägt je System einen
Treffer und eine Kategorie vor und markiert, was schon im Katalog ist. Beim Anlegen kommen Name, Herausgeber, Beschreibung und Homepage aus den
Quellen (winget: Locale-Manifest), das Icon von Chocolatey oder der Homepage; danach wird aufgelöst.

**Installationsoptionen der Thix-Apps** (SETUP-08): kreuzt jemand auf der Startseite eine Thix-App an, erscheinen ihre Fragen mit den Vorgaben;
die Antworten speichert das Paket (`BundleItem.InstallChoices`, nur was die App anbietet) und gibt sie im Plan mit (`InstallPlan.Choices`). Das
Gerät baut daraus die Kommandozeile (`ThixAppSetup` im Vertrag): Windows `<App>Setup.exe --quiet --desktop-shortcut=yes|no --autostart=yes|no
--automatic-updates=yes|no --file-types=.a,.b|none` (nur angebotene Optionen) und `--start`, wenn Autostart gewählt ist; ein Update ruft `--update`
(die App behält ihre Wahl), eine App ohne `installer`-Block ebenfalls `--update`. macOS kopiert das Bundle nach /Programme und ruft
`<App>.app/Contents/MacOS/<App> --configure <Optionen>`. Exit-Code 2 = die App hat die Optionen abgelehnt und nichts geändert (der Auftrag schlägt
mit ihrer Meldung fehl). Ohne Auswahl (Katalog auf dem Gerät, Aufträge, Profile) gelten die Vorgaben der App. Thix-Apps installieren pro Benutzer:
unter Windows führt sie – wie Store-Apps – die App in der Sitzung des Benutzers aus, nicht der Dienst.

Erkennung installierter Apps: Windows über „Installierte Apps“ (Registry Uninstall, HKLM 64/32 Bit und die Benutzer) bzw. bei Store-Apps (MSIX)
über die Paketfamilie,
macOS über `/Applications/<App>.app` (`CFBundleShortVersionString`) bzw. `brew list --versions`. Update verfügbar =
`AppVersion.IsNewer(LatestVersion, installiert)`.

## Desktop: Betriebsarten

| Art | Wann | Was |
|---|---|---|
| **Bundle-Runner** | Programm trägt ein `BundleTicket` mit der Paket-Konfiguration (an die EXE angehängt; macOS: `.thixinstall`-Datei neben der signierten App) und ist nicht installiert | Ninite-artiges Fenster: liest die eingepackte Konfiguration, installiert alles nacheinander mit Fortschritt; Windows startet sich dafür einmal mit Administratorrechten neu. Bundle mit Agent: installiert danach ThixInstall und meldet das Gerät mit dem Bundle-Token an. |
| **Setup** (Windows) | `ThixInstallSetup.exe` ohne Ticket, nicht installiert | Assistent: nach `%ProgramFiles%\ThixInstall`, Dienst „ThixInstall Agent“ (LocalSystem), Startmenü; Desktop-Verknüpfung, Autostart und automatische Updates nach `installer.json` (SETUP-08). Still: `--quiet [--desktop-shortcut=yes/no --autostart=yes/no --automatic-updates=yes/no] [--start] [--cloud <url> --enroll-key <schlüssel>]` (auch `--install`), `--update`, `--configure`; eine falsche Option endet mit Exit-Code 2, ohne etwas zu tun. |
| **Dienst** (Windows) | `ThixInstall.exe --service` (vom Dienst-Manager) | Der Agent: Verbindung zur Cloud, Inventar, Aufträge, Auto-Updates – als LocalSystem, ohne UAC. |
| **App** | sonst | Tray + Hauptfenster: Katalog, Updates, Skripte, Aufträge, Einstellungen. Spricht mit dem Agenten über `AgentClient`: Windows installiert → Named Pipe zum Dienst; macOS und Entwicklung → derselbe Agent im eigenen Prozess. |

Skripte im Kontext *Benutzer* und Apps aus dem **Microsoft Store** (`winget --source msstore`; Store-Apps gehören einem Benutzer,
als LocalSystem geht der Store nicht) führt die App in der Sitzung des Benutzers aus (der Dienst hält sie dafür bereit). Fehlt
Chocolatey für ein Chocolatey-Paket, installiert der Dienst es vorher still (Skript von chocolatey.org).

## Cloud: Datenmodell (PostgreSQL)

Regeln: Tabellen und Spalten PascalCase, Schlüssel `Id` (bigint), `CreateDate`, `CreateUserId`, `UpdateDate`,
`UpdateUserId`, `UpdateState` (0 gelöscht, 1 angelegt, 2 geändert) für Soft-Delete, `Token` (uuid) wo ein Schlüssel ein
Geheimnis ist.

| Tabelle | Inhalt |
|---|---|
| `User` | Name, Anzeigename, E-Mail, Passwort-Hash, `IsAdmin` |
| `UserSession` | Token, UserId, Ablauf (Browser-Anmeldung) |
| `Package` | Key, Name, Publisher, Beschreibung, Kategorie, Homepage, Icon (bytea + Typ), `IsPublic`, `IsFeatured`, Sortierung; Thix-App: `ThixRepository`, `ThixManifest`, `InstallerOptions` (JSON des `installer`-Blocks); Windows: `WindowsMethod`, `WingetId`, `StoreId`, `StorePackageFamilyName`, `ChocoId`, `DirectUrl`, `DirectSilentArguments`, `DirectSha256`, `DirectVersion`, `WindowsUploadSha256`, `WindowsUploadFileName`, `DetectDisplayName`; macOS: `MacMethod`, `BrewName`, `MacDirectUrl`, `MacDirectSha256`, `MacDirectVersion`, `MacUploadSha256`, `MacUploadFileName`, `MacAppBundleName` |
| `PackageRelease` | PackageId, Os, Architecture, Version, Method, InstallerUrl, Sha256, InstallerKind, SilentArguments, ProductCode, AppBundleName, FileName, Size, ResolvedDate, Error |
| `Script` | Key, Name, Beschreibung, Kategorie, Os, Shell, Context, Content, TimeoutSeconds, `IsPublic` |
| `Profile` | Name, Beschreibung, Os (optional), `EnrollmentKey` (Token) |
| `ProfileItem` | ProfileId, PackageId oder ScriptId, Sortierung |
| `Device` | Token (Hash), UserId, ProfileId, Name, Os, OsVersion, Architecture, AgentVersion, HardwareId, `SyncEnabled`, LastSeenDate, InventoryDate |
| `DeviceApp` | DeviceId, Name, Version, Publisher, PackageId, Source (das letzte Inventar) |
| `DeviceAction` | DeviceId, Kind, State, Origin, PackageId, ScriptId, Title, RequestedByUserId, Revision, StartedDate, FinishedDate, ExitCode, Output, InstalledVersion |
| `Bundle` | Token, Name, Os, `IncludesAgent`, ProfileId, OwnerUserId (null = anonym), DownloadCount, LastDownloadDate |
| `BundleItem` | BundleId, PackageId oder ScriptId, Sortierung, `InstallChoices` (JSON: die Installationsoptionen einer Thix-App) |
| `CacheFile` | Sha256, SourceUrl, FileName, ContentType, Size, LastUsedDate, UseCount |
| `Setting` | Key/Value: Desktop-Kanal, GitHub-Token, Cache-Größe, öffentlicher Katalog an/aus |

Rechte: **Administratoren** pflegen Katalog, Profile, Benutzer, Einstellungen und sehen alle Geräte. **Benutzer**
sehen den Katalog, erstellen Bundles und verwalten ihre eigenen Geräte (Aktionen senden, Profil zuweisen). Jede
Prüfung steht (auch) im Business-Layer (`Actor`).

## Versionen, Kanäle

Siehe README: `main` → Release `0.1.<build>-<datum>`, `dev` → `nightly-<build>-<datum>`, sonst `local-<datum>`. Die Cloud
holt die Desktop-Pakete, die sie in Bundles ausliefert, aus dem GitHub-Release ihres Kanals (`release-channel` bzw.
`nightly-channel`; eine lokale Cloud nimmt den Release-Kanal) – oder aus einer Datei, die ein Administrator unter
Einstellungen › Desktop-App hochgeladen hat (für lokale Tests).
