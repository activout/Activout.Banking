using Activout.DatabaseClient.Attributes;

namespace Activout.Banking.Archive;

// Rows map snake_case columns via Dapper's MatchNamesWithUnderscores. Dates are ISO yyyy-MM-dd text,
// timestamps ISO 8601 UTC text and amounts invariant decimal text (never SQLite REAL).

public sealed class ConnectionRow
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string AspspName { get; init; } = "";
    public string AspspCountry { get; init; } = "";
    public string PsuType { get; init; } = "";
    public string? SessionId { get; init; }
    public string? ValidUntil { get; init; }
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
}

public sealed class AccountRow
{
    public long Id { get; init; }
    public long ConnectionId { get; init; }
    public string? ProviderUid { get; init; }
    public string? IdentificationHash { get; init; }
    public string IdentificationHashes { get; init; } = "[]";
    public string? Iban { get; init; }
    public string? OtherIdentification { get; init; }
    public string? Currency { get; init; }
    public string? Name { get; init; }
    public string? Details { get; init; }
    public string? Product { get; init; }
    public string? Alias { get; init; }
    public string? RawJson { get; init; }
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
}

public sealed class TransactionRow
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public string IdentityKey { get; init; } = "";
    public string? EntryReference { get; init; }
    public string? TransactionId { get; init; }
    public string Status { get; init; } = "";
    public string? BookingDate { get; init; }
    public string? ValueDate { get; init; }
    public string? TransactionDate { get; init; }
    public string Amount { get; init; } = "0";
    public string Currency { get; init; } = "";
    public string? Counterparty { get; init; }
    public string? Description { get; init; }
    public string? Reference { get; init; }
    public string RawJson { get; init; } = "";
    public string FirstSeenAt { get; init; } = "";
    public string LastSeenAt { get; init; } = "";
}

public sealed class SyncRunRow
{
    public long Id { get; init; }
    public long ConnectionId { get; init; }
    public string StartedAt { get; init; } = "";
    public string? FinishedAt { get; init; }
    public string Outcome { get; init; } = "";
    public long Added { get; init; }
    public long Updated { get; init; }
    public string? DetailsJson { get; init; }
}

public sealed class SyncStateRow
{
    public long AccountId { get; init; }
    public string? CoveredFrom { get; init; }
    public string? CoveredTo { get; init; }
    public bool InitialComplete { get; init; }
    public string? LastSuccessAt { get; init; }
}

public interface IArchiveDao
{
    [SqlQuery("PRAGMA user_version")]
    Task<long> GetSchemaVersion();

    [SqlUpdate(Schema.V1)]
    Task ApplySchemaV1();

    // Connections

    [SqlQuery("SELECT * FROM connection ORDER BY name")]
    Task<IEnumerable<ConnectionRow>> ListConnections();

    [SqlQuery("SELECT * FROM connection WHERE name = @name")]
    Task<ConnectionRow?> GetConnection(string name);

    [SqlQuery("SELECT * FROM connection WHERE id = @id")]
    Task<ConnectionRow?> GetConnectionById(long id);

    [SqlQuery("""
              INSERT INTO connection (name, aspsp_name, aspsp_country, psu_type, created_at, updated_at)
              VALUES (@name, @aspspName, @aspspCountry, @psuType, @now, @now) RETURNING id
              """)]
    Task<long> InsertConnection(string name, string aspspName, string aspspCountry, string psuType, string now);

    [SqlUpdate("UPDATE connection SET session_id = @sessionId, valid_until = @validUntil, updated_at = @now WHERE id = @id")]
    Task<int> UpdateConnectionSession(long id, string? sessionId, string? validUntil, string now);

    // Accounts

    [SqlQuery("SELECT * FROM account WHERE connection_id = @connectionId ORDER BY id")]
    Task<IEnumerable<AccountRow>> ListAccounts(long connectionId);

    [SqlQuery("SELECT * FROM account WHERE id = @id")]
    Task<AccountRow?> GetAccount(long id);

    [SqlQuery("""
              INSERT INTO account (connection_id, provider_uid, identification_hash, identification_hashes, iban,
                                   other_identification, currency, name, details, product, raw_json, created_at, updated_at)
              VALUES (@ConnectionId, @ProviderUid, @IdentificationHash, @IdentificationHashes, @Iban,
                      @OtherIdentification, @Currency, @Name, @Details, @Product, @RawJson, @CreatedAt, @UpdatedAt)
              RETURNING id
              """)]
    Task<long> InsertAccount([BindProperties] AccountRow account);

    [SqlUpdate("""
               UPDATE account SET provider_uid = @ProviderUid, identification_hash = @IdentificationHash,
                   identification_hashes = @IdentificationHashes, iban = @Iban, other_identification = @OtherIdentification,
                   currency = @Currency, name = @Name, details = @Details, product = @Product, raw_json = @RawJson,
                   updated_at = @UpdatedAt
               WHERE id = @Id
               """)]
    Task<int> UpdateAccountFromProvider([BindProperties] AccountRow account);

    [SqlUpdate("UPDATE account SET provider_uid = NULL, updated_at = @now WHERE connection_id = @connectionId")]
    Task<int> ClearProviderUids(long connectionId, string now);

    [SqlUpdate("UPDATE account SET alias = @alias, updated_at = @now WHERE id = @id")]
    Task<int> SetAlias(long id, string? alias, string now);

    // Transactions

    [SqlQuery("SELECT * FROM bank_transaction WHERE account_id = @accountId AND identity_key = @identityKey")]
    Task<TransactionRow?> FindTransaction(long accountId, string identityKey);

