using Microsoft.Data.Sqlite;

namespace ZeroBrowser.Storage.Sqlite;

public static class Schema
{
    public const string CreateTables =
        """
        CREATE TABLE IF NOT EXISTS profiles (
            id                TEXT PRIMARY KEY,
            name              TEXT NOT NULL,
            notes             TEXT,
            group_id          TEXT,
            tags              TEXT NOT NULL DEFAULT '[]',
            fingerprint_seed  TEXT NOT NULL,
            pinned_os         TEXT,
            proxy_id          TEXT,
            storage_path      TEXT NOT NULL,
            engine_path       TEXT,
            created_at        INTEGER NOT NULL,
            last_used_at      INTEGER
        );

        CREATE TABLE IF NOT EXISTS proxies (
            id            TEXT PRIMARY KEY,
            type          TEXT NOT NULL,
            host          TEXT NOT NULL,
            port          INTEGER NOT NULL,
            username      TEXT,
            password_enc  BLOB,
            country       TEXT,
            city          TEXT,
            last_check_at INTEGER,
            status        TEXT,
            ip_last_seen  TEXT
        );

        CREATE TABLE IF NOT EXISTS profile_extensions (
            id          TEXT PRIMARY KEY,
            profile_id  TEXT NOT NULL,
            name        TEXT NOT NULL,
            path        TEXT NOT NULL,
            enabled     INTEGER NOT NULL DEFAULT 1,
            sort_order  INTEGER NOT NULL DEFAULT 0,
            created_at  INTEGER NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_profiles_group ON profiles(group_id);
        CREATE INDEX IF NOT EXISTS idx_profiles_proxy ON profiles(proxy_id);
        CREATE INDEX IF NOT EXISTS idx_extensions_profile ON profile_extensions(profile_id);
        """;

    public static void Apply(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = CreateTables;
        cmd.ExecuteNonQuery();

        // Best-effort migrations for older DBs. SQLite throws if the column
        // already exists; we swallow that and continue.
        TryAddColumn(conn, "profiles", "pinned_os",   "TEXT");
        TryAddColumn(conn, "profiles", "engine_path", "TEXT");
    }

    private static void TryAddColumn(SqliteConnection conn, string table, string column, string type)
    {
        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Column already exists — fine.
        }
    }
}
