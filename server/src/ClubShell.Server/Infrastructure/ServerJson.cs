using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ClubShell.Contracts.Serialization;

namespace ClubShell.Server.Infrastructure;

/// <summary>
/// JSON of the agent/player surface (DESIGN §2.4): <see cref="JsonDefaults.Options"/> byte for byte (camelCase, string
/// enums with <c>unknown</c> fallback, <c>.fffZ</c>, nulls omitted, unmapped members skipped, strict numbers). Contract
/// DTOs resolve through the source-generated <c>ContractsJsonContext</c>; server-only shapes (error details, DTOs that
/// exist only here) fall back to reflection.
/// </summary>
public static class ServerJson
{
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>Contract time format: <c>2026-09-21T10:15:30.123Z</c>.</summary>
    public static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Copies <see cref="Options"/> into the minimal-API options (they are created by the framework).</summary>
    public static void Apply(JsonSerializerOptions target)
    {
        target.PropertyNamingPolicy = Options.PropertyNamingPolicy;
        target.PropertyNameCaseInsensitive = Options.PropertyNameCaseInsensitive;
        target.DefaultIgnoreCondition = Options.DefaultIgnoreCondition;
        target.NumberHandling = Options.NumberHandling;
        target.UnmappedMemberHandling = Options.UnmappedMemberHandling;
        target.ReadCommentHandling = Options.ReadCommentHandling;
        target.AllowTrailingCommas = Options.AllowTrailingCommas;
        target.Encoder = Options.Encoder;
        target.MaxDepth = Options.MaxDepth;
        target.TypeInfoResolver = Options.TypeInfoResolver;
        target.Converters.Clear();
        foreach (var converter in Options.Converters)
        {
            target.Converters.Add(converter);
        }
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(JsonDefaults.Context, new DefaultJsonTypeInfoResolver()),
        };
        options.MakeReadOnly();
        return options;
    }
}
