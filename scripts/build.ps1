param(
    [string]$DotNet = "dotnet",
    [string]$Configuration = "Release",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-Content -LiteralPath (Join-Path $root "version.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$versionInfo.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "version.json contains an invalid version" }
$project = Join-Path $root "Jellyfin.Plugin.Jellix\Jellyfin.Plugin.Jellix.csproj"
$output = Join-Path $root "Jellyfin.Plugin.Jellix\bin\$Configuration\net9.0"
$dist = Join-Path $root "dist"

function Get-ContainedPath([string]$Parent, [string]$Child) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe staging path: $childFull" }
    return $childFull
}

$stage = Get-ContainedPath $dist (Join-Path $dist "stage-plugin")
$arguments = @("build", $project, "--configuration", $Configuration)
if ($NoRestore) { $arguments += "--no-restore" }
& $DotNet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$runtimeFiles = @(
    "Jellyfin.Plugin.Jellix.dll",
    "Discord.Net.Core.dll",
    "Discord.Net.Dave.dll",
    "Discord.Net.Rest.dll",
    "Discord.Net.WebSocket.dll",
    "System.Linq.AsyncEnumerable.dll"
)
foreach ($file in $runtimeFiles) {
    $source = Join-Path $output $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required runtime file missing: $source" }
    Copy-Item -LiteralPath $source -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $root "Jellyfin.Plugin.Jellix\meta.json") -Destination $stage
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $stage
Copy-Item -LiteralPath (Join-Path $root "NOTICE") -Destination $stage
Copy-Item -LiteralPath (Join-Path $root "THIRD-PARTY-NOTICES.txt") -Destination $stage

$archive = Join-Path $dist "Jellix_$version.zip"
$watchdogArchive = Join-Path $dist "JellixWatchdog_$version.zip"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
if (Test-Path -LiteralPath $watchdogArchive) { Remove-Item -LiteralPath $watchdogArchive -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -CompressionLevel Optimal
Remove-Item -LiteralPath $stage -Recurse -Force
$watchdogFiles = @(
    (Join-Path $root "watchdog\jellix-watchdog.ps1"),
    (Join-Path $root "watchdog\README.txt")
)
Compress-Archive -LiteralPath $watchdogFiles -DestinationPath $watchdogArchive -CompressionLevel Optimal
$checksums = @($archive, $watchdogArchive) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
}
Set-Content -LiteralPath (Join-Path $dist "SHA256SUMS.txt") -Value $checksums -Encoding UTF8
Write-Output "Created $archive"
Write-Output "Created $watchdogArchive"
