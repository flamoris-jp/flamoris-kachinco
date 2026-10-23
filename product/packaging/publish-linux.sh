#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
command -v dotnet >/dev/null
command -v cmake >/dev/null
destination="$PWD/artifacts/linux/FLAMORIS-Kachinco-linux-x64"
dotnet publish product/Kachinco.Cli/Kachinco.Cli.csproj -c Release -r linux-x64 --self-contained true -m:1 -o "$destination"
cp product/packaging/install-linux-user.sh product/packaging/create-linux-hub-descriptor.py "$destination/"
chmod +x "$destination/install-linux-user.sh" "$destination/create-linux-hub-descriptor.py"
cp LICENSE docs/linux-cli.md "$destination/"
"$destination/kachinco" --help
"$destination/kachinco" catalog > "$destination/kachinco-lime-tools.json"
printf 'Linux bundle: %s\n' "$destination"
