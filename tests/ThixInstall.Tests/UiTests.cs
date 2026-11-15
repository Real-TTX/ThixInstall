using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Pages;
using ThixInstall.Shared;
using ThixInstall.ViewModels;

[assembly: AvaloniaTestApplication(typeof(ThixInstall.Tests.UiTestApp))]

namespace ThixInstall.Tests;

/// <summary>The real app (App.axaml, themes, pages) rendered headless with Skia: real pixels, no screen needed.</summary>
public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The main window with a sample catalog, driven like a user would, and screenshots of every page in light and dark
/// (set THIXINSTALL_SCREENSHOTS to a folder to keep them; docs/images has the ones of the README).
/// </summary>
public sealed class UiTests
{
    private static readonly string? ShotFolder = Environment.GetEnvironmentVariable("THIXINSTALL_SCREENSHOTS");

    [AvaloniaFact]
    public void Choosing_tiles_offers_the_right_actions_and_queues_jobs()
    {
        using var data = Sample();
        var window = Open();
        var catalog = (CatalogPage)window.PageHost.Content!;
        WaitUntil(window, () => catalog.FindControl<ItemsControl>("Groups")!.ItemsSource is IEnumerable<CatalogGroup> groups && groups.Any());

        var groups = ((IEnumerable<CatalogGroup>)catalog.FindControl<ItemsControl>("Groups")!.ItemsSource!).ToList();
        Assert.Contains(groups, group => group.Name == "Browser");
        var tiles = groups.SelectMany(group => group.Items).ToList();
        var firefox = tiles.Single(tile => tile.Key == "firefox");
        var git = tiles.Single(tile => tile.Key == "git");
        Assert.True(firefox.IsUpdateAvailable);
        Assert.StartsWith("Update auf", firefox.StateText);

        var install = catalog.FindControl<Button>("InstallButton")!;
        var uninstall = catalog.FindControl<Button>("UninstallButton")!;
        Assert.False(install.IsEnabled);
        git.IsSelected = true;
        Settle(window);
        Assert.True(install.IsEnabled);
        Assert.False(uninstall.IsVisible);
        firefox.IsSelected = true;
        Settle(window);
        Assert.True(uninstall.IsVisible, "an installed app can be removed");
        Assert.True(catalog.FindControl<Button>("UpdateButton")!.IsVisible);

        // The header's search filters the catalog in memory.
        window.SearchBox.Text = "visual";
        Settle(window);
        var found = ((IEnumerable<CatalogGroup>)catalog.FindControl<ItemsControl>("Groups")!.ItemsSource!).SelectMany(group => group.Items).ToList();
        Assert.Equal(["vscode"], found.Select(tile => tile.Key));
        window.SearchBox.Text = "";
        Settle(window);

        install.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        WaitUntil(window, () => JobService.All().Any(job => job.Package?.Key == "git"));
        Assert.Contains(JobService.All(), job => job is { Kind: ActionKind.Install, Origin: ActionOrigin.Device } && job.Package!.Key == "git");
        Assert.False(git.IsSelected, "a queued app is no longer chosen");
        window.IsExiting = true;
        window.Close();
    }

