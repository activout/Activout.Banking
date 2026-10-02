using System.Text.Json;
using static Activout.Banking.Tests.TestEnvironment;

namespace Activout.Banking.Tests;

public class TransactionMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement[] Parse(params string[] transactions) =>
        transactions.Select(t => JsonDocument.Parse(t).RootElement.Clone()).ToArray();

    [Fact]
    public void DebitsAreNegativeAndPrecisionIsPreserved()
    {
        var rows = TransactionMapper.MapWindow(1,
            Parse(Transaction("a", "100.10", "DBIT", "2026-09-01"), Transaction("b", "0.05", "CRDT", "2026-09-01")), Now);

        Assert.Equal("-100.10", rows[0].Amount);
        Assert.Equal("0.05", rows[1].Amount);
    }

    [Fact]
    public void IdenticalTransactionsWithoutReferenceKeepMultiplicityAcrossRepeatedFetches()
    {
        var window = Parse(
            Transaction(null, "45.00", "DBIT", "2026-09-03", "Coffee"),
            Transaction(null, "45.00", "DBIT", "2026-09-03", "Coffee"),
            Transaction(null, "45.00", "DBIT", "2026-09-04", "Coffee"));

        var first = TransactionMapper.MapWindow(1, window, Now).Select(r => r.IdentityKey).ToList();
        var second = TransactionMapper.MapWindow(1, window, Now.AddDays(1)).Select(r => r.IdentityKey).ToList();

        Assert.Equal(3, first.Distinct().Count());
        Assert.Equal(first, second);
    }

    [Fact]
    public void EntryReferenceIsTheIdentityWhenPresent()
    {
        var rows = TransactionMapper.MapWindow(1, Parse(Transaction("ARCHIVE-1", "1.00", "CRDT", "2026-09-01")), Now);

        Assert.Equal("ref:ARCHIVE-1", Assert.Single(rows).IdentityKey);
    }

    [Fact]
    public void DuplicatedEntryReferenceWithinWindowIsNotLost()
    {
        var rows = TransactionMapper.MapWindow(1, Parse(
            Transaction("dup", "1.00", "CRDT", "2026-09-01", "one"),
            Transaction("dup", "2.00", "CRDT", "2026-09-01", "two")), Now);

        Assert.Equal(2, rows.Select(r => r.IdentityKey).Distinct().Count());
    }

    [Fact]
    public void PendingTransactionsAreNotArchived()
    {
        var rows = TransactionMapper.MapWindow(1, Parse(
            Transaction("p", "1.00", "DBIT", "2026-09-01", status: "PDNG"),
            Transaction("b", "1.00", "DBIT", "2026-09-01")), Now);

        Assert.Equal("ref:b", Assert.Single(rows).IdentityKey);
    }

    [Fact]
    public void MinimalTransactionWithOnlyRequiredFieldsMaps()
    {
        var rows = TransactionMapper.MapWindow(1, Parse(
            """{"transaction_amount":{"amount":"3","currency":"EUR"},"credit_debit_indicator":"DBIT","status":"BOOK"}"""),
            Now);

        var row = Assert.Single(rows);
        Assert.Equal("-3", row.Amount);
        Assert.Null(row.BookingDate);
        Assert.Null(row.Description);
    }

    [Fact]
    public void CsvEscapesSeparatorsQuotesAndNewlines()
    {
        Assert.Equal("plain", CsvExport.Escape("plain"));
        Assert.Equal("\"a,b\"", CsvExport.Escape("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExport.Escape("say \"hi\""));
        Assert.Equal("\"line\nbreak\"", CsvExport.Escape("line\nbreak"));
        Assert.Equal("", CsvExport.Escape(null));
    }
}
