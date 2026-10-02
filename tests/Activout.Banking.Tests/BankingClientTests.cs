using System.Net;
using Activout.Banking.EnableBanking;
using RichardSzalay.MockHttp;
using static Activout.Banking.Tests.TestEnvironment;

namespace Activout.Banking.Tests;

public class BankingClientTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task AspspsQueryFiltersAccountInformationAndPsuType()
    {
        _env.Http.Expect(HttpMethod.Get, Api + "aspsps")
            .WithExactQueryString("country=SE&psu_type=business&service=AIS")
            .With(r => r.Headers.Authorization?.Scheme == "Bearer" && r.Headers.Authorization.Parameter!.Split('.').Length == 3)
            .Respond("application/json", """
                {"aspsps":[{"name":"SEB","country":"SE","psu_types":["personal","business"],
                  "maximum_consent_validity":15552000,"beta":false,"logo":"x","auth_methods":[]}]}
                """);

        var aspsps = await _env.Client.GetAspsps("SE", "business", CancellationToken.None);

        var seb = Assert.Single(aspsps);
        Assert.Equal(15552000, seb.MaximumConsentValidity);
        _env.Http.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task PagingContinuesAcrossEmptyPageAndKeepsOriginalParameters()
    {
        _env.Http.Expect(Api + "accounts/uid-1/transactions")
            .WithExactQueryString("date_from=2026-09-01&date_to=2026-09-30")
            .Respond("application/json", Page("k1", Transaction("a", "1.00", "CRDT", "2026-09-01")));
        _env.Http.Expect(Api + "accounts/uid-1/transactions")
            .WithExactQueryString("date_from=2026-09-01&date_to=2026-09-30&continuation_key=k1")
            .Respond("application/json", Page("k2"));
        _env.Http.Expect(Api + "accounts/uid-1/transactions")
            .WithExactQueryString("date_from=2026-09-01&date_to=2026-09-30&continuation_key=k2")
            .Respond("application/json", Page(null, Transaction("b", "2.00", "DBIT", "2026-09-02")));

        var result = await _env.Client.GetTransactions("uid-1",
            new TransactionQuery(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), CancellationToken.None);

        Assert.Equal(2, result.Count);
        _env.Http.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task RepeatedContinuationKeyStopsPaging()
    {
        _env.Http.When(Api + "accounts/uid-1/transactions").Respond("application/json", Page("same"));

        var e = await Assert.ThrowsAsync<BankingApiException>(() =>
            _env.Client.GetTransactions("uid-1", new TransactionQuery(null, null, Longest: true), CancellationToken.None));

        Assert.Equal("REPEATED_CONTINUATION_KEY", e.Code);
    }

    [Fact]
    public async Task LongestStrategyOmitsDateTo()
    {
        _env.Http.Expect(Api + "accounts/uid-1/transactions")
            .WithExactQueryString("date_from=2020-01-01&strategy=longest")
            .Respond("application/json", Page(null));

        await _env.Client.GetTransactions("uid-1",
            new TransactionQuery(new DateOnly(2020, 1, 1), new DateOnly(2026, 1, 1), Longest: true), CancellationToken.None);

        _env.Http.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task ReadRequestsRetryAfterRateLimit()
    {
        var calls = 0;
        _env.Http.When(Api + "application").Respond(_ =>
        {
            if (++calls == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"name":"Activout.Banking","active":true,"redirect_urls":[]}"""),
            };
        });

        var app = await _env.Client.GetApplication(CancellationToken.None);

        Assert.Equal("Activout.Banking", app.Name);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CodeExchangeIsNeverRetried()
    {
        var calls = 0;
        _env.Http.When(HttpMethod.Post, Api + "sessions").Respond(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        await Assert.ThrowsAsync<BankingApiException>(() => _env.Client.CreateSession("code", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpiredSessionIsDistinguishedFromApplicationAuthentication()
    {
        _env.Http.When(Api + "accounts/uid-1/balances").Respond(HttpStatusCode.UnprocessableEntity, "application/json",
            """{"code":"EXPIRED_SESSION","message":"Session is expired"}""");
        _env.Http.When(Api + "application").Respond(HttpStatusCode.Unauthorized, "application/json",
            """{"code":"UNAUTHORIZED_ACCESS","message":"Unauthorized access"}""");

        var expired = await Assert.ThrowsAsync<BankingApiException>(() =>
            _env.Client.GetBalances("uid-1", CancellationToken.None));
        var unauthorised = await Assert.ThrowsAsync<BankingApiException>(() =>
            _env.Client.GetApplication(CancellationToken.None));

        Assert.True(expired.RequiresReauthorisation);
        Assert.False(expired.IsApplicationAuthenticationFailure);
        Assert.True(unauthorised.IsApplicationAuthenticationFailure);
        Assert.False(unauthorised.RequiresReauthorisation);
    }

    [Fact]
    public async Task OptionalWireFieldsMayBeAbsent()
    {
        _env.Http.When(Api + "accounts/uid-1/balances").Respond("application/json",
            """{"balances":[{"balance_amount":{"amount":"-12.50","currency":"SEK"}}]}""");

        var balance = Assert.Single(await _env.Client.GetBalances("uid-1", CancellationToken.None));

        Assert.Equal("-12.50", balance.BalanceAmount.Amount);
        Assert.Null(balance.BalanceType);
        Assert.Null(balance.ReferenceDate);
    }
}
