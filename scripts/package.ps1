[CmdletBinding()]
param([string]$SourceCommit)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'package.json') -Raw | ConvertFrom-Json).version
$assemblyVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexEnhance.csproj') -Raw)).Project.PropertyGroup.Version
if ($assemblyVersion -ne $version) { throw 'Node and application versions differ.' }
$releaseRoot = Join-Path $projectRoot ('artifacts\releases\v' + $version)
$packageName = 'CodexEnhance-v' + $version + '-win-x64'
$stage = Join-Path $releaseRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
$appRoot = Join-Path $stage $packageName
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $appRoot
New-Item -ItemType Directory -Path (Join-Path $appRoot 'scripts') -Force | Out-Null
$utf8Bom = New-Object Text.UTF8Encoding $true
foreach ($name in @('start-codex.ps1', 'install-user.ps1')) {
    [IO.File]::WriteAllText((Join-Path $appRoot ('scripts\' + $name)), [IO.File]::ReadAllText((Join-Path $PSScriptRoot $name)), $utf8Bom)
}
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'packaging') -File | Copy-Item -Destination $appRoot
if ($SourceCommit -and $SourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'SourceCommit must be a full Git commit SHA.' }
$manifest = [ordered]@{ product = 'Codex Enhance'; version = $version; architecture = 'win-x64'; sourceCommit = $SourceCommit; node = (& (Join-Path $appRoot 'runtime\node.exe') --version); builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $appRoot 'version.json') -Encoding UTF8
$zip = Join-Path $releaseRoot ($packageName + '.zip')
if (Test-Path -LiteralPath $zip) { throw 'Release archive already exists; retain it or move it aside before repackaging.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), "$sha  $packageName.zip`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Version = $version; PackageDirectory = $appRoot; Archive = $zip; SHA256 = $sha; Bytes = (Get-Item -LiteralPath $zip).Length }
