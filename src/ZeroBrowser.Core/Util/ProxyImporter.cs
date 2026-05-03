using System.Text.RegularExpressions;
using ZeroBrowser.Core.Models;

namespace ZeroBrowser.Core.Util;

/// <summary>
/// Parses a multi-line proxy string into a list of <see cref="ProxyEntry"/>. Supported formats
/// (one per line):
/// <list type="bullet">
///   <item><c>host:port</c></item>
///   <item><c>host:port:username:password</c></item>
///   <item><c>username:password@host:port</c></item>
///   <item><c>protocol://host:port</c></item>
///   <item><c>protocol://username:password@host:port</c></item>
/// </list>
/// Protocol can be <c>http</c>, <c>https</c>, or <c>socks5</c>. Default is <c>http</c>.
/// Lines starting with <c>#</c> or <c>//</c> are treated as comments and skipped.
/// </summary>
public static class ProxyImporter
{
    public sealed record Line(int Number, string Text);
    public sealed record Failure(int LineNumber, string Text, string Reason);
    public sealed record Result(IReadOnlyList<ProxyEntry> Proxies, IReadOnlyList<Failure> Failures);

    private static readonly Regex SchemeRegex = new(@"^(?<scheme>https?|socks5)://", RegexOptions.IgnoreCase);

    public static Result Parse(string input)
    {
        var proxies = new List<ProxyEntry>();
        var failures = new List<Failure>();

        var lines = input.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0) continue;
            if (raw.StartsWith("#") || raw.StartsWith("//")) continue;

            try
            {
                var entry = ParseSingle(raw);
                proxies.Add(entry);
            }
            catch (FormatException ex)
            {
                failures.Add(new Failure(i + 1, raw, ex.Message));
            }
        }

        return new Result(proxies, failures);
    }

    private static ProxyEntry ParseSingle(string line)
    {
        // 1) Strip and capture optional scheme
        var type = ProxyType.Http;
        var schemeMatch = SchemeRegex.Match(line);
        if (schemeMatch.Success)
        {
            type = schemeMatch.Groups["scheme"].Value.ToLowerInvariant() switch
            {
                "https"  => ProxyType.Https,
                "socks5" => ProxyType.Socks5,
                _        => ProxyType.Http,
            };
            line = line.Substring(schemeMatch.Length);
        }

        // 2) Now line looks like one of:
        //    host:port
        //    host:port:user:pass
        //    user:pass@host:port
        string? user = null, pass = null;
        if (line.Contains('@'))
        {
            var atIdx = line.LastIndexOf('@');
            var creds = line.Substring(0, atIdx);
            var hostPort = line.Substring(atIdx + 1);
            var credParts = creds.Split(':', 2);
            if (credParts.Length != 2) throw new FormatException("Invalid 'user:pass@' segment");
            user = credParts[0]; pass = credParts[1];
            line = hostPort;
        }

        var parts = line.Split(':');
        string host;
        int port;
        if (parts.Length == 2)
        {
            host = parts[0];
            if (!int.TryParse(parts[1], out port)) throw new FormatException("Port must be a number");
        }
        else if (parts.Length == 4)
        {
            // host:port:user:pass
            host = parts[0];
            if (!int.TryParse(parts[1], out port)) throw new FormatException("Port must be a number");
            user = parts[2];
            pass = parts[3];
        }
        else
        {
            throw new FormatException("Expected host:port or host:port:user:pass");
        }

        if (string.IsNullOrWhiteSpace(host)) throw new FormatException("Host cannot be empty");
        if (port <= 0 || port > 65535) throw new FormatException($"Port {port} out of range");

        return new ProxyEntry
        {
            Id = Guid.NewGuid(),
            Type = type,
            Host = host.Trim(),
            Port = port,
            Username = user,
            Password = pass
        };
    }
}
