# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What ThixInstall is

An alternative to Ninite for **Windows and macOS**: on the start page of **ThixInstall.Cloud** (server, Docker, port **4955**)
anybody - also without an account - picks apps and scripts ("Makros") and downloads them as **one program** that installs
everything silently. With an account the bundle can also install **ThixInstall itself**: the device then belongs to the
account, shows updates, installs from the catalog and - with **Sync** on - runs the actions sent from the cloud (a simple
Intune/Entra). **Profiles** bundle apps and scripts for devices and allow auto-enrollment with a key. Underneath it uses
**winget / Chocolatey / Homebrew**; the cloud resolves the installers (winget manifests, Homebrew cask API) and **caches the
downloads** - devices take the cache or the package manager first (a setting, per app overridable), the other one is the fallback; further sources are the **Microsoft Store**, direct downloads and **installers uploaded** to the cloud, and "App hinzufügen"
searches all sources at once (`src/ThixInstall.Cloud/Business/Sources`). The design (data model, modes, installing) is [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md); the server is
[docs/CLOUD.md](docs/CLOUD.md). UI language German; code, comments and identifiers English.

Stack: **.NET 10**, C#. Desktop: **Avalonia 11.3** (Fluent underneath our own look, "Apple-like"). Cloud: ASP.NET Core
**Razor Pages**, EF Core + **PostgreSQL**, **MatUi** of the sister project MatDesign (copied into `wwwroot/matui`).

## Build / run / test

- `dotnet build ThixInstall.slnx` – expected: 0 warnings, 0 errors. Desktop projects target `net10.0` (`DesktopTargetFramework`).
- `dotnet test tests/ThixInstall.Tests` – business layer (versions, tickets, recognising apps, catalog view), the agent against
  `FakeCloud` (in memory: enroll, catalog, actions pushed and finished, permissions), real scripts through the job runner, and
  the **UI rendered headless** (`UiTests`; with `THIXINSTALL_SCREENSHOTS=<folder>` screenshots of every page in light and
  dark – look at them). Tests never touch the real data, registry values, Keychain or LaunchAgents (`TestData`: own folder,
  own inventory reader, token store in memory, no policies).
- `dotnet test tests/ThixInstall.Cloud.Tests` – see docs/CLOUD.md (with `THIXINSTALL_TEST_DB` also against PostgreSQL);
  browser checks: `cd tests/e2e && npm install && node run.js http://localhost:4955`.
- **Run the cloud: always rebuild and redeploy the stack:** `./scripts/redeploy.ps1` (`-Clean` drops the volumes).
- Run the desktop app on its own data: `THIXINSTALL_DATA_DIR=<folder>` (own instance, the agent runs inside the app, never
  meets the installed service). `ThixInstall.exe --agent-console` runs the agent with its pipe in a console (the service
  path without installing it). Installing apps for real changes the computer: test with scripts, not with installers.
- Icons: `cd tools && npm install && node render-icons.js` renders `branding/*.svg` (installed Edge, headless) into
  `branding/png`, `src/ThixInstall/Assets`, `src/ThixInstall.Cloud/wwwroot/img`. `node tools/avalonia-icons.js` turns
  MatDesign's icon sprite into `src/ThixInstall/Themes/Icons.axaml` (never edit that file by hand; add icons in the script).
- The Windows publish (win-x64) is a **trimmed** single file (`TrimMode=partial`, about 35 MB; the trim warnings of our own
  assemblies are expected: they stay whole). `ThixInstall.exe --self-test <report>` checks the published program (JSON,
  bundle ticket, token store, installed apps, COM, every window) in a temporary data folder; CI runs it after every
  Windows publish. **A ticket appended to the program does not disturb the single-file host** (verified: the self test of
  a copy with a ticket passes).
- `scripts/package-macos.sh` builds `ThixInstall.app` (Info.plist with `LSUIElement`, icns, ad-hoc signature), the update zip and the dmg.

## Map

