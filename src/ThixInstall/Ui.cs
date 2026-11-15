using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ThixInstall.Core.Infrastructure;

namespace ThixInstall;

/// <summary>Small helpers for code-built UI: theme resources by key, the UI thread, opening addresses.</summary>
internal static class Ui
{
    public static object Resource(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) && value is not null
            ? value
            : throw new InvalidOperationException($"Resource {key} is missing in Themes/*.axaml.");

    public static IBrush Brush(string key) => (IBrush)Resource(key);

    /// <summary>An icon geometry ("Icon.Copy", Themes/Icons.axaml).</summary>
    public static Geometry Icon(string key) => (Geometry)Resource(key);

    /// <summary>A named look (ControlTheme) such as "PrimaryButton" or "MutedText".</summary>
    public static ControlTheme Theme(string key) => (ControlTheme)Resource(key);

    /// <summary>Runs the action on the UI thread (events of the business layer come from any thread).</summary>
    public static void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>The window that hosts a control (null while it is not shown).</summary>
    public static Window? WindowOf(Control control) => TopLevel.GetTopLevel(control) as Window;

    /// <summary>Ctrl on Windows, Cmd on macOS: the modifier of the app's own shortcuts.</summary>
    public static KeyModifiersName CommandKey => OperatingSystem.IsMacOS() ? new("⌘", Avalonia.Input.KeyModifiers.Meta) : new("Strg", Avalonia.Input.KeyModifiers.Control);

    /// <summary>Pause / resume recording: Ctrl+Shift+P, on macOS ⌘⇧P.</summary>
    public static string PauseShortcut => OperatingSystem.IsMacOS() ? "⌘⇧P" : "Strg+Umschalt+P";

    /// <summary>Opens a web address or a folder with the program the system uses for it (browser, Explorer, Finder).</summary>
    public static void OpenExternal(string pathOrUrl)
    {
        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new System.Diagnostics.ProcessStartInfo("open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                : OperatingSystem.IsLinux()
                    ? new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                    : new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true };
            System.Diagnostics.Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Could not open {pathOrUrl}: {exception.Message}");
        }
    }

    /// <summary>"vor 3 Min." / "14:28" / "Gestern, 17:56" / "12.11., 09:14" / "12.11.2025".</summary>
    public static string FormatTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today)
        {
            return local.ToString("HH:mm");
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Gestern, {local:HH:mm}";
        }

        return local.Year == today.Year ? local.ToString("dd.MM., HH:mm") : local.ToString("dd.MM.yyyy");
    }

    /// <summary>"Heute, 13:47" or "12.11.2026, 13:47".</summary>
    public static string FormatDateTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        if (local.Date == DateTime.Today)
        {
            return $"Heute, {local:HH:mm}";
        }

        return local.Date == DateTime.Today.AddDays(-1) ? $"Gestern, {local:HH:mm}" : local.ToString("dd.MM.yyyy, HH:mm");
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };

    public static string RetentionText(int days) => days switch
    {
        0 => "Unbegrenzt",
        1 => "1 Tag",
        365 => "1 Jahr",
        _ => $"{days} Tage",
    };
}

/// <summary>The name and the modifier of the command key of the platform.</summary>
internal sealed record KeyModifiersName(string Name, Avalonia.Input.KeyModifiers Modifier);
