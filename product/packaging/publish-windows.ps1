param([string]$OutputDirectory = 'artifacts/windows')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$destination = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$bundle = Join-Path $destination 'Kachinco-win-x64'
if (Test-Path $bundle) { throw "Choose a fresh output directory: $bundle already exists." }
New-Item -ItemType Directory -Force $bundle | Out-Null
& dotnet publish (Join-Path $repositoryRoot 'product/Kachinco.App/Kachinco.App.csproj') -c Release -r win-x64 --self-contained true -o $bundle
if ($LASTEXITCODE -ne 0) { throw 'Editor publish failed.' }
& dotnet publish (Join-Path $repositoryRoot 'product/Kachinco.Mcp/Kachinco.Mcp.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $bundle 'mcp')
if ($LASTEXITCODE -ne 0) { throw 'MCP bridge publish failed.' }
if (-not (Test-Path (Join-Path $bundle 'Flamoris.Mcp.Wpf.dll'))) { throw 'Shared MCP UI runtime missing.' }
if (-not (Test-Path (Join-Path $bundle 'Kachinco.Native.Runtime.dll'))) { throw 'Native runtime missing from portable package.' }
$bridge = Join-Path $bundle 'mcp/Flamoris.Mcp.Bridge.exe'
$core = Join-Path $bundle 'mcp/Flamoris.Mcp.Core.dll'
if (-not (Test-Path $bridge) -or -not (Test-Path $core)) { throw 'Matching Core bridge runtime missing.' }
foreach ($engine in @('Kachinco.Core.dll', 'Kachinco.Native.dll', 'Kachinco.Native.Runtime.dll', 'Kachinco.Infrastructure.dll')) {
    if (Test-Path (Join-Path $bundle "mcp/$engine")) { throw "Editor authority entered bridge package: $engine" }
}
foreach ($required in @('Kachinco.Native.dll', 'recipe-worker.py')) {
    if (-not (Test-Path (Join-Path $bundle $required))) { throw "Required native shell dependency missing: $required" }
}
$licenses = Join-Path $bundle 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $repositoryRoot 'LICENSE') (Join-Path $licenses 'Kachinco-LICENSE')
Copy-Item (Join-Path $repositoryRoot 'product/native/third_party/nlohmann/LICENSE.MIT') (Join-Path $licenses 'nlohmann-json-LICENSE.MIT')
$revision = & git -C $repositoryRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Unable to record package source revision.' }
@("commit=$revision", 'platform=win-x64', 'native-abi=1', 'required-capabilities=2047', 'ticks-per-second=35280000') |
    Set-Content (Join-Path $bundle 'BUILD-INFO.txt') -Encoding utf8

Copy-Item (Join-Path $repositoryRoot 'staging/windows-production.md') (Join-Path $bundle 'START-HERE.md')
Copy-Item (Join-Path $repositoryRoot 'staging/windows-native-phase2.md') (Join-Path $bundle 'NATIVE-ACCEPTANCE.md')
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath (Join-Path $destination 'Kachinco-win-x64.zip') -Force
