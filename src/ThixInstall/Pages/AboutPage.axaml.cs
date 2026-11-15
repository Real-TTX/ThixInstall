using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;

namespace ThixInstall.Pages;

/// <summary>Version, channel, build and where the data lives.</summary>
public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        ChannelText.Text = AppInfo.Channel switch
        {
            "release" => "Release",
            "nightly" => "Nightly",
            _ => "Lokaler Build",
        };
        BuildText.Text = AppInfo.BuildNumber > 0 ? $"#{AppInfo.BuildNumber}" : "–";
        SystemText.Text = $"{RuntimeInformation.OSDescription} ({AppInfo.PlatformKey}), .NET {Environment.Version}";
        DataText.Text = AppPaths.DataDirectory;
        AttachedToVisualTree += (_, _) => RefreshUpdate();
        AgentMonitor.StatusChanged += (_, _) => RefreshUpdate();
    }

    /// <summary>What the last update check found; the button leads to Einstellungen › Updates (install or check there).</summary>
    private void RefreshUpdate()
    {
        var status = AgentMonitor.Status;
        var update = status?.AvailableUpdate;
        var available = update is not null;
        UpdateTitle.Text = available ? $"Version {update} ist verfügbar" : AppInfo.IsLocalBuild ? "Lokaler Build" : "Updates";
        UpdateText.Text = available
            ? "Installieren dauert nur einen Moment, Verbindung und Aufträge bleiben erhalten."
            : AppInfo.IsLocalBuild
                ? "Ein lokaler Build bekommt keine Updates."
                : status?.UpdateMessage is { } message
                    ? $"{message} Zuletzt geprüft: {(status.LastUpdateCheck is { } last ? Ui.FormatDateTime(last) : "–")}."
                    : "Noch nicht geprüft.";
        UpdateLabel.Text = available ? "Update installieren …" : "Nach Updates suchen …";
        UpdateButton.Theme = available ? Ui.Theme("PrimaryButton") : null;
        UpdateIcon.Foreground = Ui.Brush(available ? "AccentBrush" : "TextMutedBrush");
        AgentText.Text = status is null ? "–" : status.IsService ? $"Windows-Dienst · {status.AgentVersion}" : $"Im Programm · {status.AgentVersion}";
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (Ui.WindowOf(this) is MainWindow window)
        {
            window.ShowSettings(SettingsPage.UpdatesTab);
        }
    }

    private void OnGitHubClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal($"https://github.com/{AppInfo.Repository}");

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (Ui.WindowOf(this) is MainWindow window)
        {
            window.ShowCatalog();
        }
    }
}
