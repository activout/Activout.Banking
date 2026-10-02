using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Activout.Banking.Archive;
using RichardSzalay.MockHttp;

namespace Activout.Banking.Cli.Tests;

/// <summary>Runs the real command line against a temporary data directory and a mocked API. Synthetic data only.</summary>
public class CliTests : IDisposable
{
    private const string Api = "https://api.test/";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bank-cli-test-" + Guid.NewGuid().ToString("N"));
    private readonly MockHttpMessageHandler _http = new();

    public CliTests()
    {
        Directory.CreateDirectory(_dir);
        using var key = RSA.Create(2048);
        File.WriteAllText(Path.Combine(_dir, "signing-key.pem"), key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""
            { "applicationId": "app-id", "apiBaseUrl": "{{Api}}", "redirectUrl": "https://localhost:18765/callback" }
            """);
    }

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> Bank(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await Program.Run([..args, "--data-dir", _dir], stdout, stderr, new StringReader(""), _http);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private async Task Seed()
    {
        _http.When(HttpMethod.Post, Api + "sessions").Respond("application/json", """
            {"session_id":"s1","accounts":[
              {"uid":"uid-1","account_id":{"iban":"SE00SYNTHETIC1"},"identification_hash":"h1","identification_hashes":["h1"],"currency":"SEK"},
              {"uid":"uid-2","account_id":{"iban":"SE00SYNTHETIC2"},"identification_hash":"h2","identification_hashes":["h2"],"currency":"SEK"}],
             "access":{"valid_until":"2099-01-01T00:00:00+00:00"}}
            """);
        var config = BankConfig.Load(_dir);
        using var archive = await BankArchive.Open(Path.Combine(_dir, "bank.db"));
        var authoriser = new Authoriser(EnableBanking.BankingClient.Create(config.ToEnableBankingOptions(), _http), archive, config);
        await authoriser.Complete(new PendingAuthorisation
        {
            Connection = "business", Bank = "SEB", Country = "SE", PsuType = "business", State = "s",
            RedirectUri = config.RedirectUri, LocalCallbackUri = config.LocalCallbackUri, Url = "https://bank.test/",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        }, "code", CancellationToken.None);
        await archive.SetAlias("business:1", "operating", DateTimeOffset.UtcNow);
    }

    private void Transactions(string account, string page) =>
        _http.When(Api + $"accounts/{account}/transactions").Respond("application/json", page);

    private const string SeptemberPage = """
        {"transactions":[
          {"entry_reference":"r1","transaction_amount":{"amount":"1234.50","currency":"SEK"},"credit_debit_indicator":"DBIT",
           "status":"BOOK","booking_date":"2026-09-10","remittance_information":["Invoice 7, \"rush\"","line two"]},
          {"entry_reference":"r2","transaction_amount":{"amount":"99.00","currency":"SEK"},"credit_debit_indicator":"CRDT",
           "status":"BOOK","booking_date":"2026-08-31"},
          {"entry_reference":"p1","transaction_amount":{"amount":"5.00","currency":"SEK"},"credit_debit_indicator":"DBIT",
           "status":"PDNG","booking_date":"2026-09-11"}]}
        """;

    [Fact]
    public async Task SyncThenExportCsvKeepsStdoutClean()
    {
        await Seed();
        Transactions("uid-1", SeptemberPage);
        Transactions("uid-2", """{"transactions":[]}""");

        var sync = await Bank("sync", "business");
        var export = await Bank("export", "business:operating", "--month", "2026-09", "--format", "csv");

        Assert.Equal(0, sync.ExitCode);
        Assert.Contains("Synchronising business:operating", sync.Stderr);
        Assert.Equal(0, export.ExitCode);
        Assert.Equal(
            "account,booking_date,value_date,amount,currency,status,counterparty,description,reference,entry_reference,transaction_id,local_id\r\n" +
            "business:operating,2026-09-10,,-1234.50,SEK,BOOK,,\"Invoice 7, \"\"rush\"\" | line two\",,r1,,1\r\n",
            export.Stdout);
        Assert.Equal("", export.Stderr);
    }

    [Fact]
    public async Task JsonExportAndStatusAreSingleDocuments()
    {
        await Seed();
        Transactions("uid-1", SeptemberPage);
        Transactions("uid-2", """{"transactions":[]}""");

        var sync = await Bank("sync", "--all", "--json");
        var export = await Bank("export", "business:operating", "--from", "2026-08-31", "--to", "2026-09-30", "--format", "json");
        var status = await Bank("status", "--json");

        Assert.True(JsonDocument.Parse(sync.Stdout).RootElement.GetProperty("ok").GetBoolean());
        var transactions = JsonDocument.Parse(export.Stdout).RootElement;
        Assert.Equal(2, transactions.GetArrayLength());
        Assert.Equal("2026-08-31", transactions[0].GetProperty("booking_date").GetString());
        Assert.Equal("-1234.50", transactions[1].GetProperty("amount").GetString());
        Assert.Equal("business", JsonDocument.Parse(status.Stdout).RootElement[0]
            .GetProperty("connection").GetProperty("name").GetString());
    }

    [Fact]
    public async Task PartialSyncExitsFiveWithPerAccountResults()
    {
        await Seed();
        Transactions("uid-1", SeptemberPage);
        _http.When(Api + "accounts/uid-2/transactions").Respond(HttpStatusCode.BadRequest, "application/json",
            """{"code":"ASPSP_ERROR","message":"Bank unavailable"}""");

        var (exitCode, stdout, _) = await Bank("sync", "business", "--json");

        Assert.Equal(ExitCodes.PartialSync, exitCode);
        var accounts = JsonDocument.Parse(stdout).RootElement.GetProperty("connections")[0].GetProperty("accounts");
        Assert.Equal(2, accounts.GetArrayLength());
    }

    [Fact]
    public async Task ExpiredSessionExitsAuthorisationRequired()
    {
        await Seed();
        _http.When(Api + "accounts/*/transactions").Respond(HttpStatusCode.UnprocessableEntity, "application/json",
            """{"code":"EXPIRED_SESSION","message":"Session is expired"}""");

        var (exitCode, _, _) = await Bank("sync", "business");

        Assert.Equal(ExitCodes.AuthorisationRequired, exitCode);
    }

    [Theory]
    [InlineData("export", "business:nope", "--month", "2026-09")]
    [InlineData("export", "business:operating", "--month", "2026-09", "--from", "2026-09-01")]
    [InlineData("transactions", "missing:operating")]
    [InlineData("sync")]
    [InlineData("export", "business:operating", "--format", "xml")]
    public async Task UsageErrorsExitTwoWithJsonErrorAndNothingElseOnStdout(params string[] args)
    {
        await Seed();

        var (exitCode, stdout, stderr) = await Bank([..args, "--json"]);

        Assert.Equal(ExitCodes.UsageOrConfiguration, exitCode);
        Assert.StartsWith("bank: ", stderr);
        if (stdout.Length > 0) Assert.False(JsonDocument.Parse(stdout).RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task DoctorOfflineReportsWithoutNetwork()
    {
        var (exitCode, stdout, _) = await Bank("doctor", "--offline", "--json");

        var checks = JsonDocument.Parse(stdout).RootElement.GetProperty("checks");
        Assert.Contains(checks.EnumerateArray(), c => c.GetProperty("name").GetString() == "signing key" &&
                                                      c.GetProperty("status").GetString() != "fail");
        Assert.Contains(checks.EnumerateArray(), c => c.GetProperty("name").GetString() == "application" &&
                                                      c.GetProperty("status").GetString() == "skip");
        Assert.Equal(0, exitCode);
    }
}
