param(
    [Parameter(Mandatory = $true)][string]$RepositorySlug,
    [string]$ReleaseTag
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$info = Get-Content -LiteralPath (Join-Path $root "version.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$info.version
if ($RepositorySlug -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "RepositorySlug must be owner/repository" }
if ([string]::IsNullOrWhiteSpace($ReleaseTag)) { $ReleaseTag = "v$version" }
if ($ReleaseTag -ne "v$version") { throw "Release tag does not match version.json" }
$name = "Jellix_$version.zip"
$archive = Join-Path $root "dist\$name"
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Release archive not found: $archive" }
$meta = Get-Content -LiteralPath (Join-Path $root "Jellyfin.Plugin.Jellix\meta.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest = @([ordered]@{
    guid = "bea64f51-00f3-4535-8fd3-88bcd2785f24"
    name = "Jellix"
    description = "Discord account tools, playback statistics, notifications and optional MediaForge requests for Jellyfin."
    overview = "Discord companion for Jellyfin"
    owner = "Eric (DasEric)"
    category = "General"
    versions = @([ordered]@{
        version = [string]$info.versionFourPart
        changelog = [string]$meta.changelog
        targetAbi = [string]$info.targetAbi
        sourceUrl = "https://github.com/$RepositorySlug/releases/download/$ReleaseTag/$name"
        checksum = (Get-FileHash -LiteralPath $archive -Algorithm MD5).Hash.ToUpperInvariant()
        timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    })
})
$directory = Join-Path $root "repository"
New-Item -ItemType Directory -Force -Path $directory | Out-Null
[IO.File]::WriteAllText((Join-Path $directory "manifest.json"), (ConvertTo-Json $manifest -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$owner, $repo = $RepositorySlug.Split('/')
$pagesPath = if ($repo -ieq "$owner.github.io") { "" } else { "/$repo" }
Write-Output "Repository URL: https://$owner.github.io$pagesPath/manifest.json"
