using System.Text.Json;
using RichardSzalay.MockHttp;
using static Activout.Banking.Tests.TestEnvironment;

namespace Activout.Banking.Tests;

public class AuthoriserTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private async Task<PendingAuthorisation> Begin(string psuType = "personal")
    {
        _env.Http.When(Api + "aspsps").Respond("application/json", """
            {"aspsps":[{"name":"SEB","country":"SE","psu_types":["personal"],"maximum_consent_validity":7776000,"beta":false}]}
            """);
        _env.Http.When(HttpMethod.Post, Api + "auth").Respond("application/json",
            """{"url":"https://bank.test/authorise","authorization_id":"auth-1"}""");
        return await _env.Authoriser().Begin("personal", "seb", "se", psuType, CancellationToken.None);
    }

    private static Uri Callback(string query) => new("https://localhost:8765/callback?" + query);

    [Fact]
    public async Task BeginSendsDocumentedRequestWithRandomStateAndBankConsentLimit()
    {
        string? body = null;
        _env.Http.When(Api + "aspsps").Respond("application/json", """
            {"aspsps":[{"name":"SEB","country":"SE","psu_types":["personal"],"maximum_consent_validity":7776000,"beta":false}]}
            """);
        _env.Http.When(HttpMethod.Post, Api + "auth")
            .With(r => (body = r.Content!.ReadAsStringAsync().Result) != null)
            .Respond("application/json", """{"url":"https://bank.test/authorise"}""");

        var a = await _env.Authoriser().Begin("personal", "seb", "se", "personal", CancellationToken.None);
        var b = await _env.Authoriser().Begin("personal", "seb", "se", "personal", CancellationToken.None);

        Assert.NotEqual(a.State, b.State);
        Assert.True(a.State.Length >= 40);
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("SEB", json.GetProperty("aspsp").GetProperty("name").GetString());
        Assert.Equal("personal", json.GetProperty("psu_type").GetString());
        Assert.Equal("https://localhost:8765/callback", json.GetProperty("redirect_url").GetString());
        Assert.Equal(_env.Time.Now.AddSeconds(7776000),
            json.GetProperty("access").GetProperty("valid_until").GetDateTimeOffset());
    }

    [Fact]
    public async Task UnavailablePsuTypeFailsWithActionableMessage()
    {
        _env.Http.When(Api + "aspsps").Respond("application/json", """{"aspsps":[]}""");

        var e = await Assert.ThrowsAsync<BankingUsageException>(() =>
            _env.Authoriser().Begin("business", "SEB", "SE", "business", CancellationToken.None));

        Assert.Contains("business", e.Message);
    }

    [Fact]
    public async Task ValidCallbackReturnsCodeOnce()
    {
        var pending = await Begin();

        var code = _env.Authoriser().ValidateCallback(pending, Callback($"state={pending.State}&code=abc"));

        Assert.Equal("abc", code);
        Assert.Throws<AuthorisationException>(() =>
            _env.Authoriser().ValidateCallback(pending, Callback($"state={pending.State}&code=abc")));
    }

    [Fact]
    public async Task StateMismatchIsRejectedWithoutConsumingTheAttempt()
    {
        var pending = await Begin();

        Assert.Throws<AuthorisationException>(() =>
            _env.Authoriser().ValidateCallback(pending, Callback("state=forged&code=abc")));
        Assert.False(pending.Consumed);
    }

    [Fact]
    public async Task ExpiredAttemptIsRejected()
    {
        var pending = await Begin();
        _env.Time.Now += Authoriser.AttemptLifetime + TimeSpan.FromSeconds(1);

        var e = Assert.Throws<AuthorisationException>(() =>
            _env.Authoriser().ValidateCallback(pending, Callback($"state={pending.State}&code=abc")));
        Assert.Contains("expired", e.Message);
    }

    [Fact]
    public async Task ProviderErrorIsReported()
    {
        var pending = await Begin();

        var e = Assert.Throws<AuthorisationException>(() => _env.Authoriser().ValidateCallback(pending,
            Callback($"state={pending.State}&error=access_denied&error_description=Cancelled+by+user")));

        Assert.Contains("access_denied", e.Message);
        Assert.Contains("Cancelled by user", e.Message);
    }

    [Fact]
    public async Task UnexpectedCallbackOriginOrPathIsRejected()
    {
        var pending = await Begin();

        Assert.Throws<AuthorisationException>(() => _env.Authoriser().ValidateCallback(pending,
            new Uri($"https://evil.test/callback?state={pending.State}&code=abc")));
        Assert.Throws<AuthorisationException>(() => _env.Authoriser().ValidateCallback(pending,
            new Uri($"https://localhost:8765/other?state={pending.State}&code=abc")));
    }

    [Fact]
    public async Task ReauthorisationKeepsLocalAccountAliasesAndHistory()
    {
        var first = await _env.Connect("session-1", AccountJson("uid-old", "SE00SYNTHETIC1", "hash-1"));
        var accountId = Assert.Single(first.Accounts).Id;
        await _env.Archive.SetAlias("personal:" + accountId, "salary", _env.Time.Now);
        _env.Http.When(Api + "accounts/uid-old/transactions").Respond("application/json",
            Page(null, Transaction("t1", "10.00", "CRDT", "2026-09-30")));
        await _env.Synchroniser().Sync(["personal"], CancellationToken.None);

        var second = await _env.Connect("session-2", AccountJson("uid-new", "SE00SYNTHETIC1", "hash-1"));

        var account = Assert.Single(second.Accounts);
        Assert.Equal(accountId, account.Id);
        Assert.Equal("salary", account.Alias);
        var row = await _env.Archive.Dao.GetAccount(accountId);
        Assert.Equal("uid-new", row!.ProviderUid);
        Assert.Single(await _env.Archive.QueryTransactions("personal:salary", DateRange.Month("2026-09")));
    }

    [Fact]
    public async Task AccountMissingFromNewSessionIsKeptButMarkedOutOfSession()
    {
        await _env.Connect("session-1", AccountJson("uid-1", "SE00SYNTHETIC1", "hash-1"),
            AccountJson("uid-2", "SE00SYNTHETIC2", "hash-2"));

        var second = await _env.Connect("session-2", AccountJson("uid-3", "SE00SYNTHETIC1", "hash-1"));

        Assert.Single(second.Accounts);
        var all = await _env.Archive.ListAccounts("personal");
        Assert.Equal(2, all.Count);
        Assert.False(all.Single(a => a.Iban == "SE00SYNTHETIC2").InCurrentSession);
    }
}
