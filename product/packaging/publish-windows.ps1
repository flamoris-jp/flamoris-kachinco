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
$bridge = Join-Path $bundle 'mcp/Flamoris.Mcp.Bridge.exe'
$core = Join-Path $bundle 'mcp/Flamoris.Mcp.Core.dll'
if (-not (Test-Path $bridge) -or -not (Test-Path $core)) { throw 'Matching Core bridge runtime missing.' }
if (Test-Path (Join-Path $bundle 'mcp/Kachinco.Core.dll')) { throw 'Editor authority entered bridge package.' }

Copy-Item (Join-Path $repositoryRoot 'staging/windows-production.md') (Join-Path $bundle 'START-HERE.md')
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath (Join-Path $destination 'Kachinco-win-x64.zip') -Force
