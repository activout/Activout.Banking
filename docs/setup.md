# Setup

The `bank` command reads your own accounts through Enable Banking's account information service (AIS). You register an
Enable Banking application and authorise each bank connection with your bank's own login, for example BankID. `bank` then
synchronises transactions into a local SQLite archive.

## 1. Install

```sh
dotnet tool install --global Activout.Banking.Cli       # once published
# or from a local build:
dotnet pack -c Release
dotnet tool install --global --add-source ./artifacts Activout.Banking.Cli
bank --help
```

## 2. Directories

| Platform | Configuration and keys | Archive (`bank.db`) |
| --- | --- | --- |
| Linux | `$XDG_CONFIG_HOME/activout-banking` (default `~/.config/activout-banking`) | `$XDG_DATA_HOME/activout-banking` (default `~/.local/share/activout-banking`) |
| macOS | `~/Library/Application Support/Activout.Banking` | same |
| Windows | `%APPDATA%\Activout.Banking` | `%LOCALAPPDATA%\Activout.Banking` |

`--data-dir <dir>` uses one directory for everything, which suits portable use and tests.

`bank` creates its directories and database readable only by you on Linux and macOS (modes 700 and 600). On Windows it
relies on the per-user profile's default access control; make sure no other accounts can read these folders. The
database is **not encrypted**, so protect backups the same way as the signing key. To back up, copy `bank.db` while no
`bank` command is running, or use `sqlite3 bank.db ".backup copy.db"`.

## 3. Signing key

Enable Banking authenticates your application with an RSA key. The private key stays on your machine and only the
certificate is registered.

```sh
cd ~/.config/activout-banking        # your configuration directory
openssl req -x509 -newkey rsa:4096 -nodes -days 3650 -subj "/CN=Activout.Banking" \
  -keyout signing-key.pem -out signing-certificate.pem
chmod 600 signing-key.pem
```

`bank` accepts PEM RSA private keys (`BEGIN PRIVATE KEY` or `BEGIN RSA PRIVATE KEY`). Never put the key in a repository.

## 4. Callback URL

After you approve access, the bank redirects your browser to the application's redirect URL with a one-time `code` and
the `state` that `bank` generated. Pick one setup:

**A. Loopback HTTPS (default).** Register `https://localhost:8765/callback`. During `bank connect`, a short-lived
listener on 127.0.0.1:8765 serves that URL over TLS. It uses a self-signed certificate that `bank` creates in `tls/`
under the configuration directory, separate from the signing key. Your browser warns about the certificate until you
trust it. You can click through the warning for that page only, or trust it explicitly:

- macOS: `security add-trusted-cert -r trustRoot -p ssl -k ~/Library/Keychains/login.keychain-db tls/localhost.crt`
- Linux: use your browser's certificate settings, or `certutil` for Chrome/Firefox NSS databases.
- Windows: `certutil -user -addstore Root tls\localhost.crt`

`bank` never changes trust settings itself, and `bank doctor` does not claim the certificate is trusted.

**B. Hosted forwarder.** Host [`site/callback/index.html`](../site/callback/index.html) on an HTTPS site you control,
register its URL (for example `https://bank.example.com/callback/`) and set it as `redirectUrl`. The page forwards
the query string to `http://127.0.0.1:8765<same path>`, where `bank` listens over plain HTTP, so no local certificate
is needed. Your web server's access log sees the one-time code. The code is single-use and worthless without your
signing key. Edit `PORT` in the page if you change `callbackPort`.

**Manual.** With `--manual`, `bank connect` does not listen. Instead it reads the full redirected URL from stdin, so you
paste it from the browser's address bar. The browser may show a connection error page; the URL is still valid. Use this
on remote or headless machines. The URL contains the one-time code, so don't paste it into chat or shell history.

## 5. Register the application

Use the [control panel](https://enablebanking.com/cp/) or the official CLI (`pip install enablebanking-cli`):

```sh
enablebanking auth login you@example.com           # completes through an email link
enablebanking app register --environment PRODUCTION --name "My bank archive" \
  --redirect-urls https://localhost:8765/callback \
  --description "Personal read-only transaction archive" \
  --gdpr-email you@example.com --privacy-url https://example.com/privacy --terms-url https://example.com/terms \
  --cert-path signing-certificate.pem
```

Production redirect URLs must be HTTPS. Never register a sandbox application expecting to switch it to production
later; they are separate applications.

A new production application is **restricted**: it can only read accounts you link to it yourself. In the control panel,
choose **Activate by linking accounts** and complete your bank's authorisation for the accounts you want. This activates
the application. It does not replace `bank connect`, which creates the API sessions `bank` uses. Restricted use is
governed by Enable Banking's terms ("Restriction of Use"); check that your intended use, including any company
accounts, fits them.

## 6. Configure

Create `config.json` in the configuration directory:

```json
{
  "applicationId": "00000000-0000-0000-0000-000000000000",
  "privateKeyPath": "signing-key.pem",
  "redirectUrl": "https://localhost:8765/callback"
}
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `applicationId` | (required) | Application ID (the JWT `kid`) |
| `privateKeyPath` | `signing-key.pem` | PEM RSA key; relative to the configuration directory |
| `apiBaseUrl` | `https://api.enablebanking.com` | API endpoint |
| `redirectUrl` | `https://localhost:8765/callback` | Exactly as registered |
| `callbackPort` | redirect port, or 8765 | Local listener port for a hosted forwarder |
| `overlapDays` | 7 | Days re-fetched before the last synchronised day |
| `initialHistoryFrom` | none | Earliest date for the first sync; none lets the bank decide |

Then run `bank doctor`.

## 7. Connect and synchronise

```sh
bank connect personal --bank SEB --country SE --psu-type personal
bank connect business --bank SEB --country SE --psu-type business
bank accounts
bank account alias business:3 operating
bank sync --all
bank status
```

`bank connect` checks that the bank offers AIS for the PSU type and fails with a clear message if, for example,
business access is unavailable. It requests the longest consent the bank allows. When consent expires, run
`bank connect <name>` again; local account IDs, aliases and history are kept.

Unattended `bank sync --all --json` (from cron, for example) never starts an authorisation. It exits with code 3 when you
need to authorise again.
