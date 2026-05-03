using Microsoft.Data.Sqlite;

namespace AntiDetectBrowser.Storage.Sqlite;

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
            proxy_id          TEXT,
            storage_path      TEXT NOT NULL,
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

        CREATE INDEX IF NOT EXISTS idx_profiles_group ON profiles(group_id);
        CREATE INDEX IF NOT EXISTS idx_profiles_proxy ON profiles(proxy_id);
        """;

    public static void Apply(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = CreateTables;
        cmd.ExecuteNonQuery();
    }
}
