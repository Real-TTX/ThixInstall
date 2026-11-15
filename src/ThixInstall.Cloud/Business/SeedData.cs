using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// The catalog a new cloud starts with: popular apps with their winget, Chocolatey and Homebrew names, and a few example scripts per system. Written
/// once (the setting "Seeded" remembers it), so what an administrator deletes stays deleted.
/// </summary>
public static class SeedData
{
    private sealed record App(string Key, string Name, string Publisher, string Category, string Homepage, string Description,
        string? Winget, string? Choco, string? Detect, string? Cask, string? Formula = null, bool Featured = false);

    private static readonly App[] Apps =
    [
        new("firefox", "Firefox", "Mozilla", "Browser", "https://www.mozilla.org/firefox/", "Der freie Browser von Mozilla.", "Mozilla.Firefox", "firefox", "Mozilla Firefox", "firefox", Featured: true),
        new("chrome", "Google Chrome", "Google", "Browser", "https://www.google.com/chrome/", "Der Browser von Google.", "Google.Chrome", "googlechrome", "Google Chrome", "google-chrome", Featured: true),
        new("brave", "Brave", "Brave Software", "Browser", "https://brave.com/", "Browser mit eingebautem Werbeblocker.", "Brave.Brave", "brave", "Brave", "brave-browser"),
        new("vivaldi", "Vivaldi", "Vivaldi Technologies", "Browser", "https://vivaldi.com/", "Ein Browser, der sich anpassen lässt.", "Vivaldi.Vivaldi", "vivaldi", "Vivaldi", "vivaldi"),
        new("zoom", "Zoom", "Zoom Video Communications", "Kommunikation", "https://zoom.us/", "Videokonferenzen.", "Zoom.Zoom", "zoom", "Zoom", "zoom", Featured: true),
        new("teams", "Microsoft Teams", "Microsoft", "Kommunikation", "https://www.microsoft.com/microsoft-teams/", "Chat und Besprechungen.", "Microsoft.Teams", "microsoft-teams-new-bootstrapper", "Microsoft Teams", "microsoft-teams"),
        new("slack", "Slack", "Slack Technologies", "Kommunikation", "https://slack.com/", "Team-Chat.", "SlackTechnologies.Slack", "slack", "Slack", "slack"),
        new("discord", "Discord", "Discord", "Kommunikation", "https://discord.com/", "Sprach- und Text-Chat.", "Discord.Discord", "discord", "Discord", "discord"),
        new("thunderbird", "Thunderbird", "Mozilla", "Kommunikation", "https://www.thunderbird.net/", "E-Mail, Kalender und Kontakte.", "Mozilla.Thunderbird", "thunderbird", "Mozilla Thunderbird", "thunderbird"),
        new("signal", "Signal", "Signal Foundation", "Kommunikation", "https://signal.org/", "Verschlüsselter Messenger.", "OpenWhisperSystems.Signal", "signal", "Signal", "signal"),
        new("vlc", "VLC media player", "VideoLAN", "Medien", "https://www.videolan.org/", "Spielt fast jedes Video- und Audioformat.", "VideoLAN.VLC", "vlc", "VLC media player", "vlc", Featured: true),
        new("spotify", "Spotify", "Spotify", "Medien", "https://www.spotify.com/", "Musik und Podcasts.", "Spotify.Spotify", "spotify", "Spotify", "spotify", Featured: true),
        new("audacity", "Audacity", "Audacity Team", "Medien", "https://www.audacityteam.org/", "Audio aufnehmen und schneiden.", "Audacity.Audacity", "audacity", "Audacity", "audacity"),
        new("handbrake", "HandBrake", "HandBrake Team", "Medien", "https://handbrake.fr/", "Videos umwandeln.", "HandBrake.HandBrake", "handbrake", "HandBrake", "handbrake-app"),
        new("obs", "OBS Studio", "OBS Project", "Medien", "https://obsproject.com/", "Bildschirm aufnehmen und streamen.", "OBSProject.OBSStudio", "obs-studio", "OBS Studio", "obs"),
        new("gimp", "GIMP", "The GIMP Team", "Bilder", "https://www.gimp.org/", "Bildbearbeitung.", "GIMP.GIMP.3", "gimp", "GIMP", "gimp"),
        new("inkscape", "Inkscape", "Inkscape", "Bilder", "https://inkscape.org/", "Vektorgrafik.", "Inkscape.Inkscape", "inkscape", "Inkscape", "inkscape"),
        new("paintdotnet", "Paint.NET", "dotPDN", "Bilder", "https://www.getpaint.net/", "Einfache Bildbearbeitung für Windows.", "dotPDN.PaintDotNet", "paint.net", "paint.net", null),
        new("libreoffice", "LibreOffice", "The Document Foundation", "Office", "https://www.libreoffice.org/", "Textverarbeitung, Tabellen, Präsentationen.", "TheDocumentFoundation.LibreOffice", "libreoffice-fresh", "LibreOffice", "libreoffice", Featured: true),
        new("acrobat-reader", "Adobe Acrobat Reader", "Adobe", "Office", "https://www.adobe.com/acrobat/pdf-reader.html", "PDF-Dateien lesen und unterschreiben.", "Adobe.Acrobat.Reader.64-bit", "adobereader", "Adobe Acrobat", "adobe-acrobat-reader"),
        new("sumatrapdf", "SumatraPDF", "Krzysztof Kowalczyk", "Office", "https://www.sumatrapdfreader.org/", "Schlanker PDF-Leser für Windows.", "SumatraPDF.SumatraPDF", "sumatrapdf", "SumatraPDF", null),
        new("notion", "Notion", "Notion Labs", "Office", "https://www.notion.so/", "Notizen, Wikis und Projekte.", "Notion.Notion", "notion", "Notion", "notion"),
        new("vscode", "Visual Studio Code", "Microsoft", "Entwicklung", "https://code.visualstudio.com/", "Der Code-Editor von Microsoft.", "Microsoft.VisualStudioCode", "vscode", "Microsoft Visual Studio Code", "visual-studio-code", Featured: true),
        new("git", "Git", "The Git Project", "Entwicklung", "https://git-scm.com/", "Versionsverwaltung.", "Git.Git", "git", "Git", null, Formula: "git"),
        new("notepadplusplus", "Notepad++", "Don Ho", "Entwicklung", "https://notepad-plus-plus.org/", "Texteditor für Windows.", "Notepad++.Notepad++", "notepadplusplus", "Notepad++", null, Featured: true),
        new("nodejs", "Node.js LTS", "OpenJS Foundation", "Entwicklung", "https://nodejs.org/", "JavaScript außerhalb des Browsers.", "OpenJS.NodeJS.LTS", "nodejs-lts", "Node.js", null, Formula: "node"),
        new("python", "Python 3.13", "Python Software Foundation", "Entwicklung", "https://www.python.org/", "Die Programmiersprache Python.", "Python.Python.3.13", "python313", "Python 3.13", null, Formula: "python@3.13"),
        new("docker", "Docker Desktop", "Docker", "Entwicklung", "https://www.docker.com/products/docker-desktop/", "Container auf dem eigenen Rechner.", "Docker.DockerDesktop", "docker-desktop", "Docker Desktop", "docker-desktop"),
        new("putty", "PuTTY", "Simon Tatham", "Entwicklung", "https://www.putty.org/", "SSH- und Telnet-Client.", "PuTTY.PuTTY", "putty", "PuTTY", null),
        new("7zip", "7-Zip", "Igor Pavlov", "Werkzeuge", "https://www.7-zip.org/", "Packt und entpackt fast jedes Archiv.", "7zip.7zip", "7zip", "7-Zip", null, Featured: true),
        new("the-unarchiver", "The Unarchiver", "MacPaw", "Werkzeuge", "https://theunarchiver.com/", "Entpackt fast jedes Archiv auf dem Mac.", null, null, null, "the-unarchiver"),
        new("everything", "Everything", "voidtools", "Werkzeuge", "https://www.voidtools.com/", "Findet Dateien sofort.", "voidtools.Everything", "everything", "Everything", null),
        new("powertoys", "PowerToys", "Microsoft", "Werkzeuge", "https://learn.microsoft.com/windows/powertoys/", "Nützliche Helfer für Windows.", "Microsoft.PowerToys", "powertoys", "PowerToys", null),
        new("winscp", "WinSCP", "Martin Prikryl", "Werkzeuge", "https://winscp.net/", "Dateien per SFTP und FTP übertragen.", "WinSCP.WinSCP", "winscp", "WinSCP", null),
        new("rectangle", "Rectangle", "Ryan Hanson", "Werkzeuge", "https://rectangleapp.com/", "Fenster per Tastatur anordnen (macOS).", null, null, null, "rectangle"),
        new("keepassxc", "KeePassXC", "KeePassXC Team", "Sicherheit", "https://keepassxc.org/", "Passwörter sicher verwalten.", "KeePassXCTeam.KeePassXC", "keepassxc", "KeePassXC", "keepassxc", Featured: true),
        new("bitwarden", "Bitwarden", "Bitwarden", "Sicherheit", "https://bitwarden.com/", "Passwortmanager mit Cloud.", "Bitwarden.Bitwarden", "bitwarden", "Bitwarden", "bitwarden"),
        new("dropbox", "Dropbox", "Dropbox", "Cloud-Speicher", "https://www.dropbox.com/", "Dateien in der Cloud.", "Dropbox.Dropbox", "dropbox", "Dropbox", "dropbox"),
        new("nextcloud", "Nextcloud", "Nextcloud", "Cloud-Speicher", "https://nextcloud.com/", "Dateien mit der eigenen Cloud abgleichen.", "Nextcloud.NextcloudDesktop", "nextcloud-client", "Nextcloud", "nextcloud"),
    ];

