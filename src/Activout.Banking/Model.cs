using System.Globalization;
using System.Text.Json.Serialization;
using Activout.Banking.Archive;

namespace Activout.Banking;

public static class PsuTypes
{
    public const string Personal = "personal";
    public const string Business = "business";

    public static string Parse(string value) => value.ToLowerInvariant() switch
    {
        Personal => Personal,
        Business => Business,
        _ => throw new BankingUsageException($"PSU type must be '{Personal}' or '{Business}', not '{value}'"),
    };
}

public sealed record Connection(
    long Id,
    string Name,
    string Bank,
    string Country,
    string PsuType,
    bool HasSession,
    DateTimeOffset? ValidUntil)
{
    internal static Connection From(ConnectionRow row) => new(row.Id, row.Name, row.AspspName, row.AspspCountry,
        row.PsuType, row.SessionId != null, ParseTimestamp(row.ValidUntil));

    internal static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
}

public sealed record Account(
    long Id,
    string Connection,
    string? Alias,
    string? Iban,
    string? OtherIdentification,
    string? Currency,
    string? Name,
    string? Details,
    string? Product,
    bool InCurrentSession)
{
    /// <summary>Preferred selector: connection:alias, falling back to connection:local-id.</summary>
    public string Selector => $"{Connection}:{Alias ?? Id.ToString(CultureInfo.InvariantCulture)}";

    internal static Account From(AccountRow row, string connection) => new(row.Id, connection, row.Alias, row.Iban,
        row.OtherIdentification, row.Currency, row.Name, row.Details, row.Product, row.ProviderUid != null);
}

/// <summary>A booked transaction from the archive. Amounts are signed: negative for debits.</summary>
public sealed record ArchivedTransaction(
    long Id,
    string Account,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal Amount,
    string Currency,
    string Status,
    string? Counterparty,
    string? Description,
    string? Reference,
    string? EntryReference,
    string? TransactionId)
{
    internal static ArchivedTransaction From(TransactionRow row, string account) => new(row.Id, account,
        BankArchive.ParseDate(row.BookingDate ?? row.ValueDate ?? row.TransactionDate), BankArchive.ParseDate(row.ValueDate),
        decimal.Parse(row.Amount, NumberStyles.Number, CultureInfo.InvariantCulture), row.Currency, row.Status,
        row.Counterparty, row.Description, row.Reference, row.EntryReference, row.TransactionId);
}

/// <summary>Inclusive calendar date range.</summary>
public sealed record DateRange(DateOnly From, DateOnly To)
{
    public static DateRange Month(string yearMonth) =>
        DateOnly.TryParseExact(yearMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var first)
            ? new DateRange(first, first.AddMonths(1).AddDays(-1))
            : throw new BankingUsageException($"--month must be YYYY-MM, not '{yearMonth}'");

    /// <summary>Combines --month or --from/--to; they are mutually exclusive.</summary>
    public static DateRange Select(string? month, DateOnly? from, DateOnly? to)
    {
        if (month != null)
        {
            if (from != null || to != null)
                throw new BankingUsageException("--month cannot be combined with --from or --to");
            return Month(month);
        }

        var range = new DateRange(from ?? DateOnly.MinValue, to ?? DateOnly.MaxValue);
        return range.From <= range.To
            ? range
            : throw new BankingUsageException("--from must not be later than --to");
    }
}
