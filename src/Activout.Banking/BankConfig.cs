using System.Text.Json;
using System.Text.Json.Serialization;
using Activout.Banking.EnableBanking;

namespace Activout.Banking;

/// <summary>Contents of config.json in the configuration directory.</summary>
public sealed record BankConfig
{
    public const int DefaultCallbackPort = 8765;
    public const string FileName = "config.json";

    /// <summary>Enable Banking application ID (JWT kid).</summary>
    public string? ApplicationId { get; init; }

    /// <summary>PEM RSA private key; relative paths are resolved against the configuration directory.</summary>
    public string PrivateKeyPath { get; init; } = "signing-key.pem";

    public string ApiBaseUrl { get; init; } = EnableBankingOptions.DefaultBaseUrl;

    /// <summary>Redirect URL exactly as registered with the application.</summary>
    public string RedirectUrl { get; init; } = $"https://localhost:{DefaultCallbackPort}/callback";

    /// <summary>Loopback port for the callback listener. Defaults to the redirect URL's port when it is loopback.</summary>
    public int? CallbackPort { get; init; }

    /// <summary>Days re-fetched before the last synchronised day to catch late bookings and corrections.</summary>
    public int OverlapDays { get; init; } = 7;

    /// <summary>Optional earliest date for the initial archive; null lets the provider find the longest history.</summary>
    public DateOnly? InitialHistoryFrom { get; init; }

    [JsonIgnore] public string Directory { get; init; } = "";

    public string ResolvedPrivateKeyPath => Path.GetFullPath(PrivateKeyPath, Directory);

    public Uri RedirectUri => Uri.TryCreate(RedirectUrl, UriKind.Absolute, out var uri)
        ? uri
        : throw new BankingConfigurationException($"redirectUrl '{RedirectUrl}' is not an absolute URL");

    /// <summary>The redirect itself targets this machine, so the listener must speak its scheme.</summary>
    public bool RedirectIsLoopback => RedirectUri.IsLoopback;

    public int ResolvedCallbackPort => CallbackPort ?? (RedirectIsLoopback ? RedirectUri.Port : DefaultCallbackPort);

    /// <summary>
    /// URL the local listener answers on: the redirect itself when it is loopback, otherwise plain HTTP on
    /// 127.0.0.1 with the same path, which a hosted forwarder page targets.
    /// </summary>
    public Uri LocalCallbackUri => RedirectIsLoopback
        ? RedirectUri
        : new UriBuilder("http", "127.0.0.1", ResolvedCallbackPort, RedirectUri.AbsolutePath).Uri;

    public EnableBankingOptions ToEnableBankingOptions() => new()
    {
        ApplicationId = string.IsNullOrWhiteSpace(ApplicationId)
            ? throw new BankingConfigurationException($"applicationId is missing in {Path.Combine(Directory, FileName)}")
            : ApplicationId,
        PrivateKeyPath = ResolvedPrivateKeyPath,
        BaseUrl = ApiBaseUrl,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static BankConfig Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            throw new BankingConfigurationException($"No configuration at {path}. See docs/setup.md.");
        }

        try
        {
            var config = JsonSerializer.Deserialize<BankConfig>(File.ReadAllText(path), JsonOptions) ??
                         throw new BankingConfigurationException($"{path} is empty");
            return config with { Directory = directory };
        }
        catch (JsonException e)
        {
            throw new BankingConfigurationException($"{path} is not valid JSON: {e.Message}");
        }
    }
}
