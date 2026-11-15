using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using ThixInstall.Controls;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;

namespace ThixInstall.Dialogs;

/// <summary>
/// Shown at start when ThixInstall crashed or hung last time (<see cref="ProblemReports"/>): what happened, the whole report
/// to read and copy, the log folder. Several reports: the newest one, the others are named.
/// </summary>
public sealed class ProblemReportDialog : ThemedWindow
{
    private ProblemReportDialog(IReadOnlyList<ProblemReport> reports)
    {
        var report = reports[^1];
        Title = report.IsHang ? "ThixInstall hat nicht reagiert" : "ThixInstall wurde unerwartet beendet";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        this[!BackgroundProperty] = this.GetResourceObservable("CardBrush").ToBinding();

        var heading = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new IconView { Data = Ui.Icon("Icon.Warning"), Width = 22, Height = 22, Foreground = Ui.Brush(report.IsHang ? "WarningBrush" : "DangerBrush") },
                new TextBlock { Text = Title, FontSize = 17, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            },
        };

        var others = reports.Count - 1;
        var intro = new TextBlock
        {
            Text = $"Am {report.Time:dd.MM.yyyy} um {report.Time:HH:mm} Uhr. Der Bericht unten hilft, die Ursache zu finden – kopiere ihn oder öffne den Protokollordner." +
                   (others > 0 ? $" Dazu {(others == 1 ? "ein weiterer Bericht" : $"{others} weitere Berichte")} seit dem letzten Start." : ""),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Brush("TextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var details = new TextBox
        {
            Text = report.Text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            FontSize = 12,
            Height = 280,
            Margin = new Thickness(0, 0, 0, 18),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(details, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        AutomationProperties.SetName(details, "Bericht");

        var copy = new Button { Content = new IconLabel { Icon = Ui.Icon("Icon.Copy"), Text = "Bericht kopieren" }, Theme = Ui.Theme("PrimaryButton") };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(report.Text);
            }

            ((IconLabel)copy.Content!).Text = "Kopiert";
        };

        var folder = new Button { Content = new IconLabel { Icon = Ui.Icon("Icon.External"), Text = "Protokollordner öffnen" } };
        folder.Click += (_, _) => Ui.OpenExternal(AppPaths.LogDirectory);

        var close = new Button { Content = new IconLabel { Icon = Ui.Icon("Icon.Close"), Text = "Schließen" }, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22),
            Children = { heading, intro, details, new ActionBar { Children = { copy, folder, close } } },
        };
    }

    /// <summary>Shows the reports of the last run, if there are any (start-up); returns the dialog (null: nothing to say).</summary>
    public static ProblemReportDialog? ShowUnseen()
    {
        var reports = ProblemReports.TakeUnseen();
        if (reports.Count == 0)
        {
            return null;
        }

        var dialog = new ProblemReportDialog(reports);
        dialog.Show();
        return dialog;
    }
}
