using System.Net.Sockets;
using System.Text;

namespace ZeroBrowser.Browser.Tor;

/// <summary>
/// Minimal Tor control-port client (control-spec). Supports cookie authentication
/// and signal dispatch — just enough for NEWNYM (new identity) and SHUTDOWN.
/// </summary>
public sealed class TorControlClient : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    /// <summary>Connects and authenticates with the hex control-auth cookie.</summary>
    public static async Task<TorControlClient> ConnectAsync(
        string host, int port, string cookieHex, CancellationToken ct = default)
    {
        var c = new TorControlClient();
        try
        {
            await c.OpenAsync(host, port, ct).ConfigureAwait(false);
            var banner = await c.ReadLineAsync(ct).ConfigureAwait(false);
            if (banner is null || !banner.StartsWith("250", StringComparison.Ordinal))
                throw new TorControlException($"Unexpected control banner: {banner ?? "<none>"}");

            var reply = await c.CommandAsync($"AUTHENTICATE {cookieHex}", ct).ConfigureAwait(false);
            if (!reply.IsOk)
                throw new TorControlException($"AUTHENTICATE failed: {reply.Line}");
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    private async Task OpenAsync(string host, int port, CancellationToken ct)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        _stream = _client.GetStream();
    }

    /// <summary>Sends a command, returns the final reply line (e.g. "250 OK").</summary>
    public async Task<TorReply> CommandAsync(string command, CancellationToken ct = default)
    {
        var line = $"{command}\r\n";
        var bytes = Encoding.ASCII.GetBytes(line);
        await _stream!.WriteAsync(bytes, ct).ConfigureAwait(false);

        string last = string.Empty;
        while (true)
        {
            var reply = await ReadLineAsync(ct).ConfigureAwait(false);
            if (reply is null) throw new TorControlException("Control connection closed mid-reply.");
            last = reply;
            // A 3-char code followed by space marks the final line of the reply.
            if (reply.Length >= 4 && reply[3] == ' ') break;
        }
        return new TorReply(last.StartsWith("250", StringComparison.Ordinal), last);
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct = default)
    {
        var buf = new List<byte>(64);
        while (true)
        {
            var b = new byte[1];
            int n = await _stream!.ReadAsync(b, ct).ConfigureAwait(false);
            if (n == 0) return buf.Count == 0 ? null : Encoding.UTF8.GetString(buf.ToArray());
            if (b[0] == '\n')
            {
                var s = Encoding.UTF8.GetString(buf.ToArray());
                return s.EndsWith('\r') ? s[..^1] : s;
            }
            buf.Add(b[0]);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }
}

public readonly record struct TorReply(bool IsOk, string Line);

public sealed class TorControlException : Exception
{
    public TorControlException(string message) : base(message) { }
}
