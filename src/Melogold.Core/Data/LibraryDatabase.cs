using Microsoft.Data.Sqlite;

namespace Melogold.Core.Data;

/// <summary>
/// Файл библиотеки (SQLite, WAL). Схема повторяет сущности DESIGN §3.13.1 для десктопов: треки, лайки, закладки,
/// плейлисты с <c>sort_key</c> и плотной <c>position</c>, прослушивания и счётчики, состояние синхронизации и её снимок
/// (вариант со снимком, REWRITE §4.12a Android). Запись — под одним замком, чтение — параллельно.
/// </summary>
public sealed class LibraryDatabase
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly Lock _writeLock = new();

    public LibraryDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = path == ":memory:" ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
        if (path == ":memory:")
        {
            // База в памяти живёт, пока открыто хотя бы одно соединение
            _keepAlive = Open();
        }
        Migrate();
    }

    private readonly SqliteConnection? _keepAlive;

    public string Path { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Запись одной транзакцией.</summary>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var result = work(connection, transaction);
            transaction.Commit();
            return result;
        }
    }

    public void Write(Action<SqliteConnection, SqliteTransaction> work) => Write<bool>((c, t) =>
    {
        work(c, t);
        return true;
    });

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        using var connection = Open();
        return work(connection);
    }

    private void Migrate()
    {
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = Path == ":memory:" ? "SELECT 1;" : "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            wal.ExecuteNonQuery();
        }
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidOperationException($"Библиотека создана более новой версией Melogold (схема {version})");
        if (version < 1)
        {
            using var transaction = connection.BeginTransaction();
            Exec(connection, transaction, Schema1);
            Exec(connection, transaction, "PRAGMA user_version = 1;");
            transaction.Commit();
        }
    }

    private const string Schema1 = """
        CREATE TABLE tracks (
            video_id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            artists_text TEXT,
            artists_json TEXT,
            album_id TEXT,
            album_title TEXT,
            duration_ms INTEGER,
            duration_text TEXT,
            thumbnail_url TEXT,
            explicit INTEGER NOT NULL DEFAULT 0,
            video_type TEXT,
            metadata_stub INTEGER NOT NULL DEFAULT 0,
            liked_at INTEGER,
            total_play_ms INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL
        );
        CREATE INDEX tracks_liked ON tracks(liked_at) WHERE liked_at IS NOT NULL;

        CREATE TABLE albums (
            browse_id TEXT PRIMARY KEY,
            title TEXT,
            artists_text TEXT,
            year TEXT,
            thumbnail_url TEXT,
            playlist_id TEXT,
            bookmarked_at INTEGER
        );

        CREATE TABLE artists (
            browse_id TEXT PRIMARY KEY,
            name TEXT,
            thumbnail_url TEXT,
            is_channel INTEGER NOT NULL DEFAULT 0,
            bookmarked_at INTEGER
        );

        CREATE TABLE playlists (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            sync_id TEXT UNIQUE,
            name TEXT NOT NULL,
            browse_id TEXT,
            thumbnail_url TEXT,
            created_at INTEGER NOT NULL
        );

        CREATE TABLE playlist_items (
            playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            video_id TEXT NOT NULL REFERENCES tracks(video_id),
            position INTEGER NOT NULL,
            sort_key TEXT,
            added_at INTEGER NOT NULL,
            PRIMARY KEY (playlist_id, video_id)
        );
        CREATE INDEX playlist_items_order ON playlist_items(playlist_id, position);

        CREATE TABLE play_events (
            event_id TEXT PRIMARY KEY,
            video_id TEXT NOT NULL,
            played_at INTEGER NOT NULL,
            play_time_ms INTEGER NOT NULL,
            synced INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX play_events_time ON play_events(played_at);
        CREATE INDEX play_events_video ON play_events(video_id, played_at);

        CREATE TABLE search_history (
            query TEXT PRIMARY KEY,
            searched_at INTEGER NOT NULL
        );

        CREATE TABLE lyrics (
            video_id TEXT PRIMARY KEY,
            synced TEXT,
            plain TEXT,
            source TEXT,
            fetched_at INTEGER NOT NULL
        );

        CREATE TABLE content_blocks (
            type TEXT NOT NULL,
            key TEXT NOT NULL,
            level TEXT NOT NULL,
            title TEXT,
            subtitle TEXT,
            thumbnail_url TEXT,
            blocked_at INTEGER NOT NULL,
            PRIMARY KEY (type, key)
        );

        CREATE TABLE app_state (
            key TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE sync_state (
            key TEXT PRIMARY KEY,
            value TEXT
        );
        CREATE TABLE synced_likes (video_id TEXT PRIMARY KEY);
        CREATE TABLE synced_bookmarks (type TEXT NOT NULL, browse_id TEXT NOT NULL, PRIMARY KEY (type, browse_id));
        CREATE TABLE synced_playlists (
            sync_id TEXT PRIMARY KEY,
            name TEXT,
            thumbnail_url TEXT,
            video_ids TEXT NOT NULL DEFAULT ''
        );
        """;

    internal static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar();
    }

    internal static int Exec(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    /// <summary>Копия файла базы (автокопия перед заменой, резервная копия пользователя).</summary>
    public void BackupTo(string destination)
    {
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }
}
