param([Parameter(Mandatory = $true)][string]$Tag)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$info = Get-Content -LiteralPath (Join-Path $root "version.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$expected = "v$($info.version)"
if ($Tag -ne $expected) { throw "Release tag '$Tag' does not match $expected" }
[xml]$project = Get-Content -LiteralPath (Join-Path $root "Jellyfin.Plugin.Jellix\Jellyfin.Plugin.Jellix.csproj") -Raw -Encoding UTF8
$properties = $project.Project.PropertyGroup | Select-Object -First 1
if ([string]$properties.InformationalVersion -ne [string]$info.version -or
    [string]$properties.Version -ne [string]$info.versionFourPart -or
    [string]$properties.AssemblyVersion -ne [string]$info.versionFourPart -or
    [string]$properties.FileVersion -ne [string]$info.versionFourPart) { throw "The project version does not match version.json" }
$meta = Get-Content -LiteralPath (Join-Path $root "Jellyfin.Plugin.Jellix\meta.json") -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$meta.version -ne [string]$info.versionFourPart -or [string]$meta.targetAbi -ne [string]$info.targetAbi -or $meta.autoUpdate -ne $true) { throw "meta.json does not match version.json" }
Write-Output "Release metadata is consistent for $expected"
