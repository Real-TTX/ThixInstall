using System.Text;

namespace ThixInstall.Core.Installers;

/// <summary>What an installer engine needs for one job: the cloud the installers come from, progress and cancel.</summary>
internal sealed class JobContext(string? cloudUrl, string? token, Action<string?, double?> report, CancellationToken cancellation)
{
    private readonly StringBuilder _steps = new();

    /// <summary>The cloud of the catalog or bundle (the download paths of the plans belong to it); null = none.</summary>
    public string? CloudUrl { get; } = cloudUrl;

    public string? Token { get; } = token;

    public CancellationToken Cancellation { get; } = cancellation;

    /// <summary>Tells the UI what happens now ("Lade herunter …") and how far a download is (0..1, null = unknown).</summary>
    public void Report(string? status, double? progress = null) => report(status, progress);

    /// <summary>A line of ThixInstall's own in the output of the job (what it did before the installer ran).</summary>
    public void Step(string line) => _steps.AppendLine("› " + line);

    /// <summary>The steps and the installer's output, as one text for the history.</summary>
    public string Combine(string? output)
    {
        var text = _steps.ToString();
        return string.IsNullOrWhiteSpace(output) ? text.TrimEnd() : text + output;
    }
}

/// <summary>How a job ended.</summary>
internal sealed record JobOutcome(bool Succeeded, int? ExitCode, string Output, string? Message = null, bool Cancelled = false)
{
    public static JobOutcome Fail(JobContext context, string message, string? output = null, int? exitCode = null) =>
        new(false, exitCode, context.Combine((output is null ? "" : output + Environment.NewLine) + message), message);
}
