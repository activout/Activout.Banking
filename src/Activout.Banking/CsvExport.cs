using System.Globalization;

namespace Activout.Banking;

/// <summary>UTF-8 RFC 4180 CSV of archived transactions. This is not an official bank statement.</summary>
public static class CsvExport
{
    public static readonly string[] Columns =
    [
        "account", "booking_date", "value_date", "amount", "currency", "status", "counterparty", "description",
        "reference", "entry_reference", "transaction_id", "local_id",
    ];

    public static void Write(TextWriter writer, IEnumerable<ArchivedTransaction> transactions)
    {
        WriteRow(writer, Columns);
        foreach (var t in transactions)
        {
            WriteRow(writer,
            [
                t.Account, Date(t.BookingDate), Date(t.ValueDate), t.Amount.ToString(CultureInfo.InvariantCulture),
                t.Currency, t.Status, t.Counterparty, t.Description, t.Reference, t.EntryReference, t.TransactionId,
                t.Id.ToString(CultureInfo.InvariantCulture),
            ]);
        }
    }

    private static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void WriteRow(TextWriter writer, IEnumerable<string?> fields)
    {
        writer.Write(string.Join(",", fields.Select(Escape)));
        writer.Write("\r\n");
    }

    internal static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        return field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
