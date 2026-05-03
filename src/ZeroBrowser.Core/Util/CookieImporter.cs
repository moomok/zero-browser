using System.Text.Json;
using ZeroBrowser.Core.Models;

namespace ZeroBrowser.Core.Util;

/// <summary>
/// Parses cookies from either a JSON export (Puppeteer / Playwright / EditThisCookie)
/// or the classic Netscape HTTP Cookie File format used by curl / wget.
/// </summary>
public static class CookieImporter
{
    public sealed record Result(IReadOnlyList<CookieRecord> Cookies, string Format, IReadOnlyList<string> Warnings);

    public static Result Parse(string input)
    {
        var trimmed = input.TrimStart();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            return ParseJson(trimmed);
        }
        // Netscape: starts with comment or has tab-separated lines
        if (trimmed.StartsWith('#') || trimmed.Contains('\t'))
        {
            return ParseNetscape(input);
        }
        throw new FormatException("Unrecognized cookie format. Expected JSON array of cookies or Netscape cookie file.");
    }

    private static Result ParseJson(string input)
    {
        var warnings = new List<string>();
        var cookies = new List<CookieRecord>();

        using var doc = JsonDocument.Parse(input);
        var root = doc.RootElement;
        IEnumerable<JsonElement> items = root.ValueKind switch
        {
            JsonValueKind.Array  => root.EnumerateArray(),
            JsonValueKind.Object => new[] { root },
            _ => throw new FormatException("Top-level JSON must be an array or object")
        };

        int i = 0;
        foreach (var el in items)
        {
            i++;
            try
            {
                cookies.Add(ParseJsonCookie(el));
            }
            catch (Exception ex)
            {
                warnings.Add($"Cookie #{i}: {ex.Message}");
            }
        }

        return new Result(cookies, "json", warnings);
    }

    private static CookieRecord ParseJsonCookie(JsonElement el)
    {
        string Required(string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!
                : throw new FormatException($"missing required string field '{name}'");

        string? Optional(string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        bool? OptionalBool(string name) =>
            el.TryGetProperty(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?)null
            } : null;

        long? OptionalLong(params string[] names)
        {
            foreach (var n in names)
            {
                if (el.TryGetProperty(n, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i;
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)d;
                }
            }
            return null;
        }

        var sameSite = Optional("sameSite");
        if (sameSite is not null)
        {
            sameSite = sameSite.ToLowerInvariant() switch
            {
                "lax" or "lax_mode"     => "Lax",
                "strict"                 => "Strict",
                "none" or "no_restriction" => "None",
                "unspecified"            => null,
                _                        => null
            };
        }

        return new CookieRecord
        {
            Name        = Required("name"),
            Value       = el.GetProperty("value").ValueKind == JsonValueKind.String
                            ? el.GetProperty("value").GetString()!
                            : throw new FormatException("missing value"),
            Domain      = Required("domain"),
            Path        = Optional("path") ?? "/",
            HttpOnly    = OptionalBool("httpOnly"),
            Secure      = OptionalBool("secure"),
            SameSite    = sameSite,
            ExpiresUnix = OptionalLong("expires", "expirationDate")
        };
    }

    private static Result ParseNetscape(string input)
    {
        var warnings = new List<string>();
        var cookies = new List<CookieRecord>();

        var lines = input.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            // skip blanks + comments
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.TrimStart().StartsWith("#")) continue;

            var parts = raw.Split('\t');
            if (parts.Length < 6 || parts.Length > 7)
            {
                warnings.Add($"Line {i + 1}: expected 7 tab-separated fields, got {parts.Length}");
                continue;
            }
            // Netscape layout: domain  flag  path  secure  expires  name  value
            try
            {
                var domain = parts[0];
                var path   = parts[2];
                var secure = string.Equals(parts[3], "TRUE", StringComparison.OrdinalIgnoreCase);
                long? expires = long.TryParse(parts[4], out var exp) && exp > 0 ? exp : null;
                var name   = parts[5];
                var value  = parts.Length == 7 ? parts[6] : string.Empty;

                cookies.Add(new CookieRecord
                {
                    Name = name,
                    Value = value,
                    Domain = domain,
                    Path = string.IsNullOrEmpty(path) ? "/" : path,
                    Secure = secure,
                    ExpiresUnix = expires
                });
            }
            catch (Exception ex)
            {
                warnings.Add($"Line {i + 1}: {ex.Message}");
            }
        }

        return new Result(cookies, "netscape", warnings);
    }
}
