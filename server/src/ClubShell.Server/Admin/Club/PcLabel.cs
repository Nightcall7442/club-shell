using System.Text.Json;
using ClubShell.Contracts.Pcs;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Admin;

/// <summary>
/// <c>settings.branding.pcLabel</c> (D-95, beyond the contract): how the PC's own name or number shows on the player
/// screen. Unset — the name and zone on every screen, as before.
/// </summary>
public static class PcLabel
{
    public static readonly string[] Formats = ["nameZone", "name", "number"];

    /// <summary>The owner's patch, checked: <c>400 validation</c> on <c>branding.pcLabel.*</c>.</summary>
    public static void Validate(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("branding.pcLabel", "schema", "JSON object expected");
        }

        if (e.TryGetProperty("format", out var f) && f.ValueKind != JsonValueKind.Null
            && (f.ValueKind != JsonValueKind.String || !Formats.Contains(f.GetString())))
        {
            throw ApiException.Validation("branding.pcLabel.format", "enum");
        }

        foreach (var name in new[] { "large", "lock", "idle", "topBar" })
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                throw ApiException.Validation("branding.pcLabel." + name, "type");
            }
        }
    }

    /// <summary>What the PCs get, or null when the owner never set it (or it is unreadable).</summary>
    public static ShellPcLabel? ToShell(JsonElement branding)
    {
        if (branding.ValueKind != JsonValueKind.Object || !branding.TryGetProperty("pcLabel", out var e) || e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var format = e.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String && Formats.Contains(f.GetString())
            ? f.GetString()!
            : "nameZone";
        bool Flag(string name, bool fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
        return new ShellPcLabel(format, Flag("large", false), Flag("lock", true), Flag("idle", true), Flag("topBar", true));
    }
}
