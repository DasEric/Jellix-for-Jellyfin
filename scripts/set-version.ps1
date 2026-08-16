param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Changelog = "See the GitHub release notes."
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$four = "$Version.0"
$versionPath = Join-Path $root "version.json"
$info = Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8 | ConvertFrom-Json
$info.version = $Version
$info.versionFourPart = $four
[IO.File]::WriteAllText($versionPath, (ConvertTo-Json $info -Depth 4) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$projectPath = Join-Path $root "Jellyfin.Plugin.Jellix\Jellyfin.Plugin.Jellix.csproj"
$project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$project = [regex]::Replace($project, '<(AssemblyVersion|FileVersion|Version)>[^<]+</\1>', { param($match) "<$($match.Groups[1].Value)>$four</$($match.Groups[1].Value)>" })
$project = [regex]::Replace($project, '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>")
[IO.File]::WriteAllText($projectPath, $project, [Text.UTF8Encoding]::new($false))
$metaPath = Join-Path $root "Jellyfin.Plugin.Jellix\meta.json"
$meta = Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json
$meta.version = $four
$meta.changelog = $Changelog
$meta.timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
[IO.File]::WriteAllText($metaPath, (ConvertTo-Json $meta -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Version set to $Version. Commit, tag v$Version, and push the tag."
