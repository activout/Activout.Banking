using System.Security.Cryptography.X509Certificates;
using Activout.Banking.EnableBanking;

namespace Activout.Banking.Cli.Tests;

public class SetupTests : IDisposable
{
    private const string Id = "6f1c2a3b-4d5e-4f60-8a7b-9c0d1e2f3a4b";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bank-setup-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> Bank(string stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await Program.Run([..args, "--data-dir", _dir, "--no-browser"], stdout, stderr,
            new StringReader(stdin));
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task CreatesKeyCertificateAndConfigFromThePastedId()
    {
        var (exitCode, _, stderr) = await Bank(Id + "\n", "setup");

        Assert.Equal(0, exitCode);
        Assert.Contains("https://localhost:8765/callback", stderr);
        Assert.Contains("signing-certificate.pem", stderr);
        var config = BankConfig.Load(_dir);
        Assert.Equal(Id, config.ApplicationId);
        using var key = JwtSigner.LoadPrivateKey(config.ResolvedPrivateKeyPath);
        using var certificate = X509Certificate2.CreateFromPem(
            File.ReadAllText(Path.Combine(_dir, "signing-certificate.pem")));
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), certificate.GetRSAPublicKey()!.ExportSubjectPublicKeyInfo());
        Assert.DoesNotContain("redirectUri", File.ReadAllText(Path.Combine(_dir, "config.json")));
    }

    [Fact]
    public async Task RunningAgainKeepsTheKeyAndAsksNothing()
    {
        await Bank("", "setup", "--application-id", Id);
        var keyBefore = await File.ReadAllTextAsync(Path.Combine(_dir, "signing-key.pem"));

        var (exitCode, stdout, stderr) = await Bank("", "setup", "--json");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Paste the application ID", stderr);
        Assert.Equal(keyBefore, await File.ReadAllTextAsync(Path.Combine(_dir, "signing-key.pem")));
        Assert.Contains("\"created\": []", stdout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-id\n")]
    public async Task RejectsAMissingOrInvalidId(string stdin)
    {
        var (exitCode, _, _) = await Bank(stdin, "setup");

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(Path.Combine(_dir, "config.json")));
    }
}
