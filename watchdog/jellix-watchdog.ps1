$ErrorActionPreference = "Stop"

$serverUrl = [Environment]::GetEnvironmentVariable("JELLIX_JELLYFIN_URL")
$webhookUrl = [Environment]::GetEnvironmentVariable("JELLIX_DISCORD_WEBHOOK")
$intervalText = [Environment]::GetEnvironmentVariable("JELLIX_INTERVAL_SECONDS")
$server = $null
if (-not [Uri]::TryCreate($serverUrl, [UriKind]::Absolute, [ref]$server) -or $server.Scheme -notin @("http", "https") -or -not [string]::IsNullOrEmpty($server.Query) -or -not [string]::IsNullOrEmpty($server.Fragment) -or -not [string]::IsNullOrEmpty($server.UserInfo)) { throw "JELLIX_JELLYFIN_URL is invalid." }
$webhook = $null
if (-not [Uri]::TryCreate($webhookUrl, [UriKind]::Absolute, [ref]$webhook) -or $webhook.Scheme -ne "https" -or $webhook.Host -notin @("discord.com", "discordapp.com")) { throw "JELLIX_DISCORD_WEBHOOK must be an HTTPS Discord webhook URL." }
$interval = 60
if ([int]::TryParse($intervalText, [ref]$interval)) { $interval = [Math]::Clamp($interval, 15, 3600) } else { $interval = 60 }
$serverBuilder = [UriBuilder]::new($server)
$serverBuilder.Path = $server.AbsolutePath.TrimEnd('/') + "/System/Info/Public"
$statusUrl = $serverBuilder.Uri.AbsoluteUri
$configuredStatePath = [Environment]::GetEnvironmentVariable("JELLIX_STATE_PATH")
$statePath = if ([string]::IsNullOrWhiteSpace($configuredStatePath)) { Join-Path $PSScriptRoot "watchdog-state.txt" } else { [IO.Path]::GetFullPath($configuredStatePath) }

function Send-DiscordAlert([bool]$Online) {
    $payload = if ($Online) {
        @{ embeds = @(@{ title = "✅ Jellix"; description = "Jellyfin ist wieder erreichbar."; color = 3066993 }) }
    } else {
        @{ embeds = @(@{ title = "⚠️ Jellix"; description = "Jellyfin ist nicht erreichbar."; color = 15158332 }) }
    }
    Invoke-RestMethod -Method Post -Uri $webhookUrl -ContentType "application/json" -Body ($payload | ConvertTo-Json -Depth 5 -Compress) | Out-Null
}

function Save-State([string]$State) {
    try {
        $stateDirectory = Split-Path -Parent $statePath
        if (-not [string]::IsNullOrWhiteSpace($stateDirectory) -and -not (Test-Path -LiteralPath $stateDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
        }
        Set-Content -LiteralPath $statePath -Value $State -Encoding UTF8
    } catch {
        Write-Warning "The watchdog state could not be persisted; in-memory state remains active."
    }
}

$previous = try {
    if (Test-Path -LiteralPath $statePath -PathType Leaf) { (Get-Content -LiteralPath $statePath -Raw).Trim() } else { "unknown" }
} catch {
    Write-Warning "The watchdog state could not be read; starting with an unknown state."
    "unknown"
}
if ($previous -notin @("online", "offline")) { $previous = "unknown" }

while ($true) {
    $online = $false
    try {
        $response = Invoke-WebRequest -Uri $statusUrl -Method Get -TimeoutSec 10 -MaximumRedirection 0 -SkipHttpErrorCheck
        $online = [int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 300
    } catch {
        $online = $false
    }
    $current = if ($online) { "online" } else { "offline" }
    if ($previous -eq "unknown" -and $online) {
        $previous = $current
        Save-State $current
    } elseif ($current -ne $previous) {
        try {
            Send-DiscordAlert $online
            $previous = $current
            Save-State $current
        } catch {
            Write-Warning "The Discord alert could not be delivered; it will be retried."
        }
    }
    Start-Sleep -Seconds $interval
}
