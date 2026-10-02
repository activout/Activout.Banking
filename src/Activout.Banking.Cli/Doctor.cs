using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Activout.Banking;
using Activout.Banking.Archive;
using Activout.Banking.EnableBanking;

namespace Activout.Banking.Cli;

/// <summary>Read-only diagnostics. Never starts consent or changes application configuration.</summary>
internal static class Doctor
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Skip = "skip";

    public sealed record Check(string Name, string Status, string Detail,
        [property: System.Text.Json.Serialization.JsonIgnore] int ExitCode = ExitCodes.UsageOrConfiguration);

    public static async Task<IReadOnlyList<Check>> Run(BankPaths paths, bool offline, HttpMessageHandler? handler,
        CancellationToken ct)
    {
        var checks = new List<Check>();
        BankConfig? config = null;
        try
        {
            config = BankConfig.Load(paths.ConfigDirectory);
            checks.Add(string.IsNullOrWhiteSpace(config.ApplicationId)
                ? new Check("config", Fail, "applicationId is missing")
                : new Check("config", Ok, $"{Path.Combine(paths.ConfigDirectory, BankConfig.FileName)}, API {config.ApiBaseUrl}"));
        }
        catch (BankingConfigurationException e)
        {
            checks.Add(new Check("config", Fail, e.Message));
        }

        checks.Add(SigningKey(config));
        checks.AddRange(Callback(config, paths));
        checks.Add(await Database(paths));

        if (offline || config == null || checks.Any(c => c.Name == "signing key" && c.Status == Fail))
        {
            checks.Add(new Check("application", Skip, offline ? "offline" : "configuration incomplete"));
        }
        else
        {
            checks.AddRange(await Online(config, paths, handler, ct));
        }

        return checks;
    }

    private static Check SigningKey(BankConfig? config)
    {
        if (config == null) return new Check("signing key", Skip, "no configuration");
        var path = config.ResolvedPrivateKeyPath;
        if (!File.Exists(path)) return new Check("signing key", Fail, $"{path} does not exist");
        try
        {
            using var key = JwtSigner.LoadPrivateKey(path);
            if (BankPaths.IsReadableByOthers(path))
                return new Check("signing key", Warn, $"RSA {key.KeySize} bits, but {path} is readable by others; run chmod 600");
            return new Check("signing key", Ok, $"RSA {key.KeySize} bits at {path}");
        }
        catch (BankingConfigurationException e)
        {
            return new Check("signing key", Fail, e.Message);
        }
    }

    private static IEnumerable<Check> Callback(BankConfig? config, BankPaths paths)
    {
        if (config == null)
        {
            yield return new Check("callback", Skip, "no configuration");
            yield break;
        }

        if (!Uri.TryCreate(config.RedirectUrl, UriKind.Absolute, out var redirect))
        {
            yield return new Check("callback", Fail, $"redirectUrl '{config.RedirectUrl}' is not an absolute URL");
            yield break;
        }

        yield return redirect.Scheme != Uri.UriSchemeHttps
            ? new Check("callback", Warn, $"{redirect} is not HTTPS; production applications require HTTPS redirect URLs")
            : new Check("callback", Ok, config.RedirectIsLoopback
                ? $"{redirect} served locally over TLS"
                : $"{redirect} forwards to {config.LocalCallbackUri}");

        if (config.RedirectIsLoopback && redirect.Port != config.ResolvedCallbackPort)
        {
            yield return new Check("callback port", Fail,
                $"callbackPort {config.ResolvedCallbackPort} differs from the redirect URL port {redirect.Port}");
        }
        else
        {
            yield return PortAvailable(config.ResolvedCallbackPort)
                ? new Check("callback port", Ok, $"127.0.0.1:{config.ResolvedCallbackPort} is free")
                : new Check("callback port", Warn, $"127.0.0.1:{config.ResolvedCallbackPort} is in use; connect will fail unless --manual");
        }

        if (config.LocalCallbackUri.Scheme == Uri.UriSchemeHttps)
        {
            var certPath = LoopbackCertificate.CertificatePath(paths.TlsDirectory);
            if (!File.Exists(certPath))
            {
                yield return new Check("callback certificate", Warn, "not created yet; 'bank connect' creates it");
            }
            else
            {
                using var certificate = X509Certificate2.CreateFromPemFile(certPath);
                var now = DateTime.Now;
                yield return now < certificate.NotBefore || now > certificate.NotAfter
                    ? new Check("callback certificate", Fail, $"{certPath} expired {certificate.NotAfter:yyyy-MM-dd}; delete it and its key to regenerate")
                    : new Check("callback certificate", Ok,
                        $"valid until {certificate.NotAfter:yyyy-MM-dd}; browser trust is not verified by doctor");
            }
        }
    }

    private static bool PortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<Check> Database(BankPaths paths)
    {
        if (!File.Exists(paths.DatabasePath))
            return new Check("archive", Warn, $"{paths.DatabasePath} does not exist yet; it is created on first use");
        try
        {
            using var archive = await BankArchive.Open(paths.DatabasePath);
            var connections = await archive.ListConnections();
            var detail = $"{paths.DatabasePath}, schema {await archive.GetSchemaVersion()}, {connections.Count} connection(s)";
            return BankPaths.IsReadableByOthers(paths.DatabasePath)
                ? new Check("archive", Warn, detail + "; readable by others, run chmod 600")
                : new Check("archive", Ok, detail);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or BankingConfigurationException)
        {
            return new Check("archive", Fail, e.Message);
        }
    }

    private static async Task<IEnumerable<Check>> Online(BankConfig config, BankPaths paths,
        HttpMessageHandler? handler, CancellationToken ct)
    {
        var checks = new List<Check>();
        var client = BankingClient.Create(config.ToEnableBankingOptions(), handler);
        try
        {
            var app = await client.GetApplication(ct);
            checks.Add(new Check("application", app.Active ? Ok : Warn,
                $"{app.Name}, {app.Environment}, {(app.Active ? "active" : "INACTIVE: link accounts in the control panel")}"));
            checks.Add(app.RedirectUrls?.Contains(config.RedirectUrl) == true
                ? new Check("redirect registered", Ok, config.RedirectUrl)
                : new Check("redirect registered", Fail,
                    $"{config.RedirectUrl} is not among the application's redirect URLs: {string.Join(", ", app.RedirectUrls ?? [])}"));
        }
        catch (Exception e) when (e is BankingApiException or HttpRequestException)
        {
            checks.Add(new Check("application", Fail, e.Message,
                e is BankingApiException { IsApplicationAuthenticationFailure: true }
                    ? ExitCodes.UsageOrConfiguration
                    : ExitCodes.ApiOrNetwork));
            return checks;
        }

        if (!File.Exists(paths.DatabasePath)) return checks;
        using var archive = await BankArchive.Open(paths.DatabasePath);
        foreach (var connection in await archive.ListConnections())
        {
            try
            {
                var aspsps = await client.GetAspsps(connection.Country, connection.PsuType, ct);
                checks.Add(aspsps.Any(a => a.Name.Equals(connection.Bank, StringComparison.OrdinalIgnoreCase))
                    ? new Check($"institution {connection.Name}", Ok, $"{connection.Bank} offers {connection.PsuType} AIS in {connection.Country}")
                    : new Check($"institution {connection.Name}", Fail, $"{connection.Bank} not listed for {connection.PsuType} AIS in {connection.Country}"));
            }
            catch (Exception e) when (e is BankingApiException or HttpRequestException)
            {
                checks.Add(new Check($"institution {connection.Name}", Fail, e.Message, ExitCodes.ApiOrNetwork));
            }
        }

        return checks;
    }
}
