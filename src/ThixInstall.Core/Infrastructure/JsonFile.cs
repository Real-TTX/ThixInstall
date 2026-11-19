using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThixInstall.Core.Infrastructure;

/// <summary>
/// Reads and writes the JSON files of ThixInstall (settings, agent settings, catalog, jobs). Writes go to a temporary file
/// first and replace the old one, so a crash never leaves half a file; a file that cannot be read is kept aside.
/// </summary>
internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new(Shared.ApiContract.Json)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (JsonException exception)
        {
            Log.Warning($"{Path.GetFileName(path)} could not be read ({exception.Message}); using the defaults");
            TryCopy(path, path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}");
            return null;
        }
        catch (IOException exception)
        {
            Log.Warning($"{Path.GetFileName(path)} could not be read: {exception.Message}");
            return null;
        }
    }

    public static T? Deserialize<T>(Stream json) where T : class => JsonSerializer.Deserialize<T>(json, Options);

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, Options));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void TryCopy(string from, string to)
    {
        try
        {
            File.Copy(from, to, overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
