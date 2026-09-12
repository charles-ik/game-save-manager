using Microsoft.Data.Sqlite;

namespace GameSaveManager.Core;

public sealed class StateStore
{
    private readonly string connectionString;
    public string DirectoryPath { get; }
    public StateStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "state.db"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS activity (id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, message TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public T Get<T>(string key, Func<T> fallback)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT value FROM state WHERE key=$key"; q.Parameters.AddWithValue("$key", key);
        return q.ExecuteScalar() is string value && value != "null" ? Json.Read<T>(value) : fallback();
    }
    public void Set<T>(string key, T value)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        q.Parameters.AddWithValue("$key", key); q.Parameters.AddWithValue("$value", Json.Write(value)); q.ExecuteNonQuery();
    }
    public void Log(string message)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO activity(timestamp,message) VALUES($time,$message); DELETE FROM activity WHERE id NOT IN (SELECT id FROM activity ORDER BY id DESC LIMIT 1000)";
        q.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O")); q.Parameters.AddWithValue("$message", message); q.ExecuteNonQuery();
    }
    public List<Activity> Activities()
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT timestamp,message FROM activity ORDER BY id DESC LIMIT 100";
        using var reader = q.ExecuteReader(); var list = new List<Activity>();
        while (reader.Read()) list.Add(new(DateTimeOffset.Parse(reader.GetString(0)), reader.GetString(1)));
        return list;
    }
}