```
src/ThixInstall.Shared   the contract: ApiContract (routes, limits, JSON), DTOs (catalog, plans, bundles, devices, actions),
                         BundleTicket (the trailer of a bundle program / .thixinstall file), AppVersion (version compare)
src/ThixInstall.Core     (no UI)
  Models/                AppSettings (per user: look, window), AgentSettings (+ CloudConnection), JobRecord, AgentStatus,
                         CatalogApp/Snapshot, JobRequest, ConnectRequest, AgentSettingsChange, AgentCaller, BusinessException
  Business/              static classes: AgentHost (the agent: start/stop, timers, every request of the app with its permission
                         checks), JobService (queue, one job at a time, history jobs.json, user-context scripts and Store apps for the app: RunClaimedAsync),
                         CloudService (enroll/leave, the loop: facts, catalog, inventory, actions long poll), CatalogService
                         (catalog.json, snapshot with installed state), InventoryService (installed apps, matching to the
                         catalog), BundleService (ticket, plan, run, install + enroll ThixInstall), InstallService (Windows:
                         Program Files, service "ThixInstall Agent", HKLM uninstall entry), UpdateService (OTA from GitHub),
                         AutostartService (HKLM/HKCU Run, LaunchAgent), AccessPolicy (policies), MacAppBundle, SettingsService
  Installers/            InstallRoutes (cache and package managers in the order of the plan, each the fallback of the other),
                         WindowsInstaller (cached download → MSI/EXE/MSIX, winget/choco (installs Chocolatey when missing)/msstore; silent uninstall), MacInstaller
                         (DMG/ZIP → /Applications, PKG via installer, else brew), ScriptRunner, Downloads, JobContext/JobOutcome
  Agent/                 AgentClient (the app's way to the agent: in-process or named pipe "ThixInstall.Agent"), AgentProtocol,
                         AgentPipeServer (the service side; the caller's name and elevation), AgentWindowsService (--service)
  Infrastructure/        CloudClient (HTTP of the contract, downloads with SHA-256), JsonFile, SettingsStore, TokenStore
                         (DPAPI user/machine, Keychain), AppPaths (user data vs agent data), AppInfo, Log, ProblemReports
  Platform/              ProcessRunner (output tail, timeout, kill tree), Elevation (runas, root), InstalledApps (registry,
                         Store packages by family (AppxAllUserStore), /Applications, brew), PackageManagers (winget also for LocalSystem, choco, brew), MachineInfo
                         (hardware id, OS version), WindowActivation, Windows/ShellLink (generated COM), Windows/MachineFolder (ACL)
src/ThixInstall          (Avalonia app)
  Program / App          start modes: --service, --agent-console, --self-test, Windows setup commands (--install/--update/
                         --uninstall, elevated), bundle (ticket → BundleWindow; Windows elevated once), setup assistant, the app
  AgentMonitor           polls the agent status (1.5 s visible / 10 s hidden), raises Status/Jobs/Catalog changes, runs user
                         scripts for the service, restarts the app when the service was updated
  MainWindow             header (search of the page, refresh, caption buttons), sidebar (Katalog, Updates, Skripte, Aufträge
                         with counts, Einstellungen; running job card, cloud state, version = about), Ctrl/Cmd+1..4 , B F R
  Pages/                 CatalogPage (tiles by category or list; toolbar: category, filter, sort, view; actions below),
                         UpdatesPage, ScriptsPage, JobsPage (list + detail with output, cancel, retry), SettingsPage (tabs
                         Allgemein | Cloud | Updates | Erweitert), AboutPage
  Bundle/                BundleWindow (the Ninite-like runner)
  Setup/                 SetupWindow (Windows install / update / uninstall assistant; runs the work elevated, tails its log)
  Controls/              ONE code base per control: AppIcon (picture or coloured letter), StatusChip, Toolbar, ActionBar,
                         FormField, SettingRow, InfoBar, IconView, IconLabel
  ViewModels/            AppTileViewModel, ScriptRowViewModel, JobRowViewModel (ObservableObject)
  Tray/                  TrayIcon (open, running job, updates, update all, catalog, refresh, settings, update, quit), NotificationToast
src/ThixInstall.Cloud    the server – map, pages, API, data model: docs/CLOUD.md
tests/ThixInstall.Tests  desktop tests (see above); tests/ThixInstall.Cloud.Tests, tests/e2e: the cloud
```

