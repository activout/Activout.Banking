using System.Globalization;
using Activout.DatabaseClient.Dapper;
using Activout.DatabaseClient.Implementation;
using Microsoft.Data.Sqlite;

namespace Activout.Banking.Archive;

/// <summary>The local SQLite archive: one connection, migrations and transaction scoping.</summary>
public sealed partial class BankArchive : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DapperGateway _gateway;

    public IArchiveDao Dao { get; }

    static BankArchive()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private BankArchive(SqliteConnection connection)
    {
        _connection = connection;
        _gateway = new DapperGateway(connection);
        Dao = new DatabaseClientBuilder()
            .With(new TaskConverter3Factory())
            .With(_gateway)
            .Build<IArchiveDao>();
    }

    /// <summary>Opens (creating if needed) and migrates the archive.</summary>
    public static async Task<BankArchive> Open(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        BankPaths.EnsurePrivateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30,
        }.ConnectionString);
        connection.Open();
        BankPaths.RestrictFile(path);

        var archive = new BankArchive(connection);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode = WAL;";
                await command.ExecuteNonQueryAsync();
            }

            await archive.Migrate();
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private async Task Migrate()
    {
        var version = await Dao.GetSchemaVersion();
        if (version > Schema.Version)
        {
            throw new BankingConfigurationException(
                $"Archive schema version {version} is newer than this bank version supports ({Schema.Version})");
        }

        if (version < 1)
        {
            await InTransaction(() => Dao.ApplySchemaV1());
        }
    }

    public Task<long> GetSchemaVersion() => Dao.GetSchemaVersion();

    /// <summary>Runs DAO calls in one SQLite transaction. Never perform network I/O inside.</summary>
    public async Task InTransaction(Func<Task> action)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync();
        _gateway.Transaction = transaction;
        try
        {
            await action();
            await transaction.CommitAsync();
        }
        finally
        {
            _gateway.Transaction = null;
        }
    }

    public void Dispose() => _connection.Dispose();

    public static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
