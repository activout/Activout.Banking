using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Activout.Banking.Archive;
using Activout.Banking.EnableBanking;

namespace Activout.Banking;

/// <summary>Normalises received transactions and assigns archive identity keys.</summary>
public static class TransactionMapper
{
    public const string Booked = "BOOK";

    /// <summary>
    /// Maps one fetched window of transactions for one account. Identity:
    /// <list type="bullet">
    /// <item><c>ref:{entry_reference}</c> when the provider supplies entry_reference, documented as unique and
    /// immutable per account.</item>
    /// <item>Otherwise <c>fp:{hash}:{n}</c>: a hash over every normalised field plus the occurrence number n of that
    /// hash in provider order within the window. Two identical legitimate transactions on the same day therefore stay
    /// two rows, and re-fetching the same window maps them to the same two keys. Identical transactions share a date,
    /// so a date-bounded window always contains all or none of them.</item>
    /// </list>
    /// Only booked transactions are returned; pending observations are not archived in V0.
    /// </summary>
    public static IReadOnlyList<TransactionRow> MapWindow(long accountId, IEnumerable<JsonElement> received,
        DateTimeOffset now)
    {
        var occurrences = new Dictionary<string, int>();
        var seenReferences = new HashSet<string>();
        var result = new List<TransactionRow>();
        var timestamp = BankArchive.Timestamp(now);

        foreach (var raw in received)
        {
            var wire = raw.Deserialize<WireTransaction>(Json.Options) ??
                       throw new BankingApiException(System.Net.HttpStatusCode.OK, null, "Null transaction in page");
            if (!string.Equals(wire.Status, Booked, StringComparison.OrdinalIgnoreCase)) continue;

            var amount = SignedAmount(wire);
            var counterparty = IsDebit(wire)
                ? Join(wire.Creditor?.Name, Identification(wire.CreditorAccount))
                : Join(wire.Debtor?.Name, Identification(wire.DebtorAccount));
            var description = Join([..wire.RemittanceInformation ?? [], wire.Note]);

            string identityKey;
            if (!string.IsNullOrEmpty(wire.EntryReference) && seenReferences.Add(wire.EntryReference))
            {
                identityKey = "ref:" + wire.EntryReference;
            }
            else
            {
                // A duplicated entry_reference within one window breaks the documented contract; fall back to
                // the fingerprint so neither observation is lost.
                var fingerprint = Fingerprint(wire, amount, counterparty, description);
                var n = occurrences.GetValueOrDefault(fingerprint) + 1;
                occurrences[fingerprint] = n;
                identityKey = $"fp:{fingerprint}:{n}";
            }

            result.Add(new TransactionRow
            {
                AccountId = accountId,
                IdentityKey = identityKey,
                EntryReference = wire.EntryReference,
                TransactionId = wire.TransactionId,
                Status = Booked,
                BookingDate = wire.BookingDate,
                ValueDate = wire.ValueDate,
                TransactionDate = wire.TransactionDate,
                Amount = amount.ToString(CultureInfo.InvariantCulture),
                Currency = wire.TransactionAmount.Currency,
                Counterparty = counterparty,
                Description = description,
                Reference = wire.ReferenceNumber,
                RawJson = raw.GetRawText(),
                FirstSeenAt = timestamp,
                LastSeenAt = timestamp,
            });
        }

        return result;
    }

    /// <summary>Provider amounts are unsigned decimal strings; the credit/debit indicator gives the sign.</summary>
    public static decimal SignedAmount(WireTransaction wire)
    {
        if (!decimal.TryParse(wire.TransactionAmount.Amount, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var amount))
        {
            throw new BankingApiException(System.Net.HttpStatusCode.OK, null,
                "Transaction amount is not a decimal number");
        }

        return IsDebit(wire) ? -Math.Abs(amount) : amount;
    }

    private static bool IsDebit(WireTransaction wire) =>
        string.Equals(wire.CreditDebitIndicator, "DBIT", StringComparison.OrdinalIgnoreCase);

    public static string? Identification(AccountIdentification? id) =>
        id?.Iban is { Length: > 0 } iban ? iban : id?.Other?.Identification is { Length: > 0 } other ? other : null;

    private static string? Join(params string?[] parts)
    {
        var joined = string.Join(" | ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return joined.Length == 0 ? null : joined;
    }

    private static string Fingerprint(WireTransaction wire, decimal amount, string? counterparty, string? description)
    {
        string?[] fields =
        [
            wire.BookingDate, wire.ValueDate, wire.TransactionDate, amount.ToString(CultureInfo.InvariantCulture),
            wire.TransactionAmount.Currency, wire.TransactionId, wire.ReferenceNumber, counterparty, description,
        ];
        var canonical = string.Join("\u001f", fields.Select(f => f ?? ""));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }
}
