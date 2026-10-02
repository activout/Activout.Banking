using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Activout.Banking.EnableBanking;

/// <summary>RS256 application JWTs as documented by Enable Banking.</summary>
public static class JwtSigner
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static string CreateToken(string applicationId, RSA key, DateTimeOffset now)
    {
        var header = new Dictionary<string, object> { ["typ"] = "JWT", ["alg"] = "RS256", ["kid"] = applicationId };
        var iat = now.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = "enablebanking.com",
            ["aud"] = "api.enablebanking.com",
            ["iat"] = iat,
            ["exp"] = iat + (long)Lifetime.TotalSeconds,
        };
        var signingInput = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
                           Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>Loads a PEM RSA private key (PKCS#1 "RSA PRIVATE KEY" or PKCS#8 "PRIVATE KEY").</summary>
    public static RSA LoadPrivateKey(string path)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(File.ReadAllText(path));
            // Fails here rather than on the first request when the PEM holds only a public key.
            rsa.ExportParameters(includePrivateParameters: true);
            return rsa;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            throw new BankingConfigurationException($"Signing key {path} is not a PEM RSA private key: {e.Message}");
        }
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
