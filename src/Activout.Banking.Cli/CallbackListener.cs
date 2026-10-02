using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Activout.Banking;

namespace Activout.Banking.Cli;

/// <summary>
/// Short-lived loopback listener that waits for one browser redirect on a fixed path. Serves TLS when given a
/// certificate (https://localhost redirects), otherwise plain HTTP for a hosted forwarder page.
/// </summary>
internal sealed class CallbackListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2? _certificate;

    private CallbackListener(TcpListener listener, X509Certificate2? certificate)
    {
        _listener = listener;
        _certificate = certificate;
    }

    public static CallbackListener Start(int port, X509Certificate2? certificate)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            throw new BankingConfigurationException(
                $"Cannot listen on 127.0.0.1:{port} ({e.SocketErrorCode}); free the port or use --manual");
        }

        return new CallbackListener(listener, certificate);
    }

    /// <summary>
    /// Waits for a GET on <paramref name="callback"/>'s path and passes the full URL to <paramref name="handle"/>.
    /// The browser gets a success page, or the error message if handle throws.
    /// </summary>
    public async Task<T> Receive<T>(Uri callback, Func<Uri, T> handle, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = await Open(client, ct);
            if (stream == null) continue;

            string? target;
            using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    target = await ReadRequestTarget(stream, requestTimeout.Token);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    continue;
                }
            }

            if (target == null || !target.StartsWith('/') ||
                new Uri(callback, target).AbsolutePath != callback.AbsolutePath)
            {
                await Respond(stream, 404, "Not found", "This listener only accepts the bank callback.", ct);
                continue;
            }

            try
            {
                var result = handle(new Uri(callback, target));
                await Respond(stream, 200, "Authorisation received",
                    "You can close this window and return to the terminal.", ct);
                return result;
            }
            catch (AuthorisationException e)
            {
                await Respond(stream, 400, "Authorisation failed", e.Message, ct);
                throw;
            }
        }
    }

    private async Task<Stream?> Open(TcpClient client, CancellationToken ct)
    {
        var network = client.GetStream();
        if (_certificate == null) return network;

        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, ct);
            return ssl;
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            // Typically the browser rejecting the untrusted certificate before the owner accepts it.
            await ssl.DisposeAsync();
            return null;
        }
    }

    /// <summary>Returns the request target of a GET, after consuming the headers.</summary>
    private static async Task<string?> ReadRequestTarget(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(ct);
        for (var i = 0; i < 100 && !string.IsNullOrEmpty(await reader.ReadLineAsync(ct)); i++)
        {
            // Skip headers.
        }

        var parts = requestLine?.Split(' ');
        return parts is ["GET", var target, _] ? target : null;
    }

    private static async Task Respond(Stream stream, int status, string title, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=utf-8><title>{WebUtility.HtmlEncode(title)}</title>" +
            $"<h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p>");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {title}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }
        catch (IOException)
        {
            // The browser went away; the callback has still been handled.
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Dispose();
    }
}

/// <summary>Self-signed certificate for https://localhost callbacks, separate from the API signing key.</summary>
internal static class LoopbackCertificate
{
    public static string CertificatePath(string directory) => Path.Combine(directory, "localhost.crt");
    public static string KeyPath(string directory) => Path.Combine(directory, "localhost.key");

    /// <summary>Loads the certificate, creating it on first use. Returns whether it was created.</summary>
    public static (X509Certificate2 Certificate, bool Created) LoadOrCreate(string directory)
    {
        var certPath = CertificatePath(directory);
        var keyPath = KeyPath(directory);
        var created = false;
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            BankPaths.EnsurePrivateDirectory(directory);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], false));
            var now = DateTimeOffset.UtcNow;
            // Browsers reject TLS server certificates valid for more than 398 days.
            using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(397));
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            BankPaths.RestrictFile(keyPath);
            File.WriteAllText(certPath, certificate.ExportCertificatePem());
            created = true;
        }

        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        // Re-import so SslStream can use the private key on every platform (ephemeral PEM keys fail on Windows).
        return (X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null), created);
    }
}
