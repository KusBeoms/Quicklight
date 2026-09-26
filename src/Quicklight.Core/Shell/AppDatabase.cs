using Microsoft.Data.Sqlite;

namespace Quicklight.Core.Shell;

/// <summary>
/// The app list in SQLite (%LOCALAPPDATA%\Quicklight\apps.db), so search has every app from the first keystroke
/// after a start while the rescan runs. Each scan replaces the whole list.
/// </summary>
public static class AppDatabase
{
    const int SchemaVersion = 1;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quicklight", "apps.db");

    static SqliteConnection Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // No pooling: a pooled connection keeps the file open after we are done with it.
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(cmd.ExecuteScalar()) != SchemaVersion)
        {
            cmd.CommandText = $"""
                DROP TABLE IF EXISTS part;
                DROP TABLE IF EXISTS app;
                CREATE TABLE app (id INTEGER PRIMARY KEY, name TEXT NOT NULL, publisher TEXT, version TEXT, location TEXT);
                CREATE TABLE part (app INTEGER NOT NULL REFERENCES app(id), kind TEXT NOT NULL, target TEXT NOT NULL, args TEXT);
                PRAGMA user_version = {SchemaVersion};
                """;
            cmd.ExecuteNonQuery();
        }
        return c;
    }

    public static List<AppEntry> Load(string path)
    {
        if (!File.Exists(path)) return [];
        using var c = Open(path);
        var apps = new Dictionary<long, AppEntry>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, publisher, version, location FROM app ORDER BY id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                apps[r.GetInt64(0)] = new AppEntry(r.GetString(1))
                {
                    Publisher = r.IsDBNull(2) ? null : r.GetString(2),
                    Version = r.IsDBNull(3) ? null : r.GetString(3),
                    Location = r.IsDBNull(4) ? null : r.GetString(4),
                };
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT app, kind, target, args FROM part ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (apps.TryGetValue(r.GetInt64(0), out var app) && Enum.TryParse<AppPartKind>(r.GetString(1), out var kind))
                    app.Add(new AppPart(kind, r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }
        // Something to launch is what makes an entry an app.
        return apps.Values.Where(a => a.Parts.Any(p => p.IsMain)).ToList();
    }

    public static void Save(string path, IReadOnlyList<AppEntry> apps)
    {
        using var c = Open(path);
        using var tx = c.BeginTransaction();
        using (var clear = c.CreateCommand()) { clear.Transaction = tx; clear.CommandText = "DELETE FROM part; DELETE FROM app;"; clear.ExecuteNonQuery(); }

        using var insertApp = c.CreateCommand();
        insertApp.Transaction = tx;
        insertApp.CommandText = "INSERT INTO app (id, name, publisher, version, location) VALUES ($id, $name, $publisher, $version, $location)";
        var (id, name, publisher, version, location) = (insertApp.Parameters.Add("$id", SqliteType.Integer), insertApp.Parameters.Add("$name", SqliteType.Text),
            insertApp.Parameters.Add("$publisher", SqliteType.Text), insertApp.Parameters.Add("$version", SqliteType.Text), insertApp.Parameters.Add("$location", SqliteType.Text));
        using var insertPart = c.CreateCommand();
        insertPart.Transaction = tx;
        insertPart.CommandText = "INSERT INTO part (app, kind, target, args) VALUES ($app, $kind, $target, $args)";
        var (appId, kind, target, args) = (insertPart.Parameters.Add("$app", SqliteType.Integer), insertPart.Parameters.Add("$kind", SqliteType.Text),
            insertPart.Parameters.Add("$target", SqliteType.Text), insertPart.Parameters.Add("$args", SqliteType.Text));

        for (int i = 0; i < apps.Count; i++)
        {
            var a = apps[i];
            id.Value = appId.Value = i + 1;
            name.Value = a.Name;
            publisher.Value = (object?)a.Publisher ?? DBNull.Value;
            version.Value = (object?)a.Version ?? DBNull.Value;
            location.Value = (object?)a.Location ?? DBNull.Value;
            insertApp.ExecuteNonQuery();
            foreach (var p in a.Parts)
            {
                kind.Value = p.Kind.ToString();
                target.Value = p.Target;
                args.Value = (object?)p.Arguments ?? DBNull.Value;
                insertPart.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }
}
