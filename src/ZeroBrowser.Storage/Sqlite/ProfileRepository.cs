using System.Text.Json;
using ZeroBrowser.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ZeroBrowser.Storage.Sqlite;

public sealed class ProfileRepository
{
    private readonly string _connectionString;

    public ProfileRepository(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        using var conn = Open();
        Schema.Apply(conn);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public IReadOnlyList<Profile> ListAll()
    {
        using var conn = Open();
        var rows = conn.Query<ProfileRow>("SELECT * FROM profiles ORDER BY created_at DESC").ToList();
        return rows.Select(Map).ToList();
    }

    public Profile? Get(Guid id)
    {
        using var conn = Open();
        var row = conn.QueryFirstOrDefault<ProfileRow>("SELECT * FROM profiles WHERE id = @id", new { id = id.ToString() });
        return row is null ? null : Map(row);
    }

    public void Insert(Profile p)
    {
        using var conn = Open();
        conn.Execute(
            """
            INSERT INTO profiles
              (id, name, notes, group_id, tags, fingerprint_seed, proxy_id, storage_path, created_at, last_used_at)
            VALUES
              (@Id, @Name, @Notes, @GroupId, @Tags, @FingerprintSeed, @ProxyId, @StoragePath, @CreatedAt, @LastUsedAt);
            """, new
            {
                Id = p.Id.ToString(),
                p.Name,
                p.Notes,
                p.GroupId,
                Tags = JsonSerializer.Serialize(p.Tags),
                p.FingerprintSeed,
                ProxyId = p.ProxyId?.ToString(),
                p.StoragePath,
                CreatedAt = p.CreatedAt.ToUnixTimeSeconds(),
                LastUsedAt = p.LastUsedAt?.ToUnixTimeSeconds()
            });
    }

    public void Update(Profile p)
    {
        using var conn = Open();
        conn.Execute(
            """
            UPDATE profiles SET
              name = @Name,
              notes = @Notes,
              group_id = @GroupId,
              tags = @Tags,
              proxy_id = @ProxyId,
              last_used_at = @LastUsedAt
            WHERE id = @Id;
            """, new
            {
                Id = p.Id.ToString(),
                p.Name,
                p.Notes,
                p.GroupId,
                Tags = JsonSerializer.Serialize(p.Tags),
                ProxyId = p.ProxyId?.ToString(),
                LastUsedAt = p.LastUsedAt?.ToUnixTimeSeconds()
            });
    }

    public void Delete(Guid id)
    {
        using var conn = Open();
        conn.Execute("DELETE FROM profiles WHERE id = @id", new { id = id.ToString() });
    }

    private static Profile Map(ProfileRow row) => new()
    {
        Id              = Guid.Parse(row.id),
        Name            = row.name,
        Notes           = row.notes,
        GroupId         = row.group_id,
        Tags            = JsonSerializer.Deserialize<List<string>>(row.tags ?? "[]") ?? new(),
        FingerprintSeed = row.fingerprint_seed,
        ProxyId         = row.proxy_id is null ? null : Guid.Parse(row.proxy_id),
        StoragePath     = row.storage_path,
        CreatedAt       = DateTimeOffset.FromUnixTimeSeconds(row.created_at),
        LastUsedAt      = row.last_used_at is null ? null : DateTimeOffset.FromUnixTimeSeconds(row.last_used_at.Value)
    };

    private sealed record ProfileRow(
        string id, string name, string? notes, string? group_id,
        string? tags, string fingerprint_seed, string? proxy_id,
        string storage_path, long created_at, long? last_used_at);
}
