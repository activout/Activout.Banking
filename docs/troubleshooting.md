# Troubleshooting

Start with `bank doctor`, or `bank doctor --offline` to skip API calls. It checks configuration, the signing key and
its permissions, callback URL and port, the callback certificate, the archive and, online, the application status,
registered redirect URLs and each connection's bank. It changes nothing.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Unexpected error (stack trace on stderr) |
| 2 | Usage or configuration error: bad arguments, unknown/ambiguous selector, missing config or key, application rejected |
| 3 | Authorisation required or failed: consent expired or revoked, state mismatch, callback error or timeout |
| 4 | API or network failure |
| 5 | Partial sync: some accounts or connections failed, others were committed |
| 6 | Another sync holds the lock |
| 130 | Cancelled (Ctrl+C) |

With `--json`, stdout holds exactly one JSON document, including on errors (`{"ok": false, "exit_code": …, "error": …}`
for failures before a result exists). Progress and diagnostics always go to stderr. CSV exports to stdout contain only
CSV.

## Common problems

**`REDIRECT_URI_NOT_ALLOWED` or doctor says the redirect is not registered.** `redirectUrl` must exactly match one of the
application's redirect URLs, including scheme, host, port, path and trailing slash.

**`UNAUTHORIZED_ACCESS` / 401 on every request.** Wrong `applicationId`, a key that doesn't match the registered
certificate, or a clock far off (JWTs are valid for five minutes).

**`NO_ACCOUNTS_ADDED` or the application is inactive.** A restricted production application only works after you have
linked your own accounts in the control panel ("Activate by linking accounts").

**"does not offer business account information".** The bank isn't listed for that PSU type with AIS in Enable Banking's
`/aspsps` metadata. Business access may be unavailable for that bank or for restricted applications.

**Browser shows a certificate warning.** Expected with the default self-signed callback certificate. Trust it as
described in [setup](setup.md#4-callback-url) or click through for that page. If the listener never receives the
callback, use `--manual`.

**`Cannot listen on 127.0.0.1:8765`.** Another program (or another `bank connect`) uses the port. Free it, change
`callbackPort` together with the registered URL, or use `--manual`.

**Timed out waiting for the callback.** On a remote or headless machine the browser cannot reach its listener. Run
`bank connect <name> --manual` and paste the redirected URL.

**Exit code 3 from `bank sync`.** The bank session has expired or been revoked. Run `bank connect <name>`. The archive
and aliases are kept.

**Account shows "not in current session".** The latest consent did not include it. History is kept; reconnect and select
it at the bank to resume syncing.

**Rate limits (`ASPSP_RATE_LIMIT_EXCEEDED`, 429).** Banks limit unattended access, often to a few requests a day per
account. Sync less often.
