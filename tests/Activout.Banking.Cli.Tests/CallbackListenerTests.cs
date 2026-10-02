using System.Net;
using System.Net.Sockets;

namespace Activout.Banking.Cli.Tests;

public class CallbackListenerTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task PlainHttpIgnoresOtherPathsAndReturnsCallbackUrl()
    {
        var port = FreePort();
        var callback = new Uri($"http://127.0.0.1:{port}/callback");
        using var listener = CallbackListener.Start(port, null);
        var received = listener.Receive(callback, uri => uri, CancellationToken.None);

        using var http = new HttpClient();
        var favicon = await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
        var response = await http.GetAsync($"http://127.0.0.1:{port}/callback?state=s&code=c");

        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("?state=s&code=c", (await received).Query);
    }

    [Fact]
    public async Task TlsListenerServesGeneratedCertificateAndReportsFailures()
    {
        var port = FreePort();
        var directory = Path.Combine(Path.GetTempPath(), "bank-tls-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (certificate, created) = LoopbackCertificate.LoadOrCreate(directory);
            Assert.True(created);
            Assert.False(LoopbackCertificate.LoadOrCreate(directory).Created);

            var callback = new Uri($"https://localhost:{port}/callback");
            using var listener = CallbackListener.Start(port, certificate);
            var received = listener.Receive<string>(callback,
                _ => throw new AuthorisationException("state mismatch"), CancellationToken.None);

            using var http = new HttpClient(new HttpClientHandler
            {
                // Only this test trusts the self-signed certificate, by exact thumbprint.
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint,
            });
            var response = await http.GetAsync($"https://localhost:{port}/callback?state=x");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("state mismatch", await response.Content.ReadAsStringAsync());
            await Assert.ThrowsAsync<AuthorisationException>(() => received);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CancellationStopsWaitingAndReleasesPort()
    {
        var port = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using (var listener = CallbackListener.Start(port, null))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                listener.Receive(new Uri($"http://127.0.0.1:{port}/callback"), uri => uri, cts.Token));
        }

        using var again = CallbackListener.Start(port, null);
    }

    [Fact]
    public void PortInUseIsAConfigurationError()
    {
        var port = FreePort();
        using var first = CallbackListener.Start(port, null);

        Assert.Throws<BankingConfigurationException>(() => CallbackListener.Start(port, null));
    }
}
