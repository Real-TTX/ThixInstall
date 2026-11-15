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
| **Paket (Package)** | Eine App im Katalog. Quellen je Betriebssystem: Windows `winget`-Id, `choco`-Id oder direkter Download; macOS Homebrew-Cask oder -Formel. |
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
   - **Chocolatey**, **Homebrew-Formel**: nur die Kommandozeile (`choco install -y`, `brew install`), Version per Info.
   - **Direkt**: URL + stille Parameter aus dem Katalog, SHA-256 optional.
2. Der `InstallPlan` im Katalog zeigt auf `/api/v1/downloads/{releaseId}`. Beim ersten Abruf lädt die Cloud die
   Datei von der Quelle, prüft die SHA-256 und legt sie unter `/data/cache` ab; danach kommt sie aus dem Cache
   (LRU-Bereinigung bei Überschreiten der eingestellten Größe).
3. Das Gerät lädt, prüft die SHA-256 und startet den Installer still; ohne Download-URL nimmt es den Paketmanager.

Erkennung installierter Apps: Windows über „Installierte Apps“ (Registry Uninstall, HKLM 64/32 Bit und die Benutzer),
macOS über `/Applications/<App>.app` (`CFBundleShortVersionString`) bzw. `brew list --versions`. Update verfügbar =
`AppVersion.IsNewer(LatestVersion, installiert)`.

## Desktop: Betriebsarten

| Art | Wann | Was |
|---|---|---|
| **Bundle-Runner** | Programm trägt ein `BundleTicket` mit der Paket-Konfiguration (an die EXE angehängt; macOS: `.thixinstall`-Datei neben der signierten App) und ist nicht installiert | Ninite-artiges Fenster: liest die eingepackte Konfiguration, installiert alles nacheinander mit Fortschritt; Windows startet sich dafür einmal mit Administratorrechten neu. Bundle mit Agent: installiert danach ThixInstall und meldet das Gerät mit dem Bundle-Token an. |
| **Setup** (Windows) | `ThixInstallSetup.exe` ohne Ticket, nicht installiert | Assistent: nach `%ProgramFiles%\ThixInstall`, Dienst „ThixInstall Agent“ (LocalSystem), Startmenü, Autostart, Updates. Still: `--install --quiet [--cloud <url> --enroll-key <schlüssel>]`. |
| **Dienst** (Windows) | `ThixInstall.exe --service` (vom Dienst-Manager) | Der Agent: Verbindung zur Cloud, Inventar, Aufträge, Auto-Updates – als LocalSystem, ohne UAC. |
| **App** | sonst | Tray + Hauptfenster: Katalog, Updates, Skripte, Aufträge, Einstellungen. Spricht mit dem Agenten über `AgentClient`: Windows installiert → Named Pipe zum Dienst; macOS und Entwicklung → derselbe Agent im eigenen Prozess. |

Skripte im Kontext *Benutzer* führt die App in der Sitzung des Benutzers aus (der Dienst hält sie dafür bereit).

## Cloud: Datenmodell (PostgreSQL)

Regeln: Tabellen und Spalten PascalCase, Schlüssel `Id` (bigint), `CreateDate`, `CreateUserId`, `UpdateDate`,
`UpdateUserId`, `UpdateState` (0 gelöscht, 1 angelegt, 2 geändert) für Soft-Delete, `Token` (uuid) wo ein Schlüssel ein
Geheimnis ist.

| Tabelle | Inhalt |
|---|---|
| `User` | Name, Anzeigename, E-Mail, Passwort-Hash, `IsAdmin` |
| `UserSession` | Token, UserId, Ablauf (Browser-Anmeldung) |
| `Package` | Key, Name, Publisher, Beschreibung, Kategorie, Homepage, Icon (bytea + Typ), `IsPublic`, `IsFeatured`, Sortierung; Windows: `WingetId`, `ChocoId`, `DirectUrl`, `DirectSilentArguments`, `DirectSha256`, `WindowsMethod`, `DetectDisplayName`; macOS: `BrewName`, `BrewIsCask`, `MacAppBundleName` |
| `PackageRelease` | PackageId, Os, Architecture, Version, Method, InstallerUrl, Sha256, InstallerKind, SilentArguments, ProductCode, AppBundleName, FileName, Size, ResolvedDate, Error |
| `Script` | Key, Name, Beschreibung, Kategorie, Os, Shell, Context, Content, TimeoutSeconds, `IsPublic` |
| `Profile` | Name, Beschreibung, Os (optional), `EnrollmentKey` (Token) |
| `ProfileItem` | ProfileId, PackageId oder ScriptId, Sortierung |
| `Device` | Token (Hash), UserId, ProfileId, Name, Os, OsVersion, Architecture, AgentVersion, HardwareId, `SyncEnabled`, LastSeenDate, InventoryDate |
| `DeviceApp` | DeviceId, Name, Version, Publisher, PackageId, Source (das letzte Inventar) |
| `DeviceAction` | DeviceId, Kind, State, Origin, PackageId, ScriptId, Title, RequestedByUserId, Revision, StartedDate, FinishedDate, ExitCode, Output, InstalledVersion |
| `Bundle` | Token, Name, Os, `IncludesAgent`, ProfileId, OwnerUserId (null = anonym), DownloadCount, LastDownloadDate |
| `BundleItem` | BundleId, PackageId oder ScriptId, Sortierung |
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
