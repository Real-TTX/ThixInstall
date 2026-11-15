using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ThixInstall.Core.Platform;

/// <summary>How a program ended: its exit code (null when it was stopped) and the end of what it wrote.</summary>
internal sealed record ProcessResult(int? ExitCode, string Output, bool TimedOut, bool Cancelled)
{
    public bool Succeeded(params int[] successCodes) => ExitCode is { } code && (code == 0 || successCodes.Contains(code));
}

/// <summary>
/// Starts installers, package managers and scripts: no window, output of both streams captured (only its end is kept),
/// stopped with its whole process tree on timeout or cancel.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>How much of the output is kept (characters).</summary>
    public const int OutputLimit = 32_000;

    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string>? arguments, TimeSpan timeout,
        CancellationToken cancellationToken, string? workingDirectory = null, string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null, string? rawArguments = null, Action<string>? onLine = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (rawArguments is not null)
        {
            startInfo.Arguments = rawArguments;
        }
        else
        {
            foreach (var argument in arguments ?? [])
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        var output = new OutputTail(OutputLimit);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(null, $"{fileName} konnte nicht gestartet werden.", false, false);
            }
        }
        catch (Win32Exception exception)
        {
            return new ProcessResult(null, $"{fileName} konnte nicht gestartet werden: {exception.Message}", false, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            process.WaitForExit(); // flushes the asynchronous readers
            return new ProcessResult(process.ExitCode, output.ToString(), false, false);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            var cancelled = cancellationToken.IsCancellationRequested;
            output.Append(cancelled ? "— abgebrochen —" : $"— nach {timeout.TotalMinutes:0} Minuten abgebrochen —");
            return new ProcessResult(null, output.ToString(), !cancelled, cancelled);
        }

        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            output.Append(line);
            onLine?.Invoke(line);
        }
    }

    /// <summary>Runs a short command and returns its standard output (empty when it failed).</summary>
    public static string ReadOutput(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "";
            }

            var text = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                Stop(process);
                return "";
            }

            return process.ExitCode == 0 ? text.Result : "";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // already gone
        }
    }

    /// <summary>Keeps the last characters of a long output, line by line.</summary>
    private sealed class OutputTail(int limit)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private bool _cut;

        public void Append(string line)
        {
            lock (_gate)
            {
                _text.AppendLine(line);
                if (_text.Length > limit * 2)
                {
                    _text.Remove(0, _text.Length - limit);
                    _cut = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                var text = _text.ToString();
                if (text.Length > limit)
                {
                    text = text[^limit..];
                    _cut = true;
                }

                return (_cut ? "…" + Environment.NewLine : "") + text.TrimEnd();
            }
        }
    }
}
