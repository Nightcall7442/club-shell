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

    /// <summary><c>Authorization: Bearer</c> platform administrator key (<c>Platform:AdminKey</c>, beyond the contract).</summary>
    Platform,
}

/// <summary>
/// Endpoint metadata read by <see cref="AgentAuthMiddleware"/>; every <c>/api/v1</c> endpoint must declare one.
/// <paramref name="OwnerOnly"/>: staff mode for the owner only (contract <c>x-roles: [owner]</c>), else <c>403 ownerOnly</c>.
/// </summary>
public sealed record AuthRequirement(AuthMode Mode, bool OwnerOnly = false);

/// <summary>
/// The staff member of a <c>staff</c>-mode request (DESIGN §3.5): a PIN token's staff row, or for the club API key
/// <c>ck_…</c> the synthetic owner "API key" (<see cref="StaffId"/> null). <see cref="WireId"/> is <c>AdminStaffMember.id</c>.
/// </summary>
public sealed record StaffContext(Guid? StaffId, string Name, string Role, Guid ClubId, Guid NetworkId)
{
    public bool IsOwner => Role == "owner";

    public string WireId => StaffId?.ToString() ?? "apiKey";
}

/// <summary>Validated claims of an agent access token.</summary>
public sealed record AgentPrincipal(Guid PcId, Guid ClubId, string Hwid, int CredentialsVersion, DateTimeOffset ExpiresAt);

/// <summary>The PC a request comes from; set as a request feature by <see cref="AgentAuthMiddleware"/>.</summary>
public sealed record AgentContext(AgentPrincipal Principal, PcRow Pc);

/// <summary>The club whose enrollment key authenticated a <c>club</c>-mode request.</summary>
public sealed record ClubContext(Guid ClubId);

/// <summary>The player of the request: <c>X-User-Token</c> valid and bound to the PC of the agent token (DESIGN §3.4).</summary>
public sealed record UserContext(Guid UserId);

/// <summary>
/// Why <c>X-User-Token</c> was not accepted (<c>invalid</c> | <c>expired</c> | <c>boundElsewhere</c> | <c>revoked</c>): set in
/// the agent-optional-user mode, where the request goes on without a player (offline replay of <c>POST /sessions</c>).
/// </summary>
public sealed record UserTokenProblem(string Problem);