    private sealed record ScriptSeed(string Key, string Name, string Description, string Category, TargetOs Os, ScriptShell Shell, ScriptContext Context, string Content);

    private static readonly ScriptSeed[] Scripts =
    [
        new("win-show-extensions", "Dateiendungen anzeigen", "Zeigt im Explorer die Endungen aller Dateien (.pdf, .exe …).", "Explorer", TargetOs.Windows, ScriptShell.PowerShell, ScriptContext.User,
            "# Dateiendungen im Explorer anzeigen\nSet-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced' -Name HideFileExt -Value 0\nWrite-Output 'Dateiendungen werden angezeigt (nach dem nächsten Öffnen des Explorers).'\n"),
        new("win-dark-mode", "Dunkles Design", "Schaltet Windows und die Apps auf das dunkle Design.", "Darstellung", TargetOs.Windows, ScriptShell.PowerShell, ScriptContext.User,
            "$key = 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize'\nSet-ItemProperty -Path $key -Name AppsUseLightTheme -Value 0\nSet-ItemProperty -Path $key -Name SystemUsesLightTheme -Value 0\nWrite-Output 'Dunkles Design eingeschaltet.'\n"),
        new("win-high-performance", "Energiesparplan „Höchstleistung“", "Aktiviert den Energiesparplan Höchstleistung.", "System", TargetOs.Windows, ScriptShell.Cmd, ScriptContext.System,
            "@echo off\npowercfg -duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c >nul 2>&1\npowercfg -setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c\npowercfg -getactivescheme\n"),
        new("win-clean-temp", "Temporäre Dateien aufräumen", "Löscht Dateien in C:\\Windows\\Temp, die älter als sieben Tage sind.", "System", TargetOs.Windows, ScriptShell.PowerShell, ScriptContext.System,
            "$limit = (Get-Date).AddDays(-7)\n$files = Get-ChildItem -Path \"$env:windir\\Temp\" -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt $limit }\n$count = 0\nforeach ($file in $files) { try { Remove-Item $file.FullName -Force -ErrorAction Stop; $count++ } catch { } }\nWrite-Output \"$count Dateien gelöscht.\"\n"),
        new("win-update-scan", "Nach Windows-Updates suchen", "Stößt die Suche nach Windows-Updates an.", "System", TargetOs.Windows, ScriptShell.PowerShell, ScriptContext.System,
            "Start-Process -FilePath \"$env:windir\\System32\\UsoClient.exe\" -ArgumentList 'StartScan' -Wait\nWrite-Output 'Die Suche nach Updates wurde gestartet.'\n"),
        new("mac-dock-autohide", "Dock automatisch ausblenden", "Blendet das Dock aus, bis die Maus an den Rand kommt.", "Darstellung", TargetOs.MacOS, ScriptShell.Zsh, ScriptContext.User,
            "#!/bin/zsh\ndefaults write com.apple.dock autohide -bool true\nkillall Dock\necho 'Das Dock wird automatisch ausgeblendet.'\n"),
        new("mac-show-extensions", "Dateiendungen im Finder anzeigen", "Zeigt im Finder die Endungen aller Dateien.", "Finder", TargetOs.MacOS, ScriptShell.Zsh, ScriptContext.User,
            "#!/bin/zsh\ndefaults write NSGlobalDomain AppleShowAllExtensions -bool true\nkillall Finder\necho 'Dateiendungen werden angezeigt.'\n"),
        new("mac-screenshots-downloads", "Bildschirmfotos in „Downloads“", "Legt neue Bildschirmfotos im Ordner Downloads ab.", "Finder", TargetOs.MacOS, ScriptShell.Zsh, ScriptContext.User,
            "#!/bin/zsh\ndefaults write com.apple.screencapture location \"$HOME/Downloads\"\nkillall SystemUIServer\necho \"Bildschirmfotos landen in $HOME/Downloads.\"\n"),
        new("mac-brew-upgrade", "Homebrew-Pakete aktualisieren", "Aktualisiert Homebrew und alle damit installierten Programme.", "System", TargetOs.MacOS, ScriptShell.Zsh, ScriptContext.User,
            "#!/bin/zsh\nexport PATH=\"/opt/homebrew/bin:/usr/local/bin:$PATH\"\nbrew update && brew upgrade\n"),
    ];

