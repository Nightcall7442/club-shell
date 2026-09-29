using ClubShell.Server.Auth;
using YamlDotNet.Serialization;

namespace ClubShell.Server.Infrastructure;

/// <summary>One operation of the vendored contract (<c>server/contracts/openapi.yaml</c>).</summary>
public sealed record ContractOperation(string Method, string Path, string OperationId, string ServerStatus, AuthMode Auth, bool OwnerOnly = false);

/// <summary>
/// The 501 table (DESIGN §1): every contract operation this server does not implement answers
/// <c>501 notImplemented</c> after authentication in the operation's mode, never 404 — the route exists.
/// Ported from club-server <c>Api/ContractStatus.cs</c> (1c68cc8) with the contract's error code and auth modes.
/// </summary>
public static class ContractStatus
{
    /// <summary>
    /// operationIds this server implements; the 501 table and <c>NotImplementedTests</c> both read this list. Slices add
    /// theirs here (S2 also adds <c>getBalance</c>, S3 <c>reportAntiCheat</c>, which the contract still marks notImplemented).
    /// </summary>
    public static readonly IReadOnlySet<string> Implemented = new HashSet<string>(
        [.. Agents.AgentEndpoints.Operations, .. Auth.PlayerAuthEndpoints.Operations, .. Users.UserEndpoints.Operations,
         .. Sessions.SessionEndpoints.Operations, .. Wallet.WalletEndpoints.Operations, .. Games.GameEndpoints.Operations,
         .. Updates.UpdateEndpoints.Operations, .. Agents.PcEndpoints.Operations, .. Admin.StaffEndpoints.Operations,
         .. Admin.CounterEndpoints.Operations, .. Admin.ShiftEndpoints.Operations, .. Admin.PcAdminEndpoints.Operations,
         .. Admin.StaffAdminEndpoints.Operations, .. Admin.ClientEndpoints.Operations, .. Admin.PromoEndpoints.Operations,
         .. Admin.TariffEndpoints.Operations, .. Admin.StockEndpoints.Operations, .. Admin.ClubSettingsEndpoints.Operations,
         .. Admin.CatalogAdminEndpoints.Operations, .. Admin.HealthEndpoints.Operations, .. Admin.ControlEndpoints.Operations,
         .. Admin.ReportsEndpoints.Operations],
        StringComparer.Ordinal);

    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch"];

    public static IReadOnlyList<ContractOperation> Load(string openApiPath)
    {
        var root = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(File.ReadAllText(openApiPath));
        var operations = new List<ContractOperation>();
        foreach (var (path, item) in (Dictionary<object, object>)root["paths"])
        {
            if (item is not Dictionary<object, object> pathItem)
            {
                continue;
            }

            foreach (var method in Methods)
            {
                if (pathItem.TryGetValue(method, out var value) && value is Dictionary<object, object> operation)
                {
                    operations.Add(new ContractOperation(
                        method.ToUpperInvariant(),
                        (string)path,
                        operation.TryGetValue("operationId", out var id) ? (string)id : "",
                        operation.TryGetValue("x-server-status", out var status) ? (string)status : "notImplemented",
                        ModeOf(operation),
                        // x-roles [owner]: a cashier gets 403 ownerOnly before the 501, as the implementation will answer.
                        operation.TryGetValue("x-roles", out var roles) && roles is List<object> r && r.Cast<string>().SequenceEqual(["owner"])));
                }
            }
        }

        return operations;
    }

    /// <summary>Maps a 501 endpoint for every contract operation that is not in <paramref name="implemented"/>.</summary>
    public static void MapNotImplemented(this IEndpointRouteBuilder app, IEnumerable<ContractOperation> operations, IReadOnlySet<string> implemented)
    {
        foreach (var op in operations.Where(o => !implemented.Contains(o.OperationId)))
        {
            app.MapMethods("/api/v1" + op.Path, [op.Method], (RequestDelegate)(_ => throw ApiException.NotImplemented(op.OperationId)))
                .WithMetadata(new AuthRequirement(op.Auth, op.OwnerOnly));
        }
    }

    /// <summary>
    /// Contract <c>security</c> to auth mode (DESIGN §3.1). <c>publishToken</c> alone is the release publisher's token,
    /// which this server does not issue: such operations are 501 without authentication. So is an operation that
    /// declares no <c>401</c> (<c>adminLogout</c>: "always 200", its 501 must not become a 401).
    /// </summary>
    private static AuthMode ModeOf(Dictionary<object, object> operation)
    {
        if (operation.TryGetValue("responses", out var responses) && responses is Dictionary<object, object> declared && !declared.ContainsKey("401"))
        {
            return AuthMode.None;
        }

        var security = operation.TryGetValue("security", out var value) ? value as List<object> : null;
        var alternatives = (security ?? []).OfType<Dictionary<object, object>>().Select(s => s.Keys.Cast<string>().ToHashSet()).ToList();
        bool Has(Func<HashSet<string>, bool> match) => alternatives.Any(match);

        if (Has(s => s.Contains("clubKey")))
        {
            return AuthMode.Club;
        }

        if (Has(s => s.Contains("staffBearer")))
        {
            return AuthMode.Staff;
        }

        if (Has(s => s.SetEquals(["agentBearer", "userToken"])))
        {
            return Has(s => s.SetEquals(["agentBearer"])) ? AuthMode.AgentOptionalUser : AuthMode.User;
        }

        return Has(s => s.Contains("agentBearer")) ? AuthMode.Agent : AuthMode.None;
    }
}
