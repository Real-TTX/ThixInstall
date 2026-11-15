using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace ThixInstall.Shared;

/// <summary>
/// What a downloaded bundle carries, packed into the program itself: the cloud it came from, the bundle's token and the
/// whole configuration of the bundle (<see cref="Bundle"/>: apps, scripts, their install plans, ThixInstall itself and the
/// profile). The program installs from that configuration; only a ticket without one fetches it (<see cref="ApiContract.Bundle"/>).
///
/// Windows: the cloud appends the ticket to the ThixInstall setup it hands out (the .NET single-file host ignores bytes
/// after its bundle): [program][JSON, UTF-8][length of the JSON: 4 bytes little endian][<see cref="Magic"/>].
/// macOS: the zip of the app has a file "&lt;name&gt;<see cref="FileExtension"/>" with the same JSON next to the app.
/// </summary>
public sealed record BundleTicket(string CloudUrl, string Token, BundleDto? Bundle = null)
{
    /// <summary>The last bytes of a Windows program that carries a ticket.</summary>
    public const string Magic = "THIXINSTALL-BNDL";

    /// <summary>The file type of a ticket next to the macOS app (and the argument of "ThixInstall.exe &lt;file&gt;").</summary>
    public const string FileExtension = ".thixinstall";

    private const int MaxJsonBytes = 4 * 1024 * 1024;

    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(Magic);

    public string ToJson() => JsonSerializer.Serialize(this, ApiContract.Json);

    public static BundleTicket? FromJson(string json)
    {
        try
        {
            var ticket = JsonSerializer.Deserialize<BundleTicket>(json, ApiContract.Json);
            return ticket is not null && IsValid(ticket) ? ticket : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The bytes to append to a program.</summary>
    public byte[] ToTrailer()
    {
        var json = Encoding.UTF8.GetBytes(ToJson());
        var trailer = new byte[json.Length + 4 + MagicBytes.Length];
        json.CopyTo(trailer, 0);
        BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(json.Length, 4), json.Length);
        MagicBytes.CopyTo(trailer, json.Length + 4);
        return trailer;
    }

    /// <summary>Reads the ticket appended to a program; null when it carries none.</summary>
    public static BundleTicket? ReadTrailer(string programFile)
    {
        try
        {
            using var stream = new FileStream(programFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadTrailer(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static BundleTicket? ReadTrailer(Stream stream)
    {
        var footerLength = 4 + MagicBytes.Length;
        if (!stream.CanSeek || stream.Length < footerLength)
        {
            return null;
        }

        Span<byte> footer = stackalloc byte[footerLength];
        stream.Seek(-footerLength, SeekOrigin.End);
        stream.ReadExactly(footer);
        if (!footer[4..].SequenceEqual(MagicBytes))
        {
            return null;
        }

        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(footer[..4]);
        if (jsonLength <= 0 || jsonLength > MaxJsonBytes || jsonLength > stream.Length - footerLength)
        {
            return null;
        }

        var json = new byte[jsonLength];
        stream.Seek(-footerLength - jsonLength, SeekOrigin.End);
        stream.ReadExactly(json);
        return FromJson(Encoding.UTF8.GetString(json));
    }

    /// <summary>The length of the program without an appended ticket (the whole length when it carries none).</summary>
    public static long ProgramLength(Stream stream)
    {
        var footerLength = 4 + MagicBytes.Length;
        if (!stream.CanSeek || stream.Length < footerLength)
        {
            return stream.Length;
        }

        Span<byte> footer = stackalloc byte[footerLength];
        stream.Seek(-footerLength, SeekOrigin.End);
        stream.ReadExactly(footer);
        if (!footer[4..].SequenceEqual(MagicBytes))
        {
            return stream.Length;
        }

        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(footer[..4]);
        return jsonLength <= 0 || jsonLength > MaxJsonBytes || jsonLength > stream.Length - footerLength
            ? stream.Length
            : stream.Length - footerLength - jsonLength;
    }

    /// <summary>An absolute http(s) address and a token of letters and digits only.</summary>
    public static bool IsValid(BundleTicket ticket) =>
        Uri.TryCreate(ticket.CloudUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        ticket.Token.Length is >= 8 and <= 64 &&
        ticket.Token.All(char.IsAsciiLetterOrDigit);
}