    /// <summary>Writes the catalog once (when the setting "Seeded" is not there yet).</summary>
    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await SettingService.GetRawAsync(db, SettingService.SeededKey, ct) is not null)
        {
            return;
        }

        var order = 0;
        foreach (var app in Apps)
        {
            if (await db.Packages.IgnoreQueryFilters().AnyAsync(p => p.Key == app.Key, ct))
            {
                continue;
            }

            db.Packages.Add(new Package
            {
                Key = app.Key,
                Name = app.Name,
                Publisher = app.Publisher,
                Category = app.Category,
                Homepage = app.Homepage,
                Description = app.Description,
                IsPublic = true,
                IsFeatured = app.Featured,
                SortOrder = order++,
                WindowsMethod = app.Winget is not null ? InstallMethod.Winget : null,
                WingetId = app.Winget,
                ChocoId = app.Choco,
                DetectDisplayName = app.Detect,
                MacMethod = app.Cask is not null ? InstallMethod.HomebrewCask : app.Formula is not null ? InstallMethod.Homebrew : null,
                BrewName = app.Cask ?? app.Formula,
            });
        }

        order = 0;
        foreach (var script in Scripts)
        {
            if (await db.Scripts.IgnoreQueryFilters().AnyAsync(s => s.Key == script.Key, ct))
            {
                continue;
            }

            db.Scripts.Add(new Script
            {
                Key = script.Key, Name = script.Name, Description = script.Description, Category = script.Category, Os = script.Os, Shell = script.Shell,
                Context = script.Context, Content = script.Content, IsPublic = true, SortOrder = order++,
            });
        }

        await db.SaveChangesAsync(ct);
        await SettingService.SetAsync(db, SettingService.SeededKey, DateTime.UtcNow.ToString("O"), ct);
    }
}
