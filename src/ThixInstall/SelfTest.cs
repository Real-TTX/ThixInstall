using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;

namespace ThixInstall;

/// <summary>
/// "ThixInstall.exe --self-test &lt;report file&gt;": the published program checks itself – everything trimming or
/// single-file packaging could break silently: JSON of settings, contract, jobs and update manifest, the bundle ticket,
/// the protected token store, the installed apps of the system, Windows shortcuts (COM), and the windows of the app with
/// their bindings. It runs in a fresh temporary data folder (Program sets it), never touches the user's data, writes a
/// report and ends with exit code 0 (all passed) or 1. CI runs it on every Windows build.
/// </summary>
internal static class SelfTest
{
    public const string Argument = "--self-test";

    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder($"ThixInstall {AppInfo.Version} self test, data folder {AppPaths.DataDirectory}{Environment.NewLine}");
        var failures = 0;

        async Task Check(string name, Func<Task<string>> check)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var note = await check();
                report.AppendLine($"ok    {name} ({watch.ElapsedMilliseconds} ms){(note.Length > 0 ? " – " + note : "")}");
            }
            catch (Exception exception)
            {
                failures++;
                report.AppendLine($"FAIL  {name}: {exception}");
            }
        }

        await Check("settings (JSON)", () =>
        {
            SettingsService.Update(settings => settings with { Theme = ThemeChoice.Dark, CatalogAsTiles = false });
            SettingsStore.UpdateAgent(settings => settings with { AutoUpdateApps = true });
            SettingsStore.Reset();
            Ensure(SettingsService.Get() is { Theme: ThemeChoice.Dark, CatalogAsTiles: false }, "app settings did not survive a save");
            Ensure(SettingsStore.ReadAgent().AutoUpdateApps, "agent settings did not survive a save");
            return Task.FromResult("");
        });

        await Check("update manifest (JSON)", () =>
        {
            const string json = """{"version":"0.1.9-20261115","channel":"release","buildNumber":9,"setup":"ThixInstallSetup.exe","sha256":"AB","packages":{"win-x64":{"file":"ThixInstallSetup.exe","sha256":"AB"}}}""";
            var manifest = new HttpResponseMessage { Content = new StringContent(json, Encoding.UTF8, "application/json") }.Content
                .ReadFromJsonAsync<UpdateManifest>().GetAwaiter().GetResult();
            Ensure(manifest is { BuildNumber: 9 } && UpdateService.PackageFor(manifest, "win-x64")?.File == "ThixInstallSetup.exe", "manifest not read");
            return Task.FromResult("");
        });

        await Check("contract (JSON)", () =>
        {
            var package = new PackageDto
            {
                Id = 7, Key = "firefox", Name = "Firefox", Category = "Browser", LatestVersion = "133.0",
                Install = new InstallPlan { Method = InstallMethod.Winget, PackageId = "Mozilla.Firefox", InstallerKind = InstallerKind.Exe, DownloadUrl = ApiContract.Download(3) },
            };
            var catalog = new CatalogResponse(TargetOs.Windows, CpuArchitecture.X64, [package], [], "local", DateTimeOffset.UtcNow);
            var back = JsonSerializer.Deserialize<CatalogResponse>(JsonSerializer.Serialize(catalog, ApiContract.Json), ApiContract.Json);
            Ensure(back?.Packages[0].Install?.InstallerKind == InstallerKind.Exe && back.Packages[0].Install!.PackageId == "Mozilla.Firefox", "catalog not read back");
            return Task.FromResult("");
        });

        await Check("install options (installer.json, arguments)", () =>
        {
            Ensure(InstallerOptions.Config is { Autostart: not null, DesktopShortcut: not null, AutomaticUpdates: not null }, "installer.json not read");
            var request = InstallerOptions.Parse([InstallerOptions.QuietArgument, "--autostart=no", "--desktop-shortcut=yes"]);
            Ensure(request is { Action: SetupAction.Install, Choices: { Autostart: false, DesktopShortcut: true } }, "arguments not read");
            var config = new InstallerConfig { Autostart = new InstallerSwitch { Default = true }, FileTypes = [new InstallerFileType { Extension = ".md", Name = "Notiz" }] };
            var back = JsonSerializer.Deserialize<PackageDto>(JsonSerializer.Serialize(new PackageDto
            {
                Id = 1, Key = "thixnote", Name = "ThixNote", Category = "Thix-Apps", Installer = config,
                Install = new InstallPlan { Method = InstallMethod.ThixApp, Choices = new InstallChoices(Autostart: false, FileTypes: [".md"]) },
            }, ApiContract.Json), ApiContract.Json);
            Ensure(back?.Installer?.FileTypes is [{ Extension: ".md" }] && back.Install?.Choices?.Autostart == false, "install options of a Thix app not read back");
            Ensure(string.Join(' ', ThixAppSetup.WindowsArguments(back!.Installer, back.Install!.Choices, isUpdate: false)) == "--quiet --autostart=no --file-types=.md",
                "arguments of a Thix app wrong");
            return Task.FromResult("");
        });

        await Check("jobs (JSON)", () =>
        {
            var job = JobService.NewJob(ActionKind.Install, ActionOrigin.Device, title: "Selbsttest");
            JsonFile.Write(Path.Combine(AppPaths.DataDirectory, "jobs-test.json"), new List<JobRecord> { job });
            var back = JsonFile.Read<List<JobRecord>>(Path.Combine(AppPaths.DataDirectory, "jobs-test.json"));
            Ensure(back is [{ Title: "Selbsttest" }], "jobs not read back");
            return Task.FromResult("");
        });

        await Check("bundle ticket", () =>
        {
            var file = Path.Combine(AppPaths.DataDirectory, "ticket.bin");
            var ticket = new BundleTicket("https://install.example.com", "AbCdEf123456");
            File.WriteAllBytes(file, [.. new byte[4096], .. ticket.ToTrailer()]);
            Ensure(BundleTicket.ReadTrailer(file) == ticket, "ticket not read back");
            using var stream = File.OpenRead(file);
            Ensure(BundleTicket.ProgramLength(stream) == 4096, "program length wrong");
            return Task.FromResult("");
        });

        await Check("token store", () =>
        {
            var stored = TokenStore.Save("self-test", "geheim");
            Ensure(TokenStore.Read("self-test", stored) == "geheim", "token not read back");
            TokenStore.Delete("self-test");
            return Task.FromResult("");
        });

        await Check("installed apps", () => Task.FromResult($"{InstalledApps.Read().Count} found"));

        if (OperatingSystem.IsWindows())
        {
            await Check("shortcut (COM)", () =>
            {
                var link = Path.Combine(AppPaths.DataDirectory, "ThixInstall.lnk");
                Core.Platform.Windows.ShellLink.Write(link, App.ExecutablePath, AppPaths.DataDirectory, "Selbsttest", App.ExecutablePath);
                Ensure(File.Exists(link) && new FileInfo(link).Length > 0, "no .lnk written");
                return Task.FromResult("");
            });
        }

        await Check("windows and bindings", async () =>
        {
            AgentClient.UseInProcess();
            var window = new MainWindow();
            window.ShowFromTray();
            await Settle();
            window.ShowUpdates();
            await Settle();
            window.ShowScripts();
            await Settle();
            window.ShowJobs();
            await Settle();
            window.ShowSettings(Pages.SettingsPage.CloudTab);
            await Settle();
            window.ShowAbout();
            await Settle();
            window.ShowCatalog();
            await Settle();

            var bundle = new Bundle.BundleWindow();
            bundle.Show();
            await Settle();
            bundle.Close();
            if (OperatingSystem.IsWindows())
            {
                var setup = new Setup.SetupWindow();
                setup.Show();
                await Settle();
                setup.Close();
            }

            ProblemReports.WriteHang(TimeSpan.FromSeconds(9), withDump: false);
            Dialogs.ProblemReportDialog.ShowUnseen()?.Close();
            window.IsExiting = true;
            window.Close();
            return "";
        });

        report.AppendLine(failures == 0 ? "PASSED" : $"FAILED ({failures})");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);

        // The temporary data folder goes again (unless the report was written into it).
        if (!Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(AppPaths.DataDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task Settle()
    {
        for (var round = 0; round < 10; round++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(30);
        }
    }
}
