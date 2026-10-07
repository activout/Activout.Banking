using System.Text.Json;
using Activout.Banking.Archive;
using Activout.Banking.EnableBanking;

namespace Activout.Banking;

public sealed record AccountSyncResult(
    string Account,
    bool Success,
    int Added,
    int Updated,
    DateOnly? CoveredFrom,
    DateOnly? CoveredTo,
    string? Error,
    bool ReauthorisationRequired);

public sealed record SyncResult(string Connection, string Outcome, IReadOnlyList<AccountSyncResult> Accounts)
{
    public const string Success = "success";
    public const string Partial = "partial";
    public const string Failed = "failed";

    public int Added => Accounts.Sum(a => a.Added);
    public int Updated => Accounts.Sum(a => a.Updated);
}

/// <summary>
/// Synchronises archived transactions. Each account's whole window is fetched before its rows and watermark are
/// committed in one SQLite transaction, so a failed page never advances coverage. Failures in one account leave
/// other accounts committed and mark the run partial.
/// </summary>
public sealed class Synchroniser(
    BankingClient client,
    BankArchive archive,
    BankConfig config,
    string lockPath,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>How long to wait for another local sync to finish before failing.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<SyncResult>> Sync(IReadOnlyList<string> connectionNames, CancellationToken ct,
        Action<string>? progress = null)
    {
        using var syncLock = await SyncLock.Acquire(lockPath, LockTimeout, ct);
        var results = new List<SyncResult>();
        foreach (var name in connectionNames)
        {
            results.Add(await SyncConnection(name, ct, progress));
        }

        return results;
    }

    private async Task<SyncResult> SyncConnection(string name, CancellationToken ct, Action<string>? progress)
    {
        var connection = await archive.GetConnection(name);
        var now = _time.GetUtcNow();
        if (connection.SessionId == null)
            throw new AuthorisationException($"Connection '{name}' has no bank session; run 'bank connect {name}'");

        var runId = await archive.Dao.InsertSyncRun(connection.Id, BankArchive.Timestamp(now));
        var results = new List<AccountSyncResult>();
        try
        {
            if (Connection.ParseTimestamp(connection.ValidUntil) is { } validUntil && validUntil <= now)
            {
                results.Add(new AccountSyncResult("*", false, 0, 0, null, null,
                    $"Consent expired at {connection.ValidUntil}; run 'bank connect {name}'", true));
            }
            else
            {
                var accounts = (await archive.Dao.ListAccounts(connection.Id)).Where(a => a.ProviderUid != null).ToList();
                if (accounts.Count == 0)
                {
                    results.Add(new AccountSyncResult("*", false, 0, 0, null, null,
                        "The current session has no accounts", false));
                }

                foreach (var account in accounts)
                {
                    var label = Account.From(account, name).Selector;
                    progress?.Invoke($"Synchronising {label}");
                    results.Add(await SyncAccount(account, label, ct));
                }
            }
        }
        catch (OperationCanceledException)
        {
            await Finish(runId, SyncResult.Failed, results, "cancelled");
            throw;
        }

        var outcome = results.All(r => r.Success) ? SyncResult.Success
            : results.Any(r => r.Success) ? SyncResult.Partial
            : SyncResult.Failed;
        await Finish(runId, outcome, results, null);
        return new SyncResult(name, outcome, results);
    }

    private async Task<AccountSyncResult> SyncAccount(AccountRow account, string label, CancellationToken ct)
    {
        var state = await archive.Dao.GetSyncState(account.Id);
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var initial = state is not { InitialComplete: true } || BankArchive.ParseDate(state.CoveredTo) == null;

        TransactionQuery query;
        if (initial)
        {
            query = new TransactionQuery(config.InitialHistoryFrom, null, Longest: true);
        }
        else
        {
            var from = BankArchive.ParseDate(state!.CoveredTo)!.Value.AddDays(-Math.Max(0, config.OverlapDays));
            query = new TransactionQuery(from > today ? today : from, today);
        }

        var now = _time.GetUtcNow();
        IReadOnlyList<TransactionRow> rows;
        try
        {
            var received = await client.GetTransactions(account.ProviderUid!, query, ct);
            rows = TransactionMapper.MapWindow(account.Id, received, now);
        }
        catch (JsonException e)
        {
            return new AccountSyncResult(label, false, 0, 0, null, null, "Unexpected transaction data: " + e.Message, false);
        }
        catch (BankingApiException e)
        {
            return new AccountSyncResult(label, false, 0, 0, null, null, e.Message, e.RequiresReauthorisation);
        }
        catch (HttpRequestException e)
        {
            return new AccountSyncResult(label, false, 0, 0, null, null, e.Message, false);
        }

        var timestamp = BankArchive.Timestamp(now);

        // Coverage is what was requested or observed, never an assumed history length.
        var observedFrom = rows.Select(r => BankArchive.ParseDate(r.BookingDate ?? r.ValueDate ?? r.TransactionDate))
            .Where(d => d != null).Min();
        DateOnly? coveredFrom = initial
            ? query.From ?? observedFrom
            : Earliest(BankArchive.ParseDate(state!.CoveredFrom), query.From);

        int added = 0, updated = 0;
        await archive.InTransaction(async transaction =>
        {
            foreach (var row in rows)
            {
                var existing = await archive.Dao.FindTransaction(account.Id, row.IdentityKey, transaction);
                if (existing == null)
                {
                    await archive.Dao.InsertTransaction(row, transaction);
                    added++;
                }
                else if (existing.RawJson != row.RawJson)
                {
                    await archive.Dao.UpdateTransaction(new TransactionRow
                    {
                        Id = existing.Id, EntryReference = row.EntryReference, TransactionId = row.TransactionId,
                        Status = row.Status, BookingDate = row.BookingDate, ValueDate = row.ValueDate,
                        TransactionDate = row.TransactionDate, Amount = row.Amount, Currency = row.Currency,
                        Counterparty = row.Counterparty, Description = row.Description, Reference = row.Reference,
                        RawJson = row.RawJson, LastSeenAt = timestamp,
                    }, transaction);
                    updated++;
                }
                else
                {
                    await archive.Dao.TouchTransaction(existing.Id, timestamp, transaction);
                }
            }

            await archive.Dao.SaveSyncState(new SyncStateRow
            {
                AccountId = account.Id,
                CoveredFrom = coveredFrom is { } f ? BankArchive.Date(f) : null,
                CoveredTo = BankArchive.Date(today),
                InitialComplete = true,
                LastSuccessAt = timestamp,
            }, transaction);
        });

        return new AccountSyncResult(label, true, added, updated, coveredFrom, today, null, false);
    }

    private static DateOnly? Earliest(DateOnly? a, DateOnly? b) =>
        a == null ? b : b == null ? a : a < b ? a : b;

    private Task Finish(long runId, string outcome, IReadOnlyList<AccountSyncResult> results, string? note) =>
        archive.Dao.FinishSyncRun(runId, BankArchive.Timestamp(_time.GetUtcNow()), outcome,
            results.Sum(r => r.Added), results.Sum(r => r.Updated),
            JsonSerializer.Serialize(new { note, accounts = results }, Json.Options));
}

/// <summary>Exclusive lock file preventing concurrent local syncs; released when disposed or the process exits.</summary>
internal sealed class SyncLock(FileStream stream) : IDisposable
{
    public static async Task<SyncLock> Acquire(string path, TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new SyncLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
            catch (IOException)
            {
                throw new SyncLockedException(
                    $"Another bank sync holds {path}; waited {timeout.TotalSeconds:0} seconds");
            }
        }
    }

    public void Dispose() => stream.Dispose();
}
