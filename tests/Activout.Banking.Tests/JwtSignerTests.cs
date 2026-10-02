using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Activout.Banking.EnableBanking;

namespace Activout.Banking.Tests;

public class JwtSignerTests
{
    private static JsonElement Decode(string part)
    {
        var padded = part.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement;
    }

    [Fact]
    public void TokenHasDocumentedHeaderClaimsAndValidSignature()
    {
        using var key = RSA.Create(2048);
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        var token = JwtSigner.CreateToken("app-id", key, now);

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var header = Decode(parts[0]);
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("app-id", header.GetProperty("kid").GetString());
        var claims = Decode(parts[1]);
        Assert.Equal("enablebanking.com", claims.GetProperty("iss").GetString());
        Assert.Equal("api.enablebanking.com", claims.GetProperty("aud").GetString());
        Assert.Equal(now.ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
        Assert.Equal(now.ToUnixTimeSeconds() + 300, claims.GetProperty("exp").GetInt64());

        var signature = Convert.FromBase64String(
            parts[2].Replace('-', '+').Replace('_', '/') + new string('=', (4 - parts[2].Length % 4) % 4));
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Theory]
    [InlineData("pkcs1")]
    [InlineData("pkcs8")]
    public void LoadsPemPrivateKeys(string format)
    {
        using var key = RSA.Create(2048);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, format == "pkcs1" ? key.ExportRSAPrivateKeyPem() : key.ExportPkcs8PrivateKeyPem());
            using var loaded = JwtSigner.LoadPrivateKey(path);
            Assert.Equal(key.ExportRSAPublicKeyPem(), loaded.ExportRSAPublicKeyPem());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsPublicKeyOnlyPem()
    {
        using var key = RSA.Create(2048);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, key.ExportSubjectPublicKeyInfoPem());
            Assert.Throws<BankingConfigurationException>(() => JwtSigner.LoadPrivateKey(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
