using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Discord;
using Jellyfin.Plugin.Jellix.Integrations;
using Jellyfin.Plugin.Jellix.Models;
using Jellyfin.Plugin.Jellix.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Api;

[ApiController]
[Route("Jellix")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class JellixController : ControllerBase
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private static readonly Action<ILogger, string, Exception?> LogWarning = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4001, "ApiWarning"), "Jellix API warning: {Message}");
    private readonly JellixDatabase _database;
    private readonly AccountLinkService _links;
    private readonly PasswordTicketService _passwordTickets;
    private readonly OperationRateLimiter _rateLimiter;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly DiscordBotService _discord;
    private readonly MediaForgeBridgeClient _mediaForge;
    private readonly ILogger<JellixController> _logger;

    public JellixController(
        JellixDatabase database,
        AccountLinkService links,
        PasswordTicketService passwordTickets,
        OperationRateLimiter rateLimiter,
        IUserManager userManager,
        ISessionManager sessionManager,
        DiscordBotService discord,
        MediaForgeBridgeClient mediaForge,
        ILogger<JellixController> logger)
    {
        _database = database;
        _links = links;
        _passwordTickets = passwordTickets;
        _rateLimiter = rateLimiter;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _discord = discord;
        _mediaForge = mediaForge;
        _logger = logger;
    }

    [HttpGet("Status")]
    [Authorize]
    public IActionResult Status()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            botEnabled = config?.BotEnabled == true,
            tokenConfigured = Plugin.Instance?.Secrets.HasToken == true,
            selfLinkEnabled = config?.SelfLinkEnabled == true,
            passwordChangeEnabled = config?.PasswordChangeEnabled == true,
            language = config?.Language ?? "de",
        });
    }

    [HttpPost("LinkCode")]
    [Authorize]
    public async Task<IActionResult> CreateLinkCode(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!_rateLimiter.TryConsume(userId.ToString("N"), "link-code", 5, RateWindow))
        {
            return TooManyRequests();
        }

        try
        {
            var result = await _links.CreateCodeAsync(userId, cancellationToken).ConfigureAwait(false);
            await TryWriteAuditAsync("jellyfin-user", userId.ToString("N"), "link-code-created", "jellyfin-user", userId.ToString("N"), cancellationToken).ConfigureAwait(false);
            return Ok(new { code = result.Code, expiresUtc = result.ExpiresUtc });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    [HttpGet("Admin/Links")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<IReadOnlyList<UserLink>> ListLinks(CancellationToken cancellationToken)
        => _database.ListLinksAsync(cancellationToken);

    [HttpPost("Admin/Links")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> CreateLink([FromBody] AdminLinkRequest payload, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(payload.GuildId, out _) || !ulong.TryParse(payload.DiscordUserId, out _))
        {
            return BadRequest(new { error = "GuildId and DiscordUserId must be Discord snowflakes." });
        }

        if (_userManager.GetUserById(payload.JellyfinUserId) is null)
        {
            return NotFound(new { error = "Jellyfin user not found." });
        }

        try
        {
            await _database.LinkUserAsync(payload.GuildId, payload.DiscordUserId, payload.JellyfinUserId, "admin", cancellationToken).ConfigureAwait(false);
            await TryWriteAuditAsync("jellyfin-admin", CurrentUserId().ToString("N"), "account-linked", "discord-user", payload.DiscordUserId, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (SqliteException)
        {
            return Conflict(new { error = "One of these accounts is already linked." });
        }
    }

    [HttpDelete("Admin/Links/{guildId}/{discordUserId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> DeleteLink(string guildId, string discordUserId, CancellationToken cancellationToken)
    {
        await _database.UnlinkUserAsync(guildId, discordUserId, cancellationToken).ConfigureAwait(false);
        await TryWriteAuditAsync("jellyfin-admin", CurrentUserId().ToString("N"), "account-unlinked", "discord-user", discordUserId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpGet("Admin/Token")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult TokenStatus() => Ok(new { configured = Plugin.Instance?.Secrets.HasToken == true });

    [HttpPost("Admin/Token")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> UpdateToken([FromBody] UpdateTokenRequest payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.Token))
        {
            return BadRequest(new { error = "Discord token is required." });
        }

        Plugin.Instance?.Secrets.SetToken(payload.Token.Trim());
        await TryWriteAuditAsync("jellyfin-admin", CurrentUserId().ToString("N"), "discord-token-updated", "plugin", "jellix", cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("Admin/Token")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> DeleteToken(CancellationToken cancellationToken)
    {
        Plugin.Instance?.Secrets.ClearToken();
        await TryWriteAuditAsync("jellyfin-admin", CurrentUserId().ToString("N"), "discord-token-deleted", "plugin", "jellix", cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpGet("Admin/Audit")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<IReadOnlyList<AuditRecord>> Audit(int limit = 100, long beforeId = 0, CancellationToken cancellationToken = default)
        => _database.ListAuditAsync(limit, beforeId, cancellationToken);

    [HttpGet("Admin/Diagnostics")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Diagnostics(CancellationToken cancellationToken)
    {
        var mediaForgeAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(static value => value.GetName().Name?.Equals("Jellyfin.Plugin.MediaForge", StringComparison.Ordinal) == true);
        return Ok(new
        {
            discordReady = _discord.IsReady,
            mediaForgeInstalled = mediaForgeAssembly is not null,
            mediaForgeBridgeAvailable = _mediaForge.IsAvailable,
            mediaForgeVersion = mediaForgeAssembly?.GetName().Version?.ToString(3),
            pendingNotifications = await _database.GetPendingNotificationCountAsync(cancellationToken).ConfigureAwait(false),
            database = "ok",
            configurationIssues = _discord.ConfigurationIssues,
        });
    }

    [HttpGet("InjectionScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public IActionResult InjectionScript()
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return Embedded("Web.injection.js", "application/javascript");
    }

    [HttpGet("PasswordPage")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    public IActionResult PasswordPage()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; connect-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";
        return Embedded("Web.password-standalone.html", MediaTypeNames.Text.Html);
    }

    [HttpGet("PasswordScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public IActionResult PasswordScript()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return Embedded("Web.password-standalone.js", "application/javascript");
    }

    [HttpPost("Password")]
    [AllowAnonymous]
    public async Task<IActionResult> ChangePassword([FromBody] PasswordChangeRequest payload, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.PasswordChangeEnabled != true)
        {
            return NotFound();
        }

        var remote = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_rateLimiter.TryConsume(remote, "password-change", 10, RateWindow))
        {
            return TooManyRequests();
        }

        if (payload.NewPassword.Length is < 1 or > 256 || payload.NewPassword != payload.ConfirmPassword)
        {
            return BadRequest(new { error = "Passwords do not match or are invalid." });
        }

        var ticket = await _passwordTickets.GetAsync(payload.Token, cancellationToken).ConfigureAwait(false);
        if (ticket is null || !await _passwordTickets.ConsumeAsync(payload.Token, cancellationToken).ConfigureAwait(false))
        {
            return Unauthorized(new { error = "This password link is invalid or expired." });
        }

        try
        {
            await _userManager.ChangePassword(ticket.JellyfinUserId, payload.NewPassword).ConfigureAwait(false);
            if (Plugin.Instance?.Configuration.RevokeSessionsAfterPasswordChange == true)
            {
                try
                {
                    await _sessionManager.RevokeUserTokens(ticket.JellyfinUserId, string.Empty).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogWarning(_logger, "sessions could not be revoked after a successful password change", exception);
                }
            }

            try
            {
                await _database.WriteAuditAsync("discord-user", ticket.DiscordUserId, "password-changed", "jellyfin-user", ticket.JellyfinUserId.ToString("N"), true, string.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogWarning(_logger, "a successful password change could not be audited", exception);
            }

            return Ok(new { success = true });
        }
        catch (Exception)
        {
            try
            {
                await _database.WriteAuditAsync("discord-user", ticket.DiscordUserId, "password-changed", "jellyfin-user", ticket.JellyfinUserId.ToString("N"), false, "Password change failed.", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception auditException)
            {
                LogWarning(_logger, "a failed password change could not be audited", auditException);
            }

            LogWarning(_logger, "password change failed", null);
            throw;
        }
    }

    private Guid CurrentUserId()
    {
        var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("Jellyfin-UserId")?.Value
            ?? User.FindFirst("UserId")?.Value;
        return Guid.TryParse(id, out var parsed) ? parsed : throw new UnauthorizedAccessException("Jellyfin user identity unavailable.");
    }

    private IActionResult Embedded(string suffix, string contentType)
    {
        var resourceName = $"{typeof(Plugin).Namespace}.{suffix}";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        return stream is null ? NotFound() : File(stream, contentType);
    }

    private async Task TryWriteAuditAsync(string actorType, string actorId, string action, string targetType, string targetId, CancellationToken cancellationToken)
    {
        try
        {
            await _database.WriteAuditAsync(actorType, actorId, action, targetType, targetId, true, string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarning(_logger, "an audit entry could not be written", exception);
        }
    }

    private static ObjectResult TooManyRequests()
        => new(new { error = "Too many requests. Please wait." }) { StatusCode = StatusCodes.Status429TooManyRequests };
}

public sealed class AdminLinkRequest
{
    [Required, MaxLength(32)]
    public string GuildId { get; set; } = string.Empty;

    [Required, MaxLength(32)]
    public string DiscordUserId { get; set; } = string.Empty;

    public Guid JellyfinUserId { get; set; }
}

public sealed class UpdateTokenRequest
{
    [Required, MaxLength(512)]
    public string Token { get; set; } = string.Empty;
}

public sealed class PasswordChangeRequest
{
    [Required, MaxLength(128)]
    public string Token { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string NewPassword { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string ConfirmPassword { get; set; } = string.Empty;
}
