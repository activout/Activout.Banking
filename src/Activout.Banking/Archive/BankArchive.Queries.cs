using System.Globalization;

namespace Activout.Banking.Archive;

public sealed partial class BankArchive
{
    public async Task<IReadOnlyList<Connection>> ListConnections() =>
        (await Dao.ListConnections()).Select(Connection.From).ToList();

    public async Task<ConnectionRow> GetConnection(string name) =>
        await Dao.GetConnection(name) ??
        throw new BankingUsageException($"Unknown connection '{name}'. Run 'bank connections' to list them.");

    public async Task<IReadOnlyList<Account>> ListAccounts(string connectionName)
    {
        var connection = await GetConnection(connectionName);
        return (await Dao.ListAccounts(connection.Id)).Select(a => Account.From(a, connection.Name)).ToList();
    }

    /// <summary>
    /// Resolves <c>connection:account</c>, where account is an alias, local ID or exact IBAN/identification within
    /// that connection, or a bare local account ID. Unknown and ambiguous selectors fail.
    /// </summary>
    public async Task<(ConnectionRow Connection, AccountRow Account)> ResolveAccount(string selector)
    {
        var separator = selector.IndexOf(':');
        if (separator < 0)
        {
            if (!long.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                throw new BankingUsageException(
                    $"Account selector '{selector}' must be 'connection:account' or a local account ID");
            }

            var account = await Dao.GetAccount(id) ??
                          throw new BankingUsageException($"Unknown account ID {id}");
            return ((await Dao.GetConnectionById(account.ConnectionId))!, account);
        }

        var connection = await GetConnection(selector[..separator]);
        var part = selector[(separator + 1)..];
        var accounts = (await Dao.ListAccounts(connection.Id)).ToList();

        var byAlias = accounts.Where(a => a.Alias == part).ToList();
        var matches = byAlias.Count > 0
            ? byAlias
            : accounts.Where(a => a.Id.ToString(CultureInfo.InvariantCulture) == part ||
                                  string.Equals(a.Iban, part, StringComparison.OrdinalIgnoreCase) ||
                                  a.OtherIdentification == part).ToList();

        return matches.Count switch
        {
            1 => (connection, matches[0]),
            0 => throw new BankingUsageException(
                $"No account '{part}' in connection '{connection.Name}'. Run 'bank accounts --connection {connection.Name}'."),
            _ => throw new BankingUsageException(
                $"'{selector}' matches {matches.Count} accounts; use the local account ID instead"),
        };
    }

    public async Task<Account> SetAlias(string selector, string? alias, DateTimeOffset now)
    {
        if (alias != null && (alias.Length == 0 || alias.Contains(':') || alias.All(char.IsAsciiDigit)))
        {
            throw new BankingUsageException("An alias must be non-empty, contain no ':' and not be all digits");
        }

        var (connection, account) = await ResolveAccount(selector);
        if (alias != null && (await Dao.ListAccounts(connection.Id)).Any(a => a.Alias == alias && a.Id != account.Id))
        {
            throw new BankingUsageException($"Alias '{alias}' is already used in connection '{connection.Name}'");
        }

        await Dao.SetAlias(account.Id, alias, Timestamp(now));
        return Account.From((await Dao.GetAccount(account.Id))!, connection.Name);
    }

    /// <summary>Archived booked transactions for one account, ordered by date then ID. Bounds are inclusive.</summary>
    public async Task<IReadOnlyList<ArchivedTransaction>> QueryTransactions(string selector, DateRange range)
    {
        var (connection, account) = await ResolveAccount(selector);
        var label = Account.From(account, connection.Name).Selector;
        return (await Dao.QueryTransactions(account.Id, Date(range.From), Date(range.To)))
            .Select(t => ArchivedTransaction.From(t, label)).ToList();
    }
}
