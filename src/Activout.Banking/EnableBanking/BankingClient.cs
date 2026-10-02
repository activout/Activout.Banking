using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Activout.Banking.EnableBanking;

public sealed record EnableBankingOptions
{
    public const string DefaultBaseUrl = "https://api.enablebanking.com";

    public required string ApplicationId { get; init; }
    public required string PrivateKeyPath { get; init; }
    public string BaseUrl { get; init; } = DefaultBaseUrl;
}

public sealed record TransactionQuery(DateOnly? From, DateOnly? To, bool Longest = false);

/// <summary>Thin typed client for the Enable Banking account information API.</summary>
public sealed class BankingClient(HttpClient http, string applicationId, RSA signingKey, TimeProvider? timeProvider = null)
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    internal TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public static BankingClient Create(EnableBankingOptions options, HttpMessageHandler? handler = null)
    {
        var key = JwtSigner.LoadPrivateKey(options.PrivateKeyPath);
        var http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(100);
        return new BankingClient(http, options.ApplicationId, key);
    }

    public Task<ApplicationInfo> GetApplication(CancellationToken ct) => Get<ApplicationInfo>("application", ct);

    /// <summary>ASPSPs offering account information for the PSU type in a country.</summary>
    public async Task<IReadOnlyList<Aspsp>> GetAspsps(string country, string psuType, CancellationToken ct)
    {
        var list = await Get<AspspList>(
            $"aspsps?country={Uri.EscapeDataString(country)}&psu_type={Uri.EscapeDataString(psuType)}&service=AIS", ct);
        return list.Aspsps;
    }

    public Task<StartAuthorisationResponse> StartAuthorisation(StartAuthorisationRequest request, CancellationToken ct) =>
        Post<StartAuthorisationResponse>("auth", request, ct);

    /// <summary>Exchanges an authorisation code. Never retried: codes are single-use.</summary>
    public Task<CreateSessionResponse> CreateSession(string code, CancellationToken ct) =>
        Post<CreateSessionResponse>("sessions", new CreateSessionRequest(code), ct);

    public Task<SessionInfo> GetSession(string sessionId, CancellationToken ct) =>
        Get<SessionInfo>($"sessions/{Uri.EscapeDataString(sessionId)}", ct);

    public async Task<IReadOnlyList<BalanceResource>> GetBalances(string accountUid, CancellationToken ct) =>
        (await Get<BalanceList>($"accounts/{Uri.EscapeDataString(accountUid)}/balances", ct)).Balances;

    /// <summary>
    /// Fetches every page of transactions for the query. Paging continues until continuation_key is absent,
    /// including across empty pages, and fails if the provider repeats a key.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetTransactions(string accountUid, TransactionQuery query,
        CancellationToken ct)
    {
        var baseQuery = new List<string>();
        if (query.From is { } from) baseQuery.Add("date_from=" + from.ToString("yyyy-MM-dd"));
        if (query.To is { } to && !query.Longest) baseQuery.Add("date_to=" + to.ToString("yyyy-MM-dd"));
        if (query.Longest) baseQuery.Add("strategy=longest");

        var result = new List<JsonElement>();
        var seenKeys = new HashSet<string>();
        string? continuationKey = null;
        do
        {
            var parameters = new List<string>(baseQuery);
            if (continuationKey != null) parameters.Add("continuation_key=" + Uri.EscapeDataString(continuationKey));
            var path = $"accounts/{Uri.EscapeDataString(accountUid)}/transactions";
            if (parameters.Count > 0) path += "?" + string.Join("&", parameters);

            var page = await Get<TransactionPage>(path, ct);
            if (page.Transactions != null) result.AddRange(page.Transactions);

            continuationKey = string.IsNullOrEmpty(page.ContinuationKey) ? null : page.ContinuationKey;
            if (continuationKey != null && !seenKeys.Add(continuationKey))
            {
                throw new BankingApiException(HttpStatusCode.OK, "REPEATED_CONTINUATION_KEY",
                    "The provider repeated a continuation key; paging stopped to avoid an endless loop");
            }
        } while (continuationKey != null);

        return result;
    }

    private async Task<T> Get<T>(string path, CancellationToken ct)
    {
        for (var attempt = 1;; attempt++)
        {
            TimeSpan delay;
            try
            {
                using var request = CreateRequest(HttpMethod.Get, path);
                using var response = await http.SendAsync(request, ct);
                if (!IsTransient(response.StatusCode) || attempt == MaxAttempts)
                {
                    return await Read<T>(response, ct);
                }

                delay = response.Headers.RetryAfter switch
                {
                    { Delta: { } delta } => delta,
                    { Date: { } date } => date - _time.GetUtcNow(),
                    _ => Backoff(attempt),
                };
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                delay = Backoff(attempt);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                // HttpClient timeout.
                delay = Backoff(attempt);
            }

            delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxRetryDelay ? MaxRetryDelay : delay;
            await Task.Delay(delay, _time, ct);
        }
    }

    private async Task<T> Post<T>(string path, object body, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body, body.GetType(), options: Json.Options);
        using var response = await http.SendAsync(request, ct);
        return await Read<T>(response, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            JwtSigner.CreateToken(applicationId, signingKey, _time.GetUtcNow()));
        return request;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(Json.Options, ct) ??
                   throw new BankingApiException(response.StatusCode, null, "Empty response body");
        }

        ErrorResponse? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Json.Options, ct);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            // Not a JSON error body; fall back to the status line without dumping the body.
        }

        throw new BankingApiException(response.StatusCode, error?.Code ?? error?.Error,
            error?.Message ?? response.ReasonPhrase ?? "Request failed");
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private TimeSpan Backoff(int attempt) => RetryBaseDelay * Math.Pow(2, attempt - 1);
}