    [AvaloniaFact]
    public void Screenshots_of_every_page_in_light_and_dark()
    {
        using var data = Sample();
        JobService.Enqueue(JobService.NewJob(ActionKind.Install, ActionOrigin.Push, package: CatalogService.FindPackage(4), requestedBy: "Anna Albers"));
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            var window = Open();
            var suffix = theme == ThemeVariant.Dark ? "dark" : "light";
            WaitUntil(window, () => ((CatalogPage)window.PageHost.Content!).FindControl<ItemsControl>("Groups")!.ItemsSource is IEnumerable<CatalogGroup> groups && groups.Any());
            Save(window, $"desktop-catalog-{suffix}");
            window.ShowUpdates();
            Settle(window);
            Save(window, $"desktop-updates-{suffix}");
            window.ShowScripts();
            Settle(window);
            Save(window, $"desktop-scripts-{suffix}");
            window.ShowJobs();
            Settle(window);
            Save(window, $"desktop-jobs-{suffix}");
            window.ShowSettings(SettingsPage.CloudTab);
            Settle(window);
            Save(window, $"desktop-settings-cloud-{suffix}");
            window.ShowAbout();
            Settle(window);
            Save(window, $"desktop-about-{suffix}");
            window.IsExiting = true;
            window.Close();
        }

        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaFact]
    public void The_list_view_and_a_narrow_window_keep_everything_reachable()
    {
        using var data = Sample();
        var window = Open(800, 640);
        var catalog = (CatalogPage)window.PageHost.Content!;
        WaitUntil(window, () => catalog.FindControl<ItemsControl>("Rows")!.ItemsSource is IEnumerable<AppTileViewModel> rows && rows.Any());
        catalog.FindControl<Button>("ViewToggle")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.True(catalog.FindControl<ItemsControl>("Rows")!.IsVisible);
        Assert.False(SettingsService.Get().CatalogAsTiles);
        Save(window, "desktop-catalog-list-narrow");

        window.ToggleSidebar();
        Settle(window);
        Assert.True(window.IsSidebarCollapsed);
        Save(window, "desktop-menu-folded");
        window.ToggleSidebar();
        window.IsExiting = true;
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_or_cmd_numbers_switch_the_sections()
    {
        using var data = Sample();
        var window = Open();
        window.KeyPressQwerty(PhysicalKey.Digit2, CommandModifier);
        Settle(window);
        Assert.IsType<UpdatesPage>(window.PageHost.Content);
        window.KeyPressQwerty(PhysicalKey.Digit4, CommandModifier);
        Settle(window);
        Assert.IsType<JobsPage>(window.PageHost.Content);
        window.KeyPressQwerty(PhysicalKey.Comma, CommandModifier);
        Settle(window);
        Assert.IsType<SettingsPage>(window.PageHost.Content);
        window.KeyPressQwerty(PhysicalKey.Digit1, CommandModifier);
        Settle(window);
        Assert.IsType<CatalogPage>(window.PageHost.Content);
        window.IsExiting = true;
        window.Close();
    }

    [AvaloniaFact]
    public void The_bundle_window_shows_its_steps()
    {
        using var data = Sample();
        var window = new Bundle.BundleWindow { Width = 600, Height = 640 };
        window.Show();
        Settle(window);
        Save(window, "desktop-bundle-empty");
        window.Close();
    }

    private static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    /// <summary>A device with a catalog, two installed apps (one with an update) and a few jobs.</summary>
    private static TestData Sample()
    {
        var data = new TestData();
        CatalogService.Store(SampleCatalog.Create());
        if (OperatingSystem.IsMacOS())
        {
            data.Install("Firefox", "132.0", "/Applications/Firefox.app", "app");
            data.Install("Visual Studio Code", "1.95.3", "/Applications/Visual Studio Code.app", "app");
        }
        else
        {
            data.Install("Firefox (x64 de)", "132.0");
            data.Install("Visual Studio Code", "1.95.3");
        }

        Core.Agent.AgentClient.UseInProcess();
        return data;
    }

    private static MainWindow Open(double width = 1360, double height = 860)
    {
        SettingsService.Remember(settings => settings with { WindowWidth = width, WindowHeight = height });
        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        window.Width = width;
        window.Height = height;
        _ = AgentMonitor.RefreshNowAsync();
        Settle(window);
        return window;
    }

    /// <summary>Runs the dispatcher until the condition holds (the agent answers on the thread pool), at most five seconds.</summary>
    private static void WaitUntil(Window window, Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Settle(window);
        }
    }

    private static void Settle(Window window)
    {
        for (var round = 0; round < 10; round++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(20);
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Save(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotFolder is null)
        {
            return;
        }

        Directory.CreateDirectory(ShotFolder);
        frame.Save(Path.Combine(ShotFolder, $"{name}.png"));
    }
}