## Conventions

- **Microsoft C# coding conventions**, file-scoped namespaces, `_camelCase` fields, human-readable names, early returns.
- **Business layer = static classes.** The UI never touches stores, files, registry, processes or HTTP: it talks to the
  agent through `AgentClient` only (also when the agent runs in the same process). Validation **and permission checks**
  (AccessPolicy, the caller of the pipe: user name, elevated administrator) live in the business layer. Errors are
  `BusinessException(message, field)`; the UI shows them at the `FormField` with that `Field`.
- **Where data lives:** the app's settings per user (`%LocalAppData%\ThixInstall`, `~/Library/Application Support/ThixInstall`);
  the agent's data (cloud connection, catalog, jobs, downloads) in `%ProgramData%\ThixInstall` for the service (SYSTEM and
  administrators only: the token is DPAPI machine scope) or in `<user data>/agent` everywhere else.
- **The installed program must never carry a bundle ticket** (`InstallService.CopyProgram` strips it).
- **Relational rules** (Postgres): PascalCase tables/columns, key `Id` (64 bit), `CreateDate`, `CreateUserId`, `UpdateDate`,
  `UpdateUserId`, `UpdateState` (0 deleted, 1 created, 2 updated) for soft deletes, a `Token` uuid where a key is a secret.
- **UI rules:** toolbar above lists (search in the header, filter, sort, view), actions below, left-aligned, positive to
  negative, delete/uninstall with a gap; buttons in one row; icons where they help; tabs for areas of one item; hide what
  does not apply; cards keep their distance; the about page opens from the version bottom left. Colours only from
  `Themes/Theme.axaml` via `DynamicResource` (light and dark; accent teal #0F7B6C). Icon-only buttons get
  `AutomationProperties.Name`. Several instances of a control on one page must work (no static state in controls).
- **Avalonia:** ControlThemes cannot contain descendant selectors – put those styles on the page (`UserControl.Styles`).
- **Platforms:** one code base; differences behind `OperatingSystem.IsWindows()/IsMacOS()` or Windows/macOS-only types.
  macOS cannot be checked from the Windows machine: CI builds and tests it, the owner tries it on a Mac (casks from the
  cache, .pkg prompt, brew, Keychain, LaunchAgent, the .thixinstall file beside the app).
- Never log passwords, tokens or script contents. Edit text files with the editor tools or `[IO.File]` with UTF-8: Windows
  PowerShell 5.1 `Get-Content`/`Set-Content` double-encode umlauts.

## Versioning, branches, CI

- `Directory.Build.props` holds major/minor. CI sets `Channel`, `BuildNumber` (GitHub run number of the workflow),
  `BuildDate`: release `0.1.42-20261115` (main), nightly `nightly-43-20261115` (dev), local `local-20261115` (everything else).
- Branches: `main` = release, `dev` = nightly. **Commits are authored by Matthias Schmoldt only – no co-author trailers, no
  other names, no "generated with" lines.**
- `.github/workflows/desktop.yml`: Windows (test, publish win-x64 single file, self test = `ThixInstallSetup.exe`), macOS
  (test, publish osx-arm64 + osx-x64, `package-macos.sh` → dmg/zip), publish (manifest `thixinstall-update.json`, rolling
  releases `release-channel` / `nightly-channel`, versioned release `v<version>` on main). The cloud hands these packages
  out in bundles (it reads the manifest of its own channel).
- `.github/workflows/cloud.yml`: tests (with a Postgres service) and the multi-arch image `ghcr.io/real-ttx/thixinstall`
  (`:latest`/`:release`/`:<version>` from main, `:nightly`/`:<version>` from dev). Compose files: `docker-compose.yml`
  (release), `docker-compose.dev.yml` (nightly + pgAdmin), `docker-compose.local.yml` (build + pgAdmin + Postgres on 4957),
  no variables, one stack each, names without repetition (services `web`, `db`, volumes `web-data`, `db-data`).