    [SqlUpdate("""
               INSERT INTO bank_transaction (account_id, identity_key, entry_reference, transaction_id, status,
                   booking_date, value_date, transaction_date, amount, currency, counterparty, description, reference,
                   raw_json, first_seen_at, last_seen_at)
               VALUES (@AccountId, @IdentityKey, @EntryReference, @TransactionId, @Status, @BookingDate, @ValueDate,
                   @TransactionDate, @Amount, @Currency, @Counterparty, @Description, @Reference, @RawJson,
                   @FirstSeenAt, @LastSeenAt)
               """)]
    Task<int> InsertTransaction([BindProperties] TransactionRow transaction);

    [SqlUpdate("""
               UPDATE bank_transaction SET entry_reference = @EntryReference, transaction_id = @TransactionId,
                   status = @Status, booking_date = @BookingDate, value_date = @ValueDate,
                   transaction_date = @TransactionDate, amount = @Amount, currency = @Currency,
                   counterparty = @Counterparty, description = @Description, reference = @Reference,
                   raw_json = @RawJson, last_seen_at = @LastSeenAt
               WHERE id = @Id
               """)]
    Task<int> UpdateTransaction([BindProperties] TransactionRow transaction);

    [SqlUpdate("UPDATE bank_transaction SET last_seen_at = @now WHERE id = @id")]
    Task<int> TouchTransaction(long id, string now);

    [SqlQuery("""
              SELECT * FROM bank_transaction
              WHERE account_id = @accountId
                AND COALESCE(booking_date, value_date, transaction_date) BETWEEN @from AND @to
              ORDER BY COALESCE(booking_date, value_date, transaction_date), value_date, id
              """)]
    Task<IEnumerable<TransactionRow>> QueryTransactions(long accountId, string from, string to);

    [SqlQuery("SELECT COUNT(*) FROM bank_transaction WHERE account_id = @accountId")]
    Task<long> CountTransactions(long accountId);

    // Synchronisation

    [SqlQuery("""
              INSERT INTO sync_run (connection_id, started_at, outcome) VALUES (@connectionId, @now, 'running')
              RETURNING id
              """)]
    Task<long> InsertSyncRun(long connectionId, string now);

    [SqlUpdate("""
               UPDATE sync_run SET finished_at = @now, outcome = @outcome, added = @added, updated = @updated,
                   details_json = @detailsJson
               WHERE id = @id
               """)]
    Task<int> FinishSyncRun(long id, string now, string outcome, long added, long updated, string detailsJson);

    [SqlQuery("""
              SELECT * FROM sync_run WHERE connection_id = @connectionId AND outcome = @outcome
              ORDER BY started_at DESC, id DESC LIMIT 1
              """)]
    Task<SyncRunRow?> LatestSyncRun(long connectionId, string outcome);

    [SqlQuery("SELECT * FROM sync_state WHERE account_id = @accountId")]
    Task<SyncStateRow?> GetSyncState(long accountId);

    [SqlUpdate("""
               INSERT INTO sync_state (account_id, covered_from, covered_to, initial_complete, last_success_at)
               VALUES (@AccountId, @CoveredFrom, @CoveredTo, @InitialComplete, @LastSuccessAt)
               ON CONFLICT (account_id) DO UPDATE SET covered_from = excluded.covered_from,
                   covered_to = excluded.covered_to, initial_complete = excluded.initial_complete,
                   last_success_at = excluded.last_success_at
               """)]
    Task<int> SaveSyncState([BindProperties] SyncStateRow state);
}

internal static class Schema
{
    public const int Version = 1;

    public const string V1 = """
        CREATE TABLE connection (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            aspsp_name TEXT NOT NULL,
            aspsp_country TEXT NOT NULL,
            psu_type TEXT NOT NULL CHECK (psu_type IN ('personal', 'business')),
            session_id TEXT,
            valid_until TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE account (
            id INTEGER PRIMARY KEY,
            connection_id INTEGER NOT NULL REFERENCES connection (id),
            provider_uid TEXT,
            identification_hash TEXT,
            identification_hashes TEXT NOT NULL DEFAULT '[]',
            iban TEXT,
            other_identification TEXT,
            currency TEXT,
            name TEXT,
            details TEXT,
            product TEXT,
            alias TEXT,
            raw_json TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE (connection_id, alias)
        );
        CREATE TABLE bank_transaction (
            id INTEGER PRIMARY KEY,
            account_id INTEGER NOT NULL REFERENCES account (id),
            identity_key TEXT NOT NULL,
            entry_reference TEXT,
            transaction_id TEXT,
            status TEXT NOT NULL,
            booking_date TEXT,
            value_date TEXT,
            transaction_date TEXT,
            amount TEXT NOT NULL,
            currency TEXT NOT NULL,
            counterparty TEXT,
            description TEXT,
            reference TEXT,
            raw_json TEXT NOT NULL,
            first_seen_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            UNIQUE (account_id, identity_key)
        );
        CREATE INDEX ix_bank_transaction_date ON bank_transaction (account_id, booking_date);
        CREATE TABLE sync_run (
            id INTEGER PRIMARY KEY,
            connection_id INTEGER NOT NULL REFERENCES connection (id),
            started_at TEXT NOT NULL,
            finished_at TEXT,
            outcome TEXT NOT NULL CHECK (outcome IN ('running', 'success', 'partial', 'failed')),
            added INTEGER NOT NULL DEFAULT 0,
            updated INTEGER NOT NULL DEFAULT 0,
            details_json TEXT
        );
        CREATE TABLE sync_state (
            account_id INTEGER PRIMARY KEY REFERENCES account (id),
            covered_from TEXT,
            covered_to TEXT,
            initial_complete INTEGER NOT NULL DEFAULT 0,
            last_success_at TEXT
        );
        PRAGMA user_version = 1;
        """;
}
