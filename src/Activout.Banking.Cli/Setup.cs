using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Activout.Banking;
using Activout.Banking.EnableBanking;

namespace Activout.Banking.Cli;

/// <summary>
/// First-time setup: signing key and certificate, guidance for the Enable Banking control panel, and config.json.
/// Safe to run again: existing keys, certificates and settings are kept.
/// </summary>
internal static class Setup
{
    public const string ControlPanelUrl = "https://enablebanking.com/cp/";
    public const string CertificateFileName = "signing-certificate.pem";

    public sealed record Result(string ConfigDirectory, string SigningKey, string Certificate, string ApplicationId,
        string RedirectUrl, IReadOnlyList<string> Created);

    public static async Task<Result> Run(BankPaths paths, string? applicationId, bool openBrowser, TextReader stdin,
        Output output, Func<string, bool> browser, CancellationToken ct)
    {
        var created = new List<string>();
        BankPaths.EnsurePrivateDirectory(paths.ConfigDirectory);
        var configPath = Path.Combine(paths.ConfigDirectory, BankConfig.FileName);
        var config = File.Exists(configPath)
            ? BankConfig.Load(paths.ConfigDirectory)
            : new BankConfig { Directory = paths.ConfigDirectory };

        var keyPath = config.ResolvedPrivateKeyPath;
        if (!File.Exists(keyPath))
        {
            output.Progress("Creating your signing key (RSA 4096). It never leaves this computer.");
            using var newKey = RSA.Create(4096);
            File.WriteAllText(keyPath, newKey.ExportPkcs8PrivateKeyPem());
            BankPaths.RestrictFile(keyPath);
            created.Add(keyPath);
        }

        using var key = JwtSigner.LoadPrivateKey(keyPath);
        var certificatePath = Path.Combine(Path.GetDirectoryName(keyPath)!, CertificateFileName);
        if (!File.Exists(certificatePath))
        {
            var request = new CertificateRequest("CN=Activout.Banking", key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var now = DateTimeOffset.UtcNow;
            using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
            File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
            created.Add(certificatePath);
        }

        var id = applicationId ?? config.ApplicationId;
        if (string.IsNullOrWhiteSpace(id))
        {
            Guide(config, certificatePath, output);
            if (openBrowser && browser(ControlPanelUrl))
                output.Progress($"Opened {ControlPanelUrl} in your browser.");
            output.Progress("");
            output.Progress("Paste the application ID shown after you register the application, then press Enter:");
            id = (await stdin.ReadLineAsync(ct))?.Trim();
            if (string.IsNullOrEmpty(id))
                throw new BankingUsageException(
                    "No application ID given. Run 'bank setup' again, or 'bank setup --application-id <id>'.");
        }

        if (!Guid.TryParse(id, out var parsed))
            throw new BankingUsageException($"'{id}' is not an application ID; it looks like 00000000-0000-0000-0000-000000000000");
        id = parsed.ToString();

        if (id != config.ApplicationId || !File.Exists(configPath))
        {
            config = config with { ApplicationId = id };
            config.Save();
            created.Add(configPath);
        }

        output.Progress("");
        output.Progress("Setup is done. What is left:");
        output.Progress("  1. In the control panel, choose 'Activate by linking accounts' for the new application and");
        output.Progress("     approve the accounts you want with your bank (for example with BankID).");
        output.Progress("  2. Check everything:        bank doctor");
        output.Progress("  3. Connect your bank:       bank connect personal --bank <bank name> --country <country code>");
        output.Progress("                              for example: bank connect personal --bank SEB --country SE");
        output.Progress("  4. Download transactions:   bank sync --all");

        return new Result(paths.ConfigDirectory, keyPath, certificatePath, id, config.RedirectUrl, created);
    }

    private static void Guide(BankConfig config, string certificatePath, Output output)
    {
        output.Progress($"""

            bank reads your accounts through Enable Banking, so you register your own read-only application with
            them once. It takes about five minutes:

              1. Sign in, or create an account, at {ControlPanelUrl}
              2. Add a new application with these values:

                 Environment              Production
                 Name                     Anything, for example "My bank archive". Your bank shows it when you approve.
                 Allowed redirect URLs    {config.RedirectUrl}
                 Description              For example "Personal read-only transaction archive"
                 Email, privacy, terms    Your own email and web pages; production applications require them
                 Key                      Choose to upload a certificate (do not let the browser generate a key)
                                          and upload this file:
                                          {certificatePath}

            """);
        if (OperatingSystem.IsMacOS())
            output.Progress("     Tip: in the file dialog, press Cmd+Shift+G and paste the path above.");
    }
}
