namespace Activout.Banking.Archive;

public sealed record AccountStatus(Account Account, long TransactionCount, DateOnly? CoveredFrom, DateOnly? CoveredTo,
    DateTimeOffset? LastSuccess);

public sealed record SyncRunSummary(DateTimeOffset StartedAt, string Outcome, long Added, long Updated);

public sealed record ConnectionStatus(
    Connection Connection,
    SyncRunSummary? LastSuccess,
    SyncRunSummary? LatestFailure,
    IReadOnlyList<AccountStatus> Accounts);

public sealed partial class BankArchive
{
    /// <summary>Offline status from the archive alone.</summary>
    public async Task<IReadOnlyList<ConnectionStatus>> GetStatus()
    {
        var result = new List<ConnectionStatus>();
        foreach (var connection in await Dao.ListConnections())
        {
            var accounts = new List<AccountStatus>();
            foreach (var account in await Dao.ListAccounts(connection.Id))
            {
                var state = await Dao.GetSyncState(account.Id);
                accounts.Add(new AccountStatus(Account.From(account, connection.Name),
                    await Dao.CountTransactions(account.Id), ParseDate(state?.CoveredFrom), ParseDate(state?.CoveredTo),
                    Connection.ParseTimestamp(state?.LastSuccessAt)));
            }

            var failures = new[]
                {
                    await Dao.LatestSyncRun(connection.Id, SyncResult.Failed),
                    await Dao.LatestSyncRun(connection.Id, SyncResult.Partial),
                }
                .Where(r => r != null).MaxBy(r => r!.StartedAt);

            result.Add(new ConnectionStatus(Connection.From(connection),
                Summary(await Dao.LatestSyncRun(connection.Id, SyncResult.Success)), Summary(failures), accounts));
        }

        return result;
    }

    private static SyncRunSummary? Summary(SyncRunRow? row) => row == null
        ? null
        : new SyncRunSummary(Connection.ParseTimestamp(row.StartedAt) ?? default, row.Outcome, row.Added, row.Updated);
}
