using System.Text.Json;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Admin;

/// <summary>
/// Field checks of the console's requests with the reasons of the mock they replace (<c>db.ts str/optStr/int</c>): an empty
/// required text is <c>required</c>, too long <c>max</c>; a number below or above its bounds <c>min</c>/<c>max</c>. Types
/// are already checked by <see cref="Api.Read{T}"/> (<c>format</c>).
/// </summary>
public static class AdminInput
{
    /// <summary>
    /// Whether the body names <paramref name="name"/> at all: a PATCH tells "leave as is" (absent) from "clear" (<c>null</c>),
    /// which the record binder cannot. The case is ignored, as the binder ignores it (<see cref="ServerJson.Options"/>):
    /// <c>Blacklisted</c> binds to <c>blacklisted</c>, so it must count as present for every guard built on this.
    /// </summary>
    public static bool Has(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.EnumerateObject().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A required text: missing, null or empty — <c>required</c>; longer than <paramref name="max"/> — <c>max</c>.</summary>
    public static string Text(string? value, string field, int max) =>
        string.IsNullOrEmpty(value) ? throw ApiException.Validation(field, "required")
        : value.Length > max ? throw ApiException.Validation(field, "max")
        : value;

    /// <summary>An optional text (null stays null); longer than <paramref name="max"/> — <c>max</c>.</summary>
    public static string? OptionalText(string? value, string field, int max) =>
        value is { Length: var length } && length > max ? throw ApiException.Validation(field, "max") : value;

    public static int? Range(int? value, string field, int min, int max) =>
        value < min ? throw ApiException.Validation(field, "min") : value > max ? throw ApiException.Validation(field, "max") : value;

    public static long? Range(long? value, string field, long min, long max) =>
        value < min ? throw ApiException.Validation(field, "min") : value > max ? throw ApiException.Validation(field, "max") : value;
}
