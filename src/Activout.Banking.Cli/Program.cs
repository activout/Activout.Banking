using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Activout.Banking;
using Activout.Banking.Archive;
using Activout.Banking.EnableBanking;

namespace Activout.Banking.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => Run(args, Console.Out, Console.Error, Console.In);

    /// <summary>Entry point for tests: explicit streams and an optional HTTP handler.</summary>
    public static async Task<int> Run(string[] args, TextWriter stdout, TextWriter stderr, TextReader stdin,
        HttpMessageHandler? httpHandler = null, CancellationToken ct = default)
    {
        var app = new App(stdout, stderr, stdin, httpHandler);
        var parseResult = app.Root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors) stderr.WriteLine($"bank: {error.Message}");
            stderr.WriteLine("Run 'bank --help' for usage.");
            return ExitCodes.UsageOrConfiguration;
        }

        return await parseResult.InvokeAsync(
            new InvocationConfiguration { Output = stdout, Error = stderr, EnableDefaultExceptionHandler = false }, ct);
    }
}

internal sealed class App
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly TextReader _stdin;
    private readonly HttpMessageHandler? _httpHandler;

    private readonly Option<string?> _dataDir = new("--data-dir")
    {
        Description = "Use this directory for configuration, keys and bank.db instead of the platform defaults",
        Recursive = true,
    };

    private readonly Option<bool> _json = new("--json")
    {
        Description = "Write a single JSON document to stdout; progress and diagnostics go to stderr",
        Recursive = true,
    };

    public RootCommand Root { get; }

    public App(TextWriter stdout, TextWriter stderr, TextReader stdin, HttpMessageHandler? httpHandler)
    {
        _stdout = stdout;
        _stderr = stderr;
        _stdin = stdin;
        _httpHandler = httpHandler;

        Root = new RootCommand(
            "Archive and export your own bank transactions through Enable Banking account information (read-only).")
        {
            _dataDir, _json,
            ConnectCommand(), ConnectionsCommand(), AccountsCommand(), AccountCommand(), BalancesCommand(),
            SyncCommand(), TransactionsCommand(), ExportCommand(), StatusCommand(), DoctorCommand(),
        };
    }

    // Commands

    private Command ConnectCommand()
    {
        var name = new Argument<string>("connection") { Description = "Local connection name, e.g. personal or business" };
        var bank = new Option<string?>("--bank") { Description = "Bank (ASPSP) name, e.g. SEB. Defaults to the existing connection's bank" };
        var country = new Option<string?>("--country") { Description = "ISO 3166 country code, e.g. SE. Defaults to the existing connection's country" };
        var psuType = new Option<string?>("--psu-type") { Description = "personal or business. Defaults to the existing connection's type, else personal" };
        psuType.AcceptOnlyFromAmong(PsuTypes.Personal, PsuTypes.Business);
        var manual = new Option<bool>("--manual") { Description = "Do not listen for the callback; paste the redirected URL from the browser (read from stdin)" };
        var noBrowser = new Option<bool>("--no-browser") { Description = "Print the authorisation URL instead of opening a browser" };
        var timeout = new Option<int>("--timeout") { Description = "Minutes to wait for the callback", DefaultValueFactory = _ => 10 };

        var command = new Command("connect", """
            Authorise (or re-authorise) a bank connection with BankID or the bank's own method.
            Examples:
              bank connect personal --bank SEB --country SE --psu-type personal
              bank connect business --bank SEB --country SE --psu-type business
              bank connect personal --manual
            """) { name, bank, country, psuType, manual, noBrowser, timeout };

        command.SetAction((p, ct) => Execute(p, async output =>
        {
            var paths = Paths(p);
            var config = BankConfig.Load(paths.ConfigDirectory);
            using var archive = await BankArchive.Open(paths.DatabasePath);
            var client = Client(config);
            var connectionName = p.GetValue(name)!;
            var existing = await archive.Dao.GetConnection(connectionName);

            var authoriser = new Authoriser(client, archive, config);
            var pending = await authoriser.Begin(connectionName,
                p.GetValue(bank) ?? existing?.AspspName ?? throw new BankingUsageException("--bank is required for a new connection"),
                p.GetValue(country) ?? existing?.AspspCountry ?? throw new BankingUsageException("--country is required for a new connection"),
                p.GetValue(psuType) ?? existing?.PsuType ?? PsuTypes.Personal,
                ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(p.GetValue(timeout)));

            string code;
            if (p.GetValue(manual))
            {
                output.Progress("Open this URL in a browser and approve access with your bank:");
                output.Progress(pending.Url);
                output.Progress("Then paste the full URL your browser was redirected to and press Enter.");
                output.Progress("(It contains a one-time code; it is read from stdin and not stored.)");
                var line = await _stdin.ReadLineAsync(timeoutCts.Token) ??
                           throw new AuthorisationException("No redirect URL was provided on stdin");
                if (!Uri.TryCreate(line.Trim(), UriKind.Absolute, out var pasted))
                    throw new AuthorisationException("That is not a complete URL");
                code = authoriser.ValidateCallback(pending, pasted);
            }
            else
            {
                var local = config.LocalCallbackUri;
                var certificate = local.Scheme == Uri.UriSchemeHttps ? Certificate(paths, output) : null;
                using var listener = CallbackListener.Start(config.ResolvedCallbackPort, certificate);
                output.Progress($"Waiting for the bank callback on {local.GetLeftPart(UriPartial.Path)} " +
                                $"for {p.GetValue(timeout)} minutes. Press Ctrl+C to cancel.");
                if (p.GetValue(noBrowser) || !OpenBrowser(pending.Url))
                {
                    output.Progress("Open this URL in a browser on this machine:");
                }
                else
                {
                    output.Progress("Opened the browser. If nothing happened, open this URL:");
                }

                output.Progress(pending.Url);
                try
                {
                    code = await listener.Receive(local, uri => authoriser.ValidateCallback(pending, uri),
                        timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new AuthorisationException(
                        "Timed out waiting for the bank callback. On a remote or headless machine use --manual.");
                }
            }

            var result = await authoriser.Complete(pending, code, ct);
            output.Result(result, () =>
            {
                output.Out.WriteLine($"Connected '{result.Connection.Name}' ({result.Connection.Bank}, " +
                                     $"{result.Connection.PsuType}); consent valid until {Format(result.Connection.ValidUntil)}.");
                AccountTable(output, result.Accounts);
            });
            return ExitCodes.Success;
        }));
        return command;
    }

    private Command ConnectionsCommand()
    {
        var command = new Command("connections", "List local bank connections (offline).");
        command.SetAction((p, _) => Execute(p, async output =>
        {
            using var archive = await BankArchive.Open(Paths(p).DatabasePath);
            var connections = await archive.ListConnections();
            output.Result(connections, () => output.Table(
                ["NAME", "BANK", "COUNTRY", "PSU TYPE", "SESSION", "CONSENT VALID UNTIL"],
                connections.Select(c => new[]
                    { c.Name, c.Bank, c.Country, c.PsuType, c.HasSession ? "yes" : "no", Format(c.ValidUntil) })));
            return ExitCodes.Success;
        }));
        return command;
    }

    private Command AccountsCommand()
    {
        var connection = new Option<string?>("--connection") { Description = "Only this connection" };
        var command = new Command("accounts", "List archived accounts and their selectors (offline).") { connection };
        command.SetAction((p, _) => Execute(p, async output =>
        {
            using var archive = await BankArchive.Open(Paths(p).DatabasePath);
            var names = p.GetValue(connection) is { } one
                ? [one]
                : (await archive.ListConnections()).Select(c => c.Name).ToList();
            var accounts = new List<Account>();
            foreach (var n in names) accounts.AddRange(await archive.ListAccounts(n));
            output.Result(accounts, () => AccountTable(output, accounts));
            return ExitCodes.Success;
        }));
        return command;
    }

    private Command AccountCommand()
    {
        var account = new Argument<string>("account") { Description = "Account selector: connection:alias, connection:id, connection:IBAN or a local ID" };
        var alias = new Argument<string>("alias") { Description = "New alias, unique within the connection" };
        var aliasCommand = new Command("alias", """
            Name an account so it can be selected as connection:alias.
            Example: bank account alias business:3 operating
            """) { account, alias };
        aliasCommand.SetAction((p, _) => Execute(p, async output =>
        {
            using var archive = await BankArchive.Open(Paths(p).DatabasePath);
            var updated = await archive.SetAlias(p.GetValue(account)!, p.GetValue(alias), DateTimeOffset.UtcNow);
            output.Result(updated, () => output.Out.WriteLine($"Account {updated.Id} is now {updated.Selector}"));
            return ExitCodes.Success;
        }));
        return new Command("account", "Account management.") { aliasCommand };
    }

    private Command BalancesCommand()
    {
        var account = new Argument<string>("account") { Description = "Account selector, e.g. business:operating" };
        var command = new Command("balances", "Fetch current balances from the bank (online). Balances are provider-supplied, never calculated.") { account };
        command.SetAction((p, ct) => Execute(p, async output =>
        {
            var paths = Paths(p);
            var config = BankConfig.Load(paths.ConfigDirectory);
            using var archive = await BankArchive.Open(paths.DatabasePath);
            var (connection, row) = await archive.ResolveAccount(p.GetValue(account)!);
            if (row.ProviderUid == null)
                throw new AuthorisationException(
                    $"Account is not in the current bank session; run 'bank connect {connection.Name}'");
            var balances = await Client(config).GetBalances(row.ProviderUid, ct);
            output.Result(balances.Select(b => new
            {
                name = b.Name, type = b.BalanceType, amount = b.BalanceAmount.Amount,
                currency = b.BalanceAmount.Currency, reference_date = b.ReferenceDate,
                last_change = b.LastChangeDateTime,
            }), () => output.Table(["TYPE", "NAME", "AMOUNT", "CURRENCY", "REFERENCE DATE"],
                balances.Select(b => new[]
                    { b.BalanceType, b.Name, b.BalanceAmount.Amount, b.BalanceAmount.Currency, b.ReferenceDate })));
            return ExitCodes.Success;
        }));
        return command;
    }

    private Command SyncCommand()
    {
        var connection = new Argument<string?>("connection") { Description = "Connection to synchronise", Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all") { Description = "Synchronise every connection" };
        var command = new Command("sync", """
            Fetch new and corrected booked transactions into the archive (online). Never starts bank authorisation.
            Examples:
              bank sync personal
              bank sync --all --json
            """) { connection, all };
        command.SetAction((p, ct) => Execute(p, async output =>
        {
            var paths = Paths(p);
            var config = BankConfig.Load(paths.ConfigDirectory);
            using var archive = await BankArchive.Open(paths.DatabasePath);
            var name = p.GetValue(connection);
            if (p.GetValue(all) == (name != null))
                throw new BankingUsageException("Give exactly one of a connection name or --all");
            var names = name != null ? [name] : (await archive.ListConnections()).Select(c => c.Name).ToList();

            var synchroniser = new Synchroniser(Client(config), archive, config, paths.SyncLockPath);
            var results = await synchroniser.Sync(names, ct, output.Progress);
            var exitCode = SyncExitCode(results);
            output.Result(new { ok = exitCode == 0, exit_code = exitCode, connections = results }, () =>
            {
                output.Table(["ACCOUNT", "RESULT", "ADDED", "UPDATED", "COVERED FROM", "COVERED TO", "ERROR"],
                    results.SelectMany(r => r.Accounts).Select(a => new[]
                    {
                        a.Account, a.Success ? "ok" : "failed", a.Added.ToString(CultureInfo.InvariantCulture),
                        a.Updated.ToString(CultureInfo.InvariantCulture), a.CoveredFrom?.ToString("yyyy-MM-dd"),
                        a.CoveredTo?.ToString("yyyy-MM-dd"), a.Error,
                    }));
                foreach (var r in results.Where(r => r.Outcome != SyncResult.Success))
                    output.Progress($"Connection '{r.Connection}': {r.Outcome}");
            });
            return exitCode;
        }));
        return command;
    }

    private Command TransactionsCommand()
    {
        var (account, month, from, to) = RangeArguments();
        var command = new Command("transactions", """
            Show archived booked transactions (offline). Date bounds are inclusive booking dates.
            Example: bank transactions business:operating --from 2026-09-01 --to 2026-09-30
            """) { account, month, from, to };
        command.SetAction((p, _) => Execute(p, async output =>
        {
            using var archive = await BankArchive.Open(Paths(p).DatabasePath);
            var transactions = await archive.QueryTransactions(p.GetValue(account)!,
                DateRange.Select(p.GetValue(month), p.GetValue(from), p.GetValue(to)));
            output.Result(transactions, () => output.Table(
                ["BOOKED", "AMOUNT", "CURRENCY", "COUNTERPARTY", "DESCRIPTION"],
                transactions.Select(t => new[]
                {
                    t.BookingDate?.ToString("yyyy-MM-dd"), t.Amount.ToString(CultureInfo.InvariantCulture), t.Currency,
                    t.Counterparty, t.Description,
                })));
            return ExitCodes.Success;
        }));
        return command;
    }

    private Command ExportCommand()
    {
        var (account, month, from, to) = RangeArguments();
        var format = new Option<string>("--format") { Description = "csv or json", DefaultValueFactory = _ => "csv" };
        format.AcceptOnlyFromAmong("csv", "json");
        var outputFile = new Option<string?>("--output", "-o") { Description = "Write to this file instead of stdout" };
        var command = new Command("export", """
            Export archived booked transactions (offline) as UTF-8 CSV or JSON. Not an official bank statement.
            CSV columns: account, booking_date, value_date, amount, currency, status, counterparty, description,
            reference, entry_reference, transaction_id, local_id. Amounts are signed invariant decimals.
            Examples:
              bank export business:operating --month 2026-09 --format csv
              bank export business:operating --month 2026-09 --format json -o september.json
            """) { account, month, from, to, format, outputFile };
        command.SetAction((p, _) => Execute(p, async output =>
        {
            using var archive = await BankArchive.Open(Paths(p).DatabasePath);
            var transactions = await archive.QueryTransactions(p.GetValue(account)!,
                DateRange.Select(p.GetValue(month), p.GetValue(from), p.GetValue(to)));

            var path = p.GetValue(outputFile);
            await using var file = path == null ? null : new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
            var writer = file ?? output.Out;
            if (p.GetValue(format) == "json")
                await writer.WriteLineAsync(JsonSerializer.Serialize(transactions, Output.JsonOptions));
            else
                CsvExport.Write(writer, transactions);

            if (path != null) output.Progress($"Wrote {transactions.Count} transactions to {path}");
            return ExitCodes.Success;
        }));
        return command;
    }

    private Command StatusCommand()
    {
        var command = new Command("status", "Consent expiry, sync history and archived coverage per connection (offline).");
        command.SetAction((p, _) => Execute(p, async output =>
        {
            using var archive = await BankArchive.Open(Paths(p).DatabasePath);
            var status = await archive.GetStatus();
            output.Result(status, () =>
            {
                foreach (var s in status)
                {
                    var c = s.Connection;
                    output.Out.WriteLine($"{c.Name}: {c.Bank} ({c.Country}, {c.PsuType})");
                    output.Out.WriteLine($"  consent valid until: {Format(c.ValidUntil)}" +
                                         (c.ValidUntil < DateTimeOffset.UtcNow ? " (EXPIRED)" : ""));
                    output.Out.WriteLine($"  last successful sync: {Run(s.LastSuccess)}");
                    output.Out.WriteLine($"  latest failed sync: {Run(s.LatestFailure)}");
                    foreach (var a in s.Accounts)
                    {
                        output.Out.WriteLine($"  {a.Account.Selector}: {a.Account.Iban ?? a.Account.OtherIdentification} " +
                                             $"{a.Account.Currency}, {a.TransactionCount} transactions, archived " +
                                             $"{a.CoveredFrom?.ToString("yyyy-MM-dd") ?? "?"} to {a.CoveredTo?.ToString("yyyy-MM-dd") ?? "never"}" +
                                             (a.Account.InCurrentSession ? "" : " (not in current session)"));
                    }
                }

                if (status.Count == 0) output.Out.WriteLine("No connections. Run 'bank connect'.");
            });
            return ExitCodes.Success;

            static string Run(SyncRunSummary? r) => r == null
                ? "never"
                : $"{Format(r.StartedAt)} ({r.Outcome}, {r.Added} added, {r.Updated} updated)";
        }));
        return command;
    }

    private Command DoctorCommand()
    {
        var offline = new Option<bool>("--offline") { Description = "Skip checks that contact the API" };
        var command = new Command("doctor", "Check configuration, keys, callback, archive and (unless --offline) the API application. Changes nothing.") { offline };
        command.SetAction((p, ct) => Execute(p, async output =>
        {
            var checks = await Doctor.Run(Paths(p), p.GetValue(offline), _httpHandler, ct);
            var exitCode = checks.FirstOrDefault(c => c.Status == Doctor.Fail)?.ExitCode ?? ExitCodes.Success;
            output.Result(new { ok = exitCode == 0, exit_code = exitCode, checks }, () => output.Table(
                ["CHECK", "STATUS", "DETAIL"], checks.Select(c => new[] { c.Name, c.Status, c.Detail })));
            return exitCode;
        }));
        return command;
    }

    // Helpers

    private static (Argument<string>, Option<string?>, Option<DateOnly?>, Option<DateOnly?>) RangeArguments() =>
    (
        new Argument<string>("account") { Description = "Account selector, e.g. business:operating" },
        new Option<string?>("--month") { Description = "Calendar month YYYY-MM; excludes --from/--to" },
        new Option<DateOnly?>("--from") { Description = "First booking date, inclusive (YYYY-MM-DD)" },
        new Option<DateOnly?>("--to") { Description = "Last booking date, inclusive (YYYY-MM-DD)" }
    );

    private Task<int> Execute(ParseResult p, Func<Output, Task<int>> action) =>
        Execute(new Output(_stdout, _stderr, p.GetValue(_json)), action);

    private static async Task<int> Execute(Output output, Func<Output, Task<int>> action)
    {
        try
        {
            return await action(output);
        }
        catch (OperationCanceledException)
        {
            output.Progress("Cancelled.");
            return 130;
        }
        catch (Exception e)
        {
            var exitCode = ExitCodes.For(e);
            output.Error(e, exitCode);
            return exitCode;
        }
    }

    private BankPaths Paths(ParseResult p) => BankPaths.Resolve(p.GetValue(_dataDir));

    private BankingClient Client(BankConfig config) => BankingClient.Create(config.ToEnableBankingOptions(), _httpHandler);

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate(BankPaths paths, Output output)
    {
        var (certificate, created) = LoopbackCertificate.LoadOrCreate(paths.TlsDirectory);
        if (created)
        {
            output.Progress($"Created a self-signed callback certificate at {LoopbackCertificate.CertificatePath(paths.TlsDirectory)}.");
            output.Progress("Your browser will warn about it unless you trust it; see docs/setup.md.");
        }

        return certificate;
    }

    private static bool OpenBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    internal static int SyncExitCode(IReadOnlyList<SyncResult> results)
    {
        var failed = results.Where(r => r.Outcome != SyncResult.Success).ToList();
        if (failed.Count == 0) return ExitCodes.Success;
        if (failed.All(r => r.Outcome == SyncResult.Failed))
        {
            return failed.SelectMany(r => r.Accounts).Any(a => a.ReauthorisationRequired)
                ? ExitCodes.AuthorisationRequired
                : results.Count == failed.Count ? ExitCodes.ApiOrNetwork : ExitCodes.PartialSync;
        }

        return ExitCodes.PartialSync;
    }

    private static void AccountTable(Output output, IEnumerable<Account> accounts) => output.Table(
        ["SELECTOR", "ID", "ALIAS", "IDENTIFICATION", "CURRENCY", "NAME", "IN SESSION"],
        accounts.Select(a => new[]
        {
            a.Selector, a.Id.ToString(CultureInfo.InvariantCulture), a.Alias, a.Iban ?? a.OtherIdentification,
            a.Currency, a.Details ?? a.Product ?? a.Name, a.InCurrentSession ? "yes" : "no",
        }));

    private static string Format(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "-";
}
