using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Melogold.Core.Data;

/// <summary>
/// Копия базы в файл и проверка файла перед восстановлением (Android «Резервное копирование» и «Восстановить»). В
/// копию не входит кэш — найденные в сети тексты; свои и импортированные тексты остаются.
/// </summary>
public static class LibraryBackup
{
    /// <summary>Снимок базы через SQLite backup — цельный и при идущей записи.</summary>
    public static void Export(LibraryDatabase database, string target)
    {
        var temp = target + ".tmp";
        File.Delete(temp);
        try
        {
            using (var source = new SqliteConnection($"Data Source={database.Path};Mode=ReadOnly;Pooling=False"))
            using (var copy = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                source.Open();
                copy.Open();
                source.BackupDatabase(copy);
                using var command = copy.CreateCommand();
                command.CommandText = """
                    DELETE FROM lyrics WHERE COALESCE(source, '') NOT IN ('file', 'user') AND COALESCE(plain_source, '') NOT IN ('file', 'user');
                    VACUUM;
                    """;
                command.ExecuteNonQuery();
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Файл — база Melogold для Windows не новее этой версии (у базы Android другое устройство).</summary>
    public static bool IsBackup(string path)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version < 1 || version > LibraryDatabase.SchemaVersion) return false;
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('tracks', 'playlists', 'playlist_items', 'play_events', 'lyrics');";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 5;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
