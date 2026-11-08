# Activout.Banking

> **Proof of concept.** This is an experiment, not a supported product. It may change or stop working without notice.

Archive and export your own bank transactions using
[Enable Banking](https://enablebanking.com/) account information (AIS). The service is read-only and never makes payments.

- **Activout.Banking** is a .NET 10 library. It covers API signing, consent, account reconciliation, the SQLite
  archive and synchronisation.
- **Activout.Banking.Cli** is a .NET tool that installs the `bank` command.

```sh
bank connect personal --bank SEB --country SE --psu-type personal   # BankID in the browser
bank account alias personal:1 salary
bank sync --all
bank export personal:salary --month 2026-09 --format csv > september.csv
bank status
bank doctor
```

Synchronisation needs the bank API. `transactions`, `export`, `status`, `connections` and `accounts` read only the local
archive, so they work offline. Exports are your own archive, not official bank statements.

## Download

Get the file for your computer from the [latest release](https://github.com/activout/Activout.Banking/releases/latest). It is a single
program with nothing else to install. The files are not code signed yet, so your computer warns you the first time.

- **Windows:** download `bank-win-x64.exe`, rename it to `bank.exe` and open PowerShell in that folder. If Windows
  says "Windows protected your PC", click **More info**, then **Run anyway**. Type `.\bank.exe` where this README
  says `bank`.
- **Mac (M1 or newer):** download `bank-osx-arm64.tar.gz`, then in Terminal:
  ```sh
  cd ~/Downloads
  tar -xzf bank-osx-arm64.tar.gz
  xattr -d com.apple.quarantine bank
  sudo mv bank /usr/local/bin/
  ```
- **Linux:** download `bank-linux-x64.tar.gz`, run `tar -xzf bank-linux-x64.tar.gz` and move `bank` to a folder on
  your `PATH`.

Check a download against the release's `SHA256SUMS`, or verify its build provenance with
`gh attestation verify <file> -R activout/Activout.Banking`.

With .NET 10 installed you can use the .NET tool instead: `dotnet tool install -g Activout.Banking.Cli`.

## Agent skill

[`skills/bank/SKILL.md`](skills/bank/SKILL.md) teaches AI coding agents (for example Claude Code) to answer questions
about your transactions with `bank`: offline first, `--json`, never starting consent, asking before syncing. Install it
for Claude Code with `ln -s "$PWD/skills/bank" ~/.claude/skills/bank`.

## Documentation

- [Setup](docs/setup.md): Enable Banking application, keys, callback and configuration
- [Data model](docs/data-model.md): archive tables, account and transaction identity, sync rules
- [Troubleshooting](docs/troubleshooting.md): exit codes, common errors, doctor

## Building

```sh
dotnet test
dotnet pack -c Release                    # writes artifacts/*.nupkg
dotnet tool install --global --add-source ./artifacts Activout.Banking.Cli
bank --help
```

## Releasing

Push a tag such as `v0.2.0` (or `v0.2.0-rc.1` for a prerelease). The Publish workflow tests and packs with that
version, attests build provenance, pushes both packages to NuGet.org via trusted publishing and creates the GitHub
release with self-contained `bank` executables for Windows, macOS and Linux. Verify a package with `gh attestation verify <file>.nupkg -R activout/Activout.Banking`.

## Licence

MIT. See [LICENSE](LICENSE).

## References inspected

Built from current upstream documentation and code (2026-10-02), not copied:

- Enable Banking API reference, AIS flow and restricted production docs at enablebanking.com/docs
- [enablebanking-api-samples](https://github.com/enablebanking/enablebanking-api-samples) at `7a16788840c42b63c305652f8ea29ab69207db25`
- [goBankCli](https://github.com/BramVR/goBankCli) (MIT) at `d5aa11a90f5e717648626dd2d205d14c6352447c`, read for design lessons only
