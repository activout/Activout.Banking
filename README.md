# Activout.Banking

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

## Licence

MIT. See [LICENSE](LICENSE).

## References inspected

Built from current upstream documentation and code (2026-10-02), not copied:

- Enable Banking API reference, AIS flow and restricted production docs at enablebanking.com/docs
- [enablebanking-api-samples](https://github.com/enablebanking/enablebanking-api-samples) at `7a16788840c42b63c305652f8ea29ab69207db25`
- [goBankCli](https://github.com/BramVR/goBankCli) (MIT) at `d5aa11a90f5e717648626dd2d205d14c6352447c`, read for design lessons only
