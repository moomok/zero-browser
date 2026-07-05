using System.Text.Json;
using ZeroBrowser.Core.Models;

namespace ZeroBrowser.Storage.Cookies;

/// <summary>
/// Per-profile cookie persistence. Stores imported cookies as JSON in
/// <c>{profile.StoragePath}/imported-cookies.json</c>. The launcher reads this
/// file on launch and applies the cookies before navigation.
/// </summary>
public static class CookieStore
{
    public const string FileName = "imported-cookies.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string PathFor(string profileStorageDir) => Path.Combine(profileStorageDir, FileName);

    public static IReadOnlyList<CookieRecord> Load(string profileStorageDir)
    {
        var path = PathFor(profileStorageDir);
        if (!File.Exists(path)) return Array.Empty<CookieRecord>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<CookieRecord>>(json) ?? new();
        }
        catch
        {
            return Array.Empty<CookieRecord>();
        }
    }

    public static void Save(string profileStorageDir, IEnumerable<CookieRecord> cookies)
    {
        Directory.CreateDirectory(profileStorageDir);
        var path = PathFor(profileStorageDir);
        File.WriteAllText(path, JsonSerializer.Serialize(cookies, JsonOpts));
        // Restrict file permissions so other OS users can't read session cookies.
        // On Windows this is best-effort; on Linux/macOS it sets mode 0600.
        try
        {
            var info = new FileInfo(path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { /* best-effort */ }
    }

    public static void Clear(string profileStorageDir)
    {
        var path = PathFor(profileStorageDir);
        if (File.Exists(path)) File.Delete(path);
    }
}
