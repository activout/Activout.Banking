using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Activout.Banking;

namespace Activout.Banking.Cli;

/// <summary>Documented exit codes; see docs/troubleshooting.md.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Unexpected = 1;
    public const int UsageOrConfiguration = 2;
    public const int AuthorisationRequired = 3;
    public const int ApiOrNetwork = 4;
    public const int PartialSync = 5;
    public const int Busy = 6;

    public static int For(Exception e) => e switch
    {
        BankingUsageException or BankingConfigurationException => UsageOrConfiguration,
        AuthorisationException => AuthorisationRequired,
        BankingApiException { RequiresReauthorisation: true } => AuthorisationRequired,
        BankingApiException or HttpRequestException or SocketException or TimeoutException => ApiOrNetwork,
        SyncLockedException => Busy,
        _ => Unexpected,
    };
}

/// <summary>Writes results: one JSON document on stdout with --json, otherwise tables. Diagnostics go to stderr.</summary>
internal sealed class Output(TextWriter stdout, TextWriter stderr, bool json)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public bool Json => json;
    public TextWriter Out => stdout;

    public void Progress(string message) => stderr.WriteLine(message);

    public void Result(object value, Action human)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        }
        else
        {
            human();
        }
    }

    public void Error(Exception e, int exitCode)
    {
        stderr.WriteLine($"bank: {e.Message}");
        if (exitCode == ExitCodes.Unexpected) stderr.WriteLine(e);
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new { ok = false, exit_code = exitCode, error = e.Message },
                JsonOptions));
        }
    }

    public void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var all = rows.Select(r => r.Select(c => c ?? "").ToList()).ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => r[i].Length)))
            .ToList();
        stdout.WriteLine(Line(headers));
        foreach (var row in all) stdout.WriteLine(Line(row));
        return;

        string Line(IReadOnlyList<string> cells) =>
            string.Join("  ", cells.Select((c, i) => i == cells.Count - 1 ? c : c.PadRight(widths[i]))).TrimEnd();
    }
}
