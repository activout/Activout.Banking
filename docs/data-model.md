# Data model

The archive is one SQLite database (`bank.db`) with foreign keys on and WAL journalling. The schema version is kept in
`PRAGMA user_version` and migrations run automatically. Dates are `YYYY-MM-DD` text and timestamps are ISO 8601 UTC
text. Amounts are signed decimal text using the invariant culture: they keep exactly the provider's precision, never
SQLite REAL.

| Table | Contents |
| --- | --- |
| `connection` | Name, bank, country, PSU type, current provider session ID, actual consent expiry (`valid_until` from the session), times |
| `account` | Stable local ID, connection, current provider UID, identification hash(es), IBAN or other identification, currency, name/details/product, alias, raw account JSON |
| `bank_transaction` | Local ID, account, identity key, entry reference, transaction ID, status, booking/value/transaction dates, signed amount, currency, counterparty, description, reference, raw transaction JSON, first/last seen |
| `sync_run` | Every sync attempt: connection, start/finish, outcome (`success`, `partial`, `failed`), counts and per-account results |
| `sync_state` | Per account: archived coverage (`covered_from`, `covered_to`), whether the initial history fetch completed, last success |

The raw JSON is sensitive personal data. It has the same protection as the rest of the archive.

## Selectors

Accounts are selected as `connection:account`. The account part is matched in this order: alias, local ID, IBAN, then
other identification. A bare local ID also works. If a selector matches nothing or more than one account, the command
fails rather than guessing. Aliases are unique within a connection.

## Account identity across consents

Enable Banking issues new account UIDs for every session. When a session is created, each returned account is matched
to an existing local account in the same connection by any shared identification hash, or the same IBAN or other
identification, and the same currency. A match keeps the local ID, alias and history and records the new UID. An
unmatched account becomes a new local account. If one returned account matches several local accounts, `bank` refuses
to merge them. Accounts missing from the new session stay in the archive, marked as not in the current session.

## Transaction identity

Each transaction gets an identity key that is unique within its local account:

- `ref:<entry_reference>` when the provider supplies `entry_reference`. Enable Banking documents it as unique and
  immutable for an account across sessions.
- Otherwise `fp:<hash>:<n>`. The hash covers dates, signed amount, currency, transaction ID, reference, counterparty
  and description. `n` is the occurrence number of that hash, in provider order, within the fetched window. Two
  identical payments on the same day therefore stay two rows. Fetching the window again gives them the same two keys,
  because identical transactions share a date and a date-bounded window holds all of them or none.

When a re-fetched transaction's raw JSON differs (a correction), the row is updated and counted as updated. Transactions
are never deleted when they disappear from a later fetch.

**Limitation:** fingerprint keys rely on the bank returning duplicates in a stable order with stable content. If a
bank without `entry_reference` changes a transaction's description afterwards, the archive keeps both versions. Check the
raw JSON from your own bank.

## Pending transactions

V0 archives only booked (`BOOK`) transactions; pending and other statuses are skipped. Exports therefore never mix
pending and booked items.

## Synchronisation

- The first sync for an account uses Enable Banking's `strategy=longest`, with `initialHistoryFrom` if it is configured.
  The bank decides how much history it returns. Recorded coverage starts at the earliest transaction actually received,
  or the configured date, never an assumed period.
- Later syncs fetch from `covered_to − overlapDays` up to today (inclusive UTC dates), catching late bookings and
  corrections.
- Each account's whole window, every page, is fetched before anything is written. Its rows and new coverage are then
  committed in one SQLite transaction. No write transaction is held open during network calls. A failed page leaves the
  previous coverage and history untouched.
- One failing account does not undo others. The run is recorded as `partial` and the command exits with code 5.
- A lock file (`sync.lock`, held exclusively) prevents concurrent syncs. A second sync waits up to 30 seconds, then
  exits with code 6.
- Read requests honour `Retry-After` and back off at most 4 attempts. Authorisation code exchange and other
  state-changing requests are never retried.

## Exports

`bank export` reads only the archive. Bounds are inclusive booking dates; `--month YYYY-MM` is a calendar month and
excludes `--from`/`--to`. Rows are ordered by booking date, value date and local ID. CSV is UTF-8 without BOM, follows
RFC 4180 quoting and uses CRLF line endings. Its columns are:
`account, booking_date, value_date, amount, currency, status, counterparty, description, reference, entry_reference,
transaction_id, local_id`. JSON gives amounts as strings to keep them exact. There is no running balance; balances come
only from `bank balances`, which fetches them live from the bank. An export is not an official bank statement.
