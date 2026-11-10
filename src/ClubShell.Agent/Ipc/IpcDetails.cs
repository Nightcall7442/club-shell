using System.Buffers;
using System.Globalization;
using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;

namespace ClubShell.Agent.Ipc;

/// <summary>
/// Flat IPC error <c>details</c> objects without a contract type: a <c>reason</c> plus the few fields the Shell shows in
/// its own words (<c>pcName</c>, <c>pcId</c>, <c>attemptsLeft</c>, …).
/// </summary>
internal static class IpcDetails
{
    /// <summary>Builds <c>{ key: value, … }</c>; strings, numbers, booleans and UUIDs are written as such, <see langword="null"/> values are left out.</summary>
    public static JsonElement Of(params (string Key, object? Value)[] pairs)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach ((string key, object? value) in pairs)
            {
                switch (value)
                {
                    case null:
                        break;
                    case string text:
                        writer.WriteString(key, text);
                        break;
                    case bool flag:
                        writer.WriteBoolean(key, flag);
                        break;
                    case int number:
                        writer.WriteNumber(key, number);
                        break;
                    case long number:
                        writer.WriteNumber(key, number);
                        break;
                    case Guid id:
                        writer.WriteString(key, id.ToString("D"));
                        break;
                    default:
                        writer.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>An <see cref="IpcException"/> of <paramref name="code"/> whose details carry <paramref name="reason"/> and <paramref name="extra"/>.</summary>
    public static IpcException Error(ErrorCode code, string message, string reason, params (string Key, object? Value)[] extra) =>
        new IpcError(code, message, Of([("reason", reason), .. extra])).ToException();

    /// <summary>An integer field of a details object (<c>attemptsLeft</c>), or <see langword="null"/>.</summary>
    public static int? IntOf(JsonElement? details, string name) =>
        details is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;
}
