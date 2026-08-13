using Microsoft.Data.Sqlite;

namespace Lacertae.Infrastructure.Storage;

public sealed class SqliteConnectionFactory(string databasePath)
{
    private readonly string databasePath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(databasePath)
            ? throw new ArgumentException("Database path cannot be blank.", nameof(databasePath))
            : databasePath);

    public string DatabasePath => databasePath;

    public SqliteConnection Create()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        return connection;
    }
}
