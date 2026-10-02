---
name: bank
description: Look up the user's own bank accounts, transactions and balances with the read-only `bank` CLI (Activout.Banking, Enable Banking AIS). Use when the user asks what was paid or received, searches for a payment, wants spending or income totals, a month's transactions, an export, account balances, or whether the archive is up to date.
---

# Checking bank transactions with `bank`

`bank` keeps a local SQLite archive of booked transactions for each connected bank account. Most questions are
answered **offline** from that archive. Only `sync` and `balances` contact the bank.

## Ground rules

- **Read-only.** `bank` cannot make payments. Never run `bank connect`: it needs the user's BankID in a browser. When
  consent is missing or expired (exit code 3), tell the user to run `bank connect <connection>` themselves.
- **Ask before `bank sync`.** Banks limit unattended access to a few requests per account per day. Check
  `bank status` first and only suggest a sync if the archive is stale for the question asked.
- **Keep data private.** Transactions are personal and financial. Answer with what was asked: totals, the matching
  rows. Don't repeat full account numbers or dump whole histories into the conversation. Don't write exports anywhere
  except where the user asks.
- **Use `--json`** for anything you will process. stdout then holds exactly one JSON document; progress goes to stderr.

## Find the accounts

```sh
bank status --json      # connections, consent expiry, last sync, per-account coverage and counts
bank accounts --json    # selectors, aliases, IBAN, currency, name
```

Accounts are selected as `connection:alias`, `connection:<local id>` or `connection:<IBAN>`, for example
`activout:operating` or `personal:2`. A bare local ID such as `7` also works. Account `name`/`details` hold the
bank's nickname (for example "BUFFERT"), which helps map the user's words to a selector. Unknown or ambiguous
selectors fail with exit code 2; list accounts rather than guess.

Check coverage before answering "nothing happened": `bank status` gives each account's `covered_from`/`covered_to`.
An empty result outside that range means "not archived", not "no transactions".

## Read transactions (offline)

```sh
bank transactions personal:2 --month 2026-09 --json
bank transactions activout:operating --from 2026-09-01 --to 2026-09-30 --json
```

- Dates are booking dates; both bounds are inclusive. `--month YYYY-MM` excludes `--from`/`--to`. With no range, the
  whole archive is returned.
- Each transaction has `id`, `account`, `booking_date`, `value_date`, `amount`, `currency`, `status` (always `BOOK`),
  `counterparty`, `description`, `reference`, `entry_reference` and `transaction_id`.
- **`amount` is a signed decimal string**: negative means money out, positive money in. Parse it as a decimal (not a
  float) when summing.
- Only booked transactions are archived; pending card purchases are not.

### Typical analyses

```sh
# Search a description or counterparty across a year
bank transactions personal:2 --from 2026-01-01 --to 2026-12-31 --json |
  jq '[.[] | select((.description // "") + " " + (.counterparty // "") | test("spotify"; "i"))]'

# Money in, money out and net for a month
bank transactions activout:operating --month 2026-09 --json | python3 -c '
import json, sys, decimal
t = json.load(sys.stdin); a = [decimal.Decimal(x["amount"]) for x in t]
print("in", sum(x for x in a if x > 0), "out", sum(x for x in a if x < 0), "net", sum(a), "count", len(a))'

# Largest outgoing payments
bank transactions personal:2 --month 2026-09 --json | jq 'sort_by(.amount | tonumber) | .[:10]'
```

To cover several accounts, loop over the selectors from `bank accounts --json`. Transfers between the user's own
accounts appear on both sides, so leave them out of combined totals.

## Export (offline)

```sh
bank export activout:operating --month 2026-09 --format csv -o activout-2026-09.csv
bank export personal:2 --from 2026-01-01 --to 2026-06-30 --format json
```

CSV is UTF-8 with columns `account, booking_date, value_date, amount, currency, status, counterparty, description,
reference, entry_reference, transaction_id, local_id`. An export is the user's own archive, not an official bank
statement.

## Online commands (ask first)

```sh
bank balances personal:2 --json   # live balances from the bank; never calculated from transactions
bank sync personal --json         # or: bank sync --all --json
```

`sync` fetches new and corrected transactions and never duplicates. Its result has per-account `added`, `updated`
and `error`. Never compute a balance by summing transactions; use `bank balances`.

## Exit codes

| Code | Meaning | What to do |
| --- | --- | --- |
| 0 | Success | |
| 2 | Usage/configuration (bad selector, bad dates, missing config) | Fix the arguments; `bank accounts`, `bank doctor --offline` |
| 3 | Bank consent missing/expired | User runs `bank connect <connection>` |
| 4 | API or network failure | Retry later; nothing was lost |
| 5 | Partial sync | Some accounts synced; report the per-account errors |
| 6 | Another sync running | Wait and retry |

With `--json`, failures print `{"ok": false, "exit_code": …, "error": …}`. For setup problems, `bank doctor --json`
checks configuration, keys, callback, archive and application status without changing anything.
