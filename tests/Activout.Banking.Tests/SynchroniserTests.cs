using System.Net;
using RichardSzalay.MockHttp;
using static Activout.Banking.Tests.TestEnvironment;

namespace Activout.Banking.Tests;

public class SynchroniserTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private Task Connect() => _env.Connect("session-1",
        AccountJson("uid-1", "SE00SYNTHETIC1", "hash-1"), AccountJson("uid-2", "SE00SYNTHETIC2", "hash-2"));

    private async Task<SyncResult> Sync() =>
        Assert.Single(await _env.Synchroniser().Sync(["personal"], CancellationToken.None));

    [Fact]
    public async Task RepeatedSyncDoesNotDuplicate()
    {
        await Connect();
        _env.Http.When(Api + "accounts/*/transactions").Respond("application/json", Page(null,
            Transaction(null, "45.00", "DBIT", "2026-09-03", "Coffee"),
            Transaction(null, "45.00", "DBIT", "2026-09-03", "Coffee"),
            Transaction("r1", "1000.00", "CRDT", "2026-09-25", "Salary")));

        var first = await Sync();
        var second = await Sync();

        Assert.Equal(SyncResult.Success, first.Outcome);
        Assert.Equal(6, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(3, (await _env.Archive.QueryTransactions("personal:1", DateRange.Month("2026-09"))).Count);
    }

    [Fact]
    public async Task InitialSyncUsesLongestStrategyThenOverlappingWindows()
    {
        await Connect();
        var queries = new List<string>();
        _env.Http.When(Api + "accounts/uid-1/transactions").With(r => { queries.Add(r.RequestUri!.Query); return true; })
            .Respond("application/json", Page(null, Transaction("a", "1.00", "CRDT", "2026-01-15")));
        _env.Http.When(Api + "accounts/uid-2/transactions").Respond("application/json", Page(null));

        await Sync();
        _env.Time.Now = _env.Time.Now.AddDays(3);
        await Sync();

        Assert.Equal("?strategy=longest", queries[0]);
        Assert.Equal("?date_from=2026-09-25&date_to=2026-10-05", queries[1]);
        var status = (await _env.Archive.GetStatus()).Single().Accounts.Single(a => a.Account.Id == 1);
        Assert.Equal(new DateOnly(2026, 1, 15), status.CoveredFrom);
        Assert.Equal(new DateOnly(2026, 10, 5), status.CoveredTo);
    }

    [Fact]
    public async Task CorrectedTransactionIsUpdatedNotDuplicated()
    {
        await Connect();
        _env.Http.When(Api + "accounts/uid-2/transactions").Respond("application/json", Page(null));
        _env.Http.When(Api + "accounts/uid-1/transactions").Respond("application/json",
            Page(null, Transaction("r1", "10.00", "DBIT", "2026-09-01", "Original")));
        await Sync();

        _env.Http.Clear();
        _env.Http.When(Api + "accounts/uid-2/transactions").Respond("application/json", Page(null));
        _env.Http.When(Api + "accounts/uid-1/transactions").Respond("application/json",
            Page(null, Transaction("r1", "10.00", "DBIT", "2026-09-01", "Corrected")));
        var result = await Sync();

        Assert.Equal(1, result.Updated);
        var transaction = Assert.Single(await _env.Archive.QueryTransactions("personal:1", DateRange.Month("2026-09")));
        Assert.Equal("Corrected", transaction.Description);
    }

    [Fact]
    public async Task MidPaginationFailurePreservesWatermarkAndHistory()
    {
        await Connect();
        _env.Http.When(Api + "accounts/*/transactions").Respond("application/json",
            Page(null, Transaction("r1", "10.00", "CRDT", "2026-09-01")));
        await Sync();
        var before = (await _env.Archive.GetStatus()).Single().Accounts.Single(a => a.Account.Id == 1);

        _env.Http.Clear();
        _env.Time.Now = _env.Time.Now.AddDays(1);
        _env.Http.When(Api + "accounts/uid-1/transactions").WithQueryString("continuation_key", "k1")
            .Respond(HttpStatusCode.InternalServerError, "application/json", """{"code":"ASPSP_ERROR","message":"boom"}""");
        _env.Http.When(Api + "accounts/uid-1/transactions").Respond("application/json",
            Page("k1", Transaction("r2", "20.00", "CRDT", "2026-10-02")));
        _env.Http.When(Api + "accounts/uid-2/transactions").Respond("application/json", Page(null));

        var result = await Sync();

        Assert.Equal(SyncResult.Partial, result.Outcome);
        var failed = result.Accounts.Single(a => !a.Success);
        Assert.Contains("ASPSP_ERROR", failed.Error);
        var after = (await _env.Archive.GetStatus()).Single().Accounts.Single(a => a.Account.Id == 1);
        Assert.Equal(before.CoveredTo, after.CoveredTo);
        Assert.Equal(1, after.TransactionCount);
    }

    [Fact]
    public async Task PartialAccountFailureCommitsOtherAccountsAndRecordsRun()
    {
        await Connect();
        _env.Http.When(Api + "accounts/uid-1/transactions").Respond(HttpStatusCode.UnprocessableEntity,
            "application/json", """{"code":"EXPIRED_SESSION","message":"Session is expired"}""");
        _env.Http.When(Api + "accounts/uid-2/transactions").Respond("application/json",
            Page(null, Transaction("r1", "10.00", "CRDT", "2026-09-01")));

        var result = await Sync();

        Assert.Equal(SyncResult.Partial, result.Outcome);
        Assert.True(result.Accounts.Single(a => !a.Success).ReauthorisationRequired);
        Assert.Equal(1, result.Added);
        var status = (await _env.Archive.GetStatus()).Single();
        Assert.Equal(SyncResult.Partial, status.LatestFailure!.Outcome);
        Assert.Null(status.LastSuccess);
    }

    [Fact]
    public async Task ConcurrentSyncIsRejected()
    {
        await Connect();
        using var held = new FileStream(_env.Paths.SyncLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None);

        await Assert.ThrowsAsync<SyncLockedException>(Sync);
    }

    [Fact]
    public async Task ExpiredConsentFailsWithoutCallingTheApi()
    {
        await Connect();
        _env.Time.Now = new DateTimeOffset(2027, 4, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await Sync();

        Assert.Equal(SyncResult.Failed, result.Outcome);
        Assert.True(Assert.Single(result.Accounts).ReauthorisationRequired);
    }
}
