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
              (id, name, notes, group_id, tags, fingerprint_seed, pinned_os, proxy_id, storage_path, engine_path, created_at, last_used_at)
            VALUES
              (@Id, @Name, @Notes, @GroupId, @Tags, @FingerprintSeed, @PinnedOs, @ProxyId, @StoragePath, @EnginePath, @CreatedAt, @LastUsedAt);
            """, new
            {
                Id = p.Id.ToString(),
                p.Name,
                p.Notes,
                p.GroupId,
                Tags = JsonSerializer.Serialize(p.Tags),
                p.FingerprintSeed,
                PinnedOs = p.PinnedOs?.ToString(),
                ProxyId = p.ProxyId?.ToString(),
                p.StoragePath,
                p.EnginePath,
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
              fingerprint_seed = @FingerprintSeed,
              pinned_os = @PinnedOs,
              proxy_id = @ProxyId,
              engine_path = @EnginePath,
              last_used_at = @LastUsedAt
            WHERE id = @Id;
            """, new
            {
                Id = p.Id.ToString(),
                p.Name,
                p.Notes,
                p.GroupId,
                Tags = JsonSerializer.Serialize(p.Tags),
                p.FingerprintSeed,
                PinnedOs = p.PinnedOs?.ToString(),
                ProxyId = p.ProxyId?.ToString(),
                p.EnginePath,
                LastUsedAt = p.LastUsedAt?.ToUnixTimeSeconds()
            });
    }

    public void Delete(Guid id)
    {
        using var conn = Open();
        // ON DELETE CASCADE on profile_extensions requires foreign-key enforcement,
        // which is off by default in SQLite. Enable it for this connection.
        conn.Execute("PRAGMA foreign_keys = ON;");
        conn.Execute("DELETE FROM profiles WHERE id = @id", new { id = id.ToString() });
    }

    // ---- Extensions ----

    public IReadOnlyList<ProfileExtension> ListExtensions(Guid profileId)
    {
        using var conn = Open();
        var rows = conn.Query<ExtensionRow>(
            "SELECT * FROM profile_extensions WHERE profile_id = @pid ORDER BY sort_order ASC, created_at ASC",
            new { pid = profileId.ToString() }).ToList();
        return rows.Select(MapExt).ToList();
    }

    public void InsertExtension(ProfileExtension e)
    {
        using var conn = Open();
        conn.Execute(
            """
            INSERT INTO profile_extensions
              (id, profile_id, name, path, enabled, sort_order, created_at)
            VALUES
              (@Id, @ProfileId, @Name, @Path, @Enabled, @SortOrder, @CreatedAt);
            """, new
            {
                Id = e.Id.ToString(),
                ProfileId = e.ProfileId.ToString(),
                e.Name,
                e.Path,
                Enabled = e.Enabled ? 1 : 0,
                e.SortOrder,
                CreatedAt = e.CreatedAt.ToUnixTimeSeconds()
            });
    }

    public void UpdateExtension(ProfileExtension e)
    {
        using var conn = Open();
        conn.Execute(
            """
            UPDATE profile_extensions SET
              name = @Name,
              path = @Path,
              enabled = @Enabled,
              sort_order = @SortOrder
            WHERE id = @Id;
            """, new
            {
                Id = e.Id.ToString(),
                e.Name,
                e.Path,
                Enabled = e.Enabled ? 1 : 0,
                e.SortOrder
            });
    }

    public void DeleteExtension(Guid extensionId)
    {
        using var conn = Open();
        conn.Execute("DELETE FROM profile_extensions WHERE id = @id", new { id = extensionId.ToString() });
    }

    private static Profile Map(ProfileRow row) => new()
    {
        Id              = Guid.Parse(row.id),
        Name            = row.name,
        Notes           = row.notes,
        GroupId         = row.group_id,
        Tags            = JsonSerializer.Deserialize<List<string>>(row.tags ?? "[]") ?? new(),
        FingerprintSeed = row.fingerprint_seed,
        PinnedOs        = row.pinned_os is null ? null : Enum.Parse<OperatingSystemKind>(row.pinned_os, ignoreCase: true),
        ProxyId         = row.proxy_id is null ? null : Guid.Parse(row.proxy_id),
        StoragePath     = row.storage_path,
        EnginePath      = row.engine_path,
        CreatedAt       = DateTimeOffset.FromUnixTimeSeconds(row.created_at),
        LastUsedAt      = row.last_used_at is null ? null : DateTimeOffset.FromUnixTimeSeconds(row.last_used_at.Value)
    };

    private static ProfileExtension MapExt(ExtensionRow row) => new()
    {
        Id        = Guid.Parse(row.id),
        ProfileId = Guid.Parse(row.profile_id),
        Name      = row.name,
        Path      = row.path,
        Enabled   = row.enabled != 0,
        SortOrder = (int)row.sort_order,
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(row.created_at)
    };

    private sealed record ProfileRow(
        string id, string name, string? notes, string? group_id,
        string? tags, string fingerprint_seed, string? pinned_os, string? proxy_id,
        string storage_path, string? engine_path, long created_at, long? last_used_at);

    private sealed record ExtensionRow(
        string id, string profile_id, string name, string path,
        long enabled, long sort_order, long created_at);
}
