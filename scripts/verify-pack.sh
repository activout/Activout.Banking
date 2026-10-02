#!/bin/sh
# Packs both packages, installs the tool into a temporary tool path and runs it.
set -eu
cd "$(dirname "$0")/.."
rm -rf artifacts
dotnet pack -c Release
tools=$(mktemp -d)
data=$(mktemp -d)
trap 'rm -rf "$tools" "$data"' EXIT
dotnet tool install --tool-path "$tools" --add-source ./artifacts Activout.Banking.Cli --version "$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)"
"$tools/bank" --help >/dev/null
"$tools/bank" status --json --data-dir "$data"
echo "verify-pack: OK"
