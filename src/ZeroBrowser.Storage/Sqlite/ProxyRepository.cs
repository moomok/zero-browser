using Dapper;
using Microsoft.Data.Sqlite;
using ZeroBrowser.Core.Models;
using ZeroBrowser.Storage.Crypto;

namespace ZeroBrowser.Storage.Sqlite;

/// <summary>
/// Persists <see cref="ProxyEntry"/> to SQLite. Passwords are encrypted at rest with the
/// app-level <see cref="SecretBox"/> derived from the master password — never stored
/// in plaintext on disk.
/// </summary>
public sealed class ProxyRepository
{
    private readonly string _connectionString;
    private readonly SecretBox _box;

    public ProxyRepository(string dbPath, SecretBox box)
    {
        _connectionString = $"Data Source={dbPath}";
        _box = box;
        using var conn = Open();
        Schema.Apply(conn);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public IReadOnlyList<ProxyEntry> ListAll()
    {
        using var conn = Open();
        var rows = conn.Query<ProxyRow>("SELECT * FROM proxies ORDER BY host, port").ToList();
        return rows.Select(Map).ToList();
    }

    public ProxyEntry? Get(Guid id)
    {
        using var conn = Open();
        var row = conn.QueryFirstOrDefault<ProxyRow>(
            "SELECT * FROM proxies WHERE id = @id", new { id = id.ToString() });
        return row is null ? null : Map(row);
    }

    public void Insert(ProxyEntry p)
    {
        using var conn = Open();
        conn.Execute(
            """
            INSERT INTO proxies
              (id, type, host, port, username, password_enc, country, city, last_check_at, status, ip_last_seen)
            VALUES
              (@Id, @Type, @Host, @Port, @Username, @PasswordEnc, @Country, @City, @LastCheckAt, @Status, @IpLastSeen);
            """, new
            {
                Id = p.Id.ToString(),
                Type = p.Type.ToString(),
                p.Host,
                p.Port,
                p.Username,
                PasswordEnc = p.Password is null ? null : _box.Encrypt(System.Text.Encoding.UTF8.GetBytes(p.Password)),
                p.Country,
                p.City,
                LastCheckAt = p.LastCheckAt?.ToUnixTimeSeconds(),
                p.Status,
                p.IpLastSeen
            });
    }

    public void InsertMany(IEnumerable<ProxyEntry> proxies)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var p in proxies)
        {
            conn.Execute(
                """
                INSERT INTO proxies
                  (id, type, host, port, username, password_enc, country, city, last_check_at, status, ip_last_seen)
                VALUES
                  (@Id, @Type, @Host, @Port, @Username, @PasswordEnc, @Country, @City, @LastCheckAt, @Status, @IpLastSeen);
                """, new
                {
                    Id = p.Id.ToString(),
                    Type = p.Type.ToString(),
                    p.Host,
                    p.Port,
                    p.Username,
                    PasswordEnc = p.Password is null ? null : _box.Encrypt(System.Text.Encoding.UTF8.GetBytes(p.Password)),
                    p.Country,
                    p.City,
                    LastCheckAt = p.LastCheckAt?.ToUnixTimeSeconds(),
                    p.Status,
                    p.IpLastSeen
                }, tx);
        }
        tx.Commit();
    }

    public void Delete(Guid id)
    {
        using var conn = Open();
        conn.Execute("DELETE FROM proxies WHERE id = @id", new { id = id.ToString() });
    }

    private ProxyEntry Map(ProxyRow row)
    {
        string? password = null;
        if (row.password_enc is { Length: > 0 })
        {
            try { password = System.Text.Encoding.UTF8.GetString(_box.Decrypt(row.password_enc)); }
            catch { password = null; }
        }
        return new ProxyEntry
        {
            Id = Guid.Parse(row.id),
            Type = Enum.Parse<ProxyType>(row.type, ignoreCase: true),
            Host = row.host,
            Port = (int)row.port,
            Username = row.username,
            Password = password,
            Country = row.country,
            City = row.city,
            LastCheckAt = row.last_check_at is null ? null : DateTimeOffset.FromUnixTimeSeconds(row.last_check_at.Value),
            Status = row.status,
            IpLastSeen = row.ip_last_seen
        };
    }

    private sealed record ProxyRow(
        string id, string type, string host, long port, string? username,
        byte[]? password_enc, string? country, string? city,
        long? last_check_at, string? status, string? ip_last_seen);
}
