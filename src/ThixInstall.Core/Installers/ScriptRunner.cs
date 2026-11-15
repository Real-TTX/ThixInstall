using System.Text;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;

namespace ThixInstall.Core.Installers;

/// <summary>
/// Runs a script of the catalog: written to a file in the agent's script folder, started by its shell, removed afterwards.
/// Windows: PowerShell (Windows PowerShell 5.1, execution policy bypassed for this file) or cmd. macOS: bash or zsh; a
/// script in the system context asks for an administrator when ThixInstall does not run as root.
/// The exit code decides: 0 = success.
/// </summary>
internal static class ScriptRunner
{
    public static async Task<JobOutcome> RunAsync(ScriptDto script, JobContext context)
    {
        if (script.Os != AppInfo.Os)
        {
            return JobOutcome.Fail(context, $"Dieses Skript ist für {(script.Os == TargetOs.MacOS ? "macOS" : "Windows")}.");
        }

        var timeout = TimeSpan.FromSeconds(script.TimeoutSeconds > 0 ? script.TimeoutSeconds : 30 * 60);
        Directory.CreateDirectory(AppPaths.ScriptDirectory);
        var file = Path.Combine(AppPaths.ScriptDirectory, $"{Guid.NewGuid():N}{Extension(script.Shell)}");
        try
        {
            WriteScript(file, script);
            context.Step($"Führe „{script.Name}“ aus ({ShellName(script.Shell)}, {(script.Context == ScriptContext.User ? "Benutzer" : "System")})");
            context.Report($"Führe „{script.Name}“ aus …");
            var result = await StartAsync(script, file, timeout, context);
            if (result.Cancelled)
            {
                return new JobOutcome(false, null, context.Combine(result.Output), "Abgebrochen.", Cancelled: true);
            }

            if (result.Succeeded())
            {
                return new JobOutcome(true, 0, context.Combine(result.Output));
            }

            var reason = result.TimedOut ? "Zeitüberschreitung." : result.ExitCode is { } code ? $"Das Skript endete mit Code {code}." : "Das Skript konnte nicht gestartet werden.";
            return new JobOutcome(false, result.ExitCode, context.Combine(result.Output), reason);
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static Task<ProcessResult> StartAsync(ScriptDto script, string file, TimeSpan timeout, JobContext context)
    {
        switch (script.Shell)
        {
            case ScriptShell.PowerShell:
                return ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file], timeout, context.Cancellation);
            case ScriptShell.Cmd:
                return ProcessRunner.RunAsync("cmd.exe", null, timeout, context.Cancellation, rawArguments: $"/d /c \"{file}\"");
            default:
                var shell = script.Shell == ScriptShell.Zsh ? "/bin/zsh" : "/bin/bash";
                if (OperatingSystem.IsMacOS() && script.Context == ScriptContext.System && !Elevation.IsElevated)
                {
                    return MacInstaller.RunAsAdministratorAsync($"{shell} {MacInstaller.ShellQuote(file)}", context, timeout);
                }

                return ProcessRunner.RunAsync(shell, [file], timeout, context.Cancellation);
        }
    }

    private static void WriteScript(string file, ScriptDto script)
    {
        switch (script.Shell)
        {
            case ScriptShell.PowerShell:
                // Windows PowerShell 5.1 reads a file without BOM as ANSI: umlauts need the BOM.
                File.WriteAllText(file, script.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                break;
            case ScriptShell.Cmd:
                File.WriteAllText(file, "@chcp 65001 >nul\r\n" + script.Content.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
                break;
            default:
                File.WriteAllText(file, script.Content.Replace("\r\n", "\n"), new UTF8Encoding(false));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                break;
        }
    }

    public static string ShellName(ScriptShell shell) => shell switch
    {
        ScriptShell.PowerShell => "PowerShell",
        ScriptShell.Cmd => "Eingabeaufforderung",
        ScriptShell.Zsh => "zsh",
        _ => "bash",
    };

    private static string Extension(ScriptShell shell) => shell switch
    {
        ScriptShell.PowerShell => ".ps1",
        ScriptShell.Cmd => ".cmd",
        _ => ".sh",
    };

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
