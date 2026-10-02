using System.Security.Cryptography;
using System.Text.Json;
using Activout.Banking.Archive;
using Activout.Banking.EnableBanking;
using RichardSzalay.MockHttp;

namespace Activout.Banking.Tests;

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Temporary directory, real SQLite archive and a mocked Enable Banking API. Synthetic data only.</summary>
internal sealed class TestEnvironment : IDisposable
{
    public const string Api = "https://api.test/";

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "bank-test-" + Guid.NewGuid().ToString("N"));
    public MockHttpMessageHandler Http { get; } = new();
    public RSA Key { get; } = RSA.Create(2048);
    public FixedTime Time { get; } = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
    public BankConfig Config { get; }
    public BankingClient Client { get; }
    public BankArchive Archive { get; }
    public BankPaths Paths { get; }

    public TestEnvironment()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Paths = new BankPaths(Directory, Directory);
        Config = new BankConfig
        {
            ApplicationId = "app-id",
            ApiBaseUrl = Api,
            RedirectUrl = "https://localhost:8765/callback",
            Directory = Directory,
        };
        var handler = BankingClient.CreateResilienceHandler(Http, TimeSpan.FromMilliseconds(1));
        Client = new BankingClient(new HttpClient(handler) { BaseAddress = new Uri(Api) }, "app-id", Key, Time);
        Archive = BankArchive.Open(Paths.DatabasePath).GetAwaiter().GetResult();
    }

    public Authoriser Authoriser() => new(Client, Archive, Config, Time);

    public Synchroniser Synchroniser() => new(Client, Archive, Config, Paths.SyncLockPath, Time)
    {
        LockTimeout = TimeSpan.FromMilliseconds(300),
    };

    public static string AccountJson(string uid, string iban, string hash, string currency = "SEK") =>
        JsonSerializer.Serialize(new
        {
            uid,
            account_id = new { iban },
            identification_hash = hash,
            identification_hashes = new[] { hash },
            currency,
            name = "Synthetic Holder",
            details = "Synthetic account",
        });

    public static string SessionJson(string sessionId, params string[] accounts) =>
        $$"""
          {"session_id":"{{sessionId}}","accounts":[{{string.Join(",", accounts)}}],
           "aspsp":{"name":"SEB","country":"SE"},"psu_type":"personal",
           "access":{"valid_until":"2027-03-31T00:00:00+00:00"} }
          """;

    public static string Transaction(string? entryReference, string amount, string indicator, string bookingDate,
        string description = "Synthetic payment", string status = "BOOK") =>
        JsonSerializer.Serialize(new
        {
            entry_reference = entryReference,
            transaction_amount = new { amount, currency = "SEK" },
            credit_debit_indicator = indicator,
            status,
            booking_date = bookingDate,
            value_date = bookingDate,
            remittance_information = new[] { description },
        });

    public static string Page(string? continuationKey, params string[] transactions) =>
        "{\"transactions\":[" + string.Join(",", transactions) + "]" +
        (continuationKey == null ? "" : ",\"continuation_key\":\"" + continuationKey + "\"") + "}";

    /// <summary>Creates connection 'personal' with the given accounts through the real completion path.</summary>
    public async Task<AuthorisationResult> Connect(string sessionId, params string[] accounts)
    {
        Http.Clear();
        Http.When(HttpMethod.Post, Api + "sessions").Respond("application/json", SessionJson(sessionId, accounts));
        var pending = new PendingAuthorisation
        {
            Connection = "personal", Bank = "SEB", Country = "SE", PsuType = "personal", State = "s",
            RedirectUri = Config.RedirectUri, LocalCallbackUri = Config.LocalCallbackUri, Url = "https://bank.test/",
            ExpiresAt = Time.Now.AddMinutes(15),
        };
        var result = await Authoriser().Complete(pending, "code", CancellationToken.None);
        Http.Clear();
        return result;
    }

    public void Dispose()
    {
        Archive.Dispose();
        Key.Dispose();
        Http.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
