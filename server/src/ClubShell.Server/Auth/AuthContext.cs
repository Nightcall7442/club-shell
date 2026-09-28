using ClubShell.Server.Agents;

namespace ClubShell.Server.Auth;

/// <summary>Authentication modes of the contract (DESIGN §3.1, <c>cc/openapi/base.yaml</c> §2).</summary>
public enum AuthMode
{
    /// <summary>No credentials (<c>refresh</c>, <c>adminLogin</c>, the 404 fallback).</summary>
    None,

    /// <summary><c>X-Club-Key</c> (<c>register</c>).</summary>
    Club,

    /// <summary>Agent JWT + HMAC signature.</summary>
    Agent,

    /// <summary>Agent; <c>X-User-Token</c> only enriches the answer and an invalid one is ignored, never 401.</summary>
    AgentOptionalUser,

    /// <summary>Agent + mandatory <c>X-User-Token</c>.</summary>
    User,

    /// <summary><c>Authorization: Bearer</c> staff token or <c>ck_</c> key.</summary>
    Staff,
}

/// <summary>Endpoint metadata read by <see cref="AgentAuthMiddleware"/>; every <c>/api/v1</c> endpoint must declare one.</summary>
public sealed record AuthRequirement(AuthMode Mode);

/// <summary>Validated claims of an agent access token.</summary>
public sealed record AgentPrincipal(Guid PcId, Guid ClubId, string Hwid, int CredentialsVersion, DateTimeOffset ExpiresAt);

/// <summary>The PC a request comes from; set as a request feature by <see cref="AgentAuthMiddleware"/>.</summary>
public sealed record AgentContext(AgentPrincipal Principal, PcRow Pc);

/// <summary>The club whose enrollment key authenticated a <c>club</c>-mode request.</summary>
public sealed record ClubContext(Guid ClubId);
