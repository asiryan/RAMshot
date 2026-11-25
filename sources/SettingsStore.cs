using System.Text.Json;

namespace RAMshot;

internal sealed class SettingsStore
{
    private const int MaxSettingsBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string path;

    public SettingsStore(string? settingsPath = null)
    {
        path = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAMshot", "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxSettingsBytes) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(file, JsonOptions);
            return settings is not null && settings.Validate() is null ? settings : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or System.Security.SecurityException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? error = settings.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(settings));
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, settings, JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
