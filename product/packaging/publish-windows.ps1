[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/windows')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'portable-package.ps1')
$destination = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory)) }
$bundle = Join-Path $destination 'FLAMORIS-Kachinco-win-x64'
foreach ($path in @($bundle, "$bundle.zip", "$bundle.inventory.json")) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force $bundle | Out-Null
& dotnet publish (Join-Path $repositoryRoot 'product/Kachinco.App/Kachinco.App.csproj') -c Release -r win-x64 --self-contained true -o $bundle /p:DebugType=None /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Editor publish failed.' }
& dotnet publish (Join-Path $repositoryRoot 'product/Kachinco.Mcp/Kachinco.Mcp.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $bundle 'mcp') /p:DebugType=None /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'MCP bridge publish failed.' }
Get-ChildItem -LiteralPath $bundle -Filter '*.pdb' -File -Recurse |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
if (-not (Test-Path (Join-Path $bundle 'Flamoris.Mcp.Wpf.dll'))) { throw 'Shared MCP UI runtime missing.' }
if (-not (Test-Path (Join-Path $bundle 'Kachinco.Native.Runtime.dll'))) { throw 'Native runtime missing from portable package.' }
$bridge = Join-Path $bundle 'mcp/Flamoris.Mcp.Bridge.exe'
$core = Join-Path $bundle 'mcp/Flamoris.Mcp.Core.dll'
if (-not (Test-Path $bridge) -or -not (Test-Path $core)) { throw 'Matching Core bridge runtime missing.' }
foreach ($engine in @('Kachinco.Core.dll', 'Kachinco.Native.dll', 'Kachinco.Native.Runtime.dll', 'Kachinco.Infrastructure.dll')) {
    if (Test-Path (Join-Path $bundle "mcp/$engine")) { throw "Editor authority entered bridge package: $engine" }
}
foreach ($required in @('Kachinco.App.exe', 'Kachinco.Native.dll', 'recipe-worker.py', 'Flamoris.Mcp.Core.dll', 'Flamoris.Logging.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'mcp/coreclr.dll', 'mcp/hostfxr.dll', 'mcp/hostpolicy.dll', 'mcp/Flamoris.Logging.dll')) {
    if (-not (Test-Path (Join-Path $bundle $required))) { throw "Required native shell dependency missing: $required" }
}
$licenses = Join-Path $bundle 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $repositoryRoot 'LICENSE') (Join-Path $licenses 'Kachinco-LICENSE')
Copy-Item (Join-Path $repositoryRoot 'product/native/third_party/nlohmann/LICENSE.MIT') (Join-Path $licenses 'nlohmann-json-LICENSE.MIT')
$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $source = Join-Path $dotnetRoot $notice
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing .NET notice: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenses "DOTNET-$notice")
}
$revision = & git -C $repositoryRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Unable to record package source revision.' }
@("commit=$revision", 'platform=win-x64', 'native-abi=1', 'required-capabilities=2047', 'ticks-per-second=35280000') |
    Set-Content (Join-Path $bundle 'BUILD-INFO.txt') -Encoding utf8

Copy-Item (Join-Path $repositoryRoot 'staging/windows-production.md') (Join-Path $bundle 'START-HERE.md')
Copy-Item (Join-Path $repositoryRoot 'staging/windows-native-phase2.md') (Join-Path $bundle 'NATIVE-ACCEPTANCE.md')
foreach ($required in @('BUILD-INFO.txt', 'START-HERE.md', 'NATIVE-ACCEPTANCE.md', 'licenses/Kachinco-LICENSE', 'licenses/nlohmann-json-LICENSE.MIT')) {
    if (-not (Test-Path -LiteralPath (Join-Path $bundle $required) -PathType Leaf)) { throw "Missing notice/provenance: $required" }
}
Complete-PortablePackage -PackageRoot $bundle -RepositoryRoot $repositoryRoot -AllowedSourcePaths @('recipe-worker.py')
