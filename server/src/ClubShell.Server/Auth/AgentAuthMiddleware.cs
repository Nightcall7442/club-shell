using ClubShell.Contracts.Errors;
using ClubShell.Server.Agents;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Auth;

/// <summary>
/// Authentication by the endpoint's <see cref="AuthRequirement"/> (DESIGN §3.1), before the handler and before the
/// 501 of unimplemented operations. Agent mode: RS256 JWT, PC not deleted and <c>cv</c> current, HMAC signature
/// (§3.3); repeats of a signature tuple are only logged (D-4). Port of club-server <c>AgentAuthMiddleware</c>.
/// <para>
/// User mode: <c>X-User-Token</c> must be a live player token bound to the PC of the agent token, else
/// <c>401 userToken</c> with <c>problem</c> (<see cref="UserTokens.ValidateAsync"/>). Agent-optional-user mode validates
/// the same way but never fails: a valid token sets <see cref="UserContext"/>, a bad one only <see cref="UserTokenProblem"/>.
/// S0 stub left for S4: <b>staff</b> checks only that a Bearer token is present (S4 moves staff to <c>StaffAuthMiddleware</c>).
/// </para>
/// </summary>
public sealed class AgentAuthMiddleware(
    RequestDelegate next,
    TokenService tokens,
    UserTokens users,
    PcRepository pcs,
    ClubRepository clubs,
    ReplayLog replays,
    AuthOptions options,
    TimeProvider clock,
    ILogger<AgentAuthMiddleware> logger)
{
    public const string ClubKeyHeader = "X-Club-Key";
    public const string UserTokenHeader = "X-User-Token";

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var requirement = endpoint?.Metadata.GetMetadata<AuthRequirement>();
        if (requirement is null)
        {
            // Fail closed: an /api/v1 endpoint that forgot to declare its mode must not become anonymous. Routing ignores
            // case, so this check does too (/API/V1/x reaches the same endpoint).
            if (endpoint is not null && context.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Endpoint '{endpoint.DisplayName}' declares no AuthRequirement");
            }

            await next(context);
            return;
        }

        switch (requirement.Mode)
        {
            case AuthMode.Club:
                var club = await clubs.FindByEnrollmentKeyAsync(context.Request.Headers[ClubKeyHeader].ToString())
                    ?? throw ApiException.Unauthorized("clubKey", "Missing or unknown X-Club-Key");
                if (club.Disabled)
                {
                    throw new ApiException(StatusCodes.Status403Forbidden, ErrorCode.Forbidden, "Club is disabled", new { reason = "clubDisabled" });
                }

                context.Features.Set(new ClubContext(club.Id));
                break;
            case AuthMode.Agent or AuthMode.AgentOptionalUser or AuthMode.User:
                var agent = await AuthenticateAgentAsync(context);
                context.Features.Set(agent);
                if (requirement.Mode == AuthMode.Agent)
                {
                    break;
                }

                var token = context.Request.Headers[UserTokenHeader].ToString();
                var (user, problem) = token.Length == 0 ? (null, "invalid") : await users.ValidateAsync(token, agent.Pc.Id);
                if (user is not null)
                {
                    context.Features.Set(user);
                }
                else if (requirement.Mode == AuthMode.User)
                {
                    throw ApiException.Unauthorized("userToken", "Missing or invalid X-User-Token", problem);
                }
                else
                {
                    context.Features.Set(new UserTokenProblem(problem!));
                }

                break;
            case AuthMode.Staff:
                _ = BearerToken(context) ?? throw ApiException.Unauthorized("missing", "Missing bearer token");
                break;
        }

        await next(context);
    }

    public static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7 ? header[7..].Trim() : null;
    }

    /// <summary>
    /// The access token alone (REST agent modes and the <c>/ws/agent</c> handshake): valid RS256 JWT, PC not deleted,
    /// <c>cv</c> current. Throws <c>401</c> <c>missing</c> | <c>expired</c> | <c>invalid</c> | <c>revoked</c>.
    /// </summary>
    public static async Task<AgentContext> AuthenticateTokenAsync(string? token, TokenService tokens, PcRepository pcs)
    {
        if (string.IsNullOrEmpty(token))
        {
            throw ApiException.Unauthorized("missing", "Missing bearer token");
        }

        var (principal, reason) = await tokens.ValidateAsync(token);
        if (principal is null)
        {
            throw ApiException.Unauthorized(reason!, reason == "expired" ? "Access token expired" : "Invalid access token");
        }

        var pc = await pcs.FindAsync(principal.PcId);
        if (pc is null || pc.DeletedAt is not null || pc.ClubId != principal.ClubId
            || pc.CredentialsVersion != principal.CredentialsVersion || pc.SigningSecret is null)
        {
            // Deleted PC or revoked credentials: the agent refreshes, then re-registers.
            throw ApiException.Unauthorized("revoked", "Access token revoked");
        }

        return new AgentContext(principal, pc);
    }

    private async Task<AgentContext> AuthenticateAgentAsync(HttpContext context)
    {
        var agent = await AuthenticateTokenAsync(BearerToken(context), tokens, pcs);
        var pc = agent.Pc;
        string timestamp, signature;
        try
        {
            (timestamp, signature) = await RequestSignature.VerifyAsync(
                context, pc.SigningSecret!, TimeSpan.FromSeconds(options.SignatureWindowSec), clock.GetUtcNow());
        }
        catch (ApiException ex)
        {
            // Security event (DESIGN §3.7): a valid JWT with a bad signature may be a stolen token. Never log the headers.
            logger.LogWarning("Request signature rejected ({Problem}) for {Method} {Path} from PC {PcId}", ex.Message, context.Request.Method, context.Request.Path, pc.Id);
            throw;
        }

        var method = context.Request.Method;
        if (replays.Seen(pc.Id, timestamp, signature)
            && (HttpMethods.IsPost(method) || HttpMethods.IsPatch(method))
            && !context.Request.Headers.ContainsKey(IdempotencyStore.KeyHeader))
        {
            logger.LogInformation("Repeated signature tuple for {Method} {Path} from PC {PcId}", method, context.Request.Path, pc.Id);
        }

        return agent;
    }
}
