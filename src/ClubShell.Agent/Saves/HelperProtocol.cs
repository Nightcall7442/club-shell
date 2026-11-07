using System.Buffers;
using System.Text;
using System.Text.Json;

namespace ClubShell.Agent.Saves;

/// <summary>Error codes of the diskless helper's saves commands (<c>{"ok":false,"error":"…"}</c>, SHELL_CHANGES 23).</summary>
public enum HelperErrorCode
{
    /// <summary>No error.</summary>
    None,

    /// <summary>The club server turned saves off, or its saves disk is gone.</summary>
    Disabled,

    /// <summary>The club server did not answer.</summary>
    ServerUnavailable,

    /// <summary>The player's saves are over the club's limit (200 MB); nothing was sent.</summary>
    OverLimit,

    /// <summary>Nothing is laid out for this player on this PC (no attach, a failed one, or a restart since).</summary>
    NotAttached,

    /// <summary>The PC is in master (image) mode.</summary>
    MasterMode,

    /// <summary>The Windows profile is not there: a wrong SID, or the profile was deleted before detach.</summary>
    ProfileNotFound,

    /// <summary>The request was malformed.</summary>
    BadRequest,

    /// <summary>Anything else, including a code this Agent does not know.</summary>
    Internal,

    /// <summary>
    /// Helper 1.8.0+ is replacing its own executable and is about to restart (self-update): the command was not run and
    /// nothing changed; retry in 5 s.
    /// </summary>
    Updating,
}

/// <summary>One games disk as the helper reports it in <c>ping</c> (SHELL_CHANGES 86).</summary>
/// <param name="Library"><c>games</c> (G:), <c>solo</c> (H:), …</param>
/// <param name="Letter">Drive letter, when known.</param>
/// <param name="State"><c>mounted</c>; <c>attaching</c>/<c>preparing</c> (still coming); <c>failed</c>, <c>noServer</c>, … (not coming).</param>
public sealed record HelperDisk(string Library, string? Letter, string State)
{
    /// <summary>The disk is on its letter.</summary>
    public bool Mounted => State == "mounted";

    /// <summary>The disk is still being attached or prepared by the server (an unknown state counts as such).</summary>
    public bool Pending => State is "attaching" or "preparing" || !KnownStates.Contains(State);

    private static readonly HashSet<string> KnownStates = new(StringComparer.Ordinal)
    {
        "mounted", "attaching", "preparing", "failed", "noServer", "unpublished", "disabled", "master",
    };
}

/// <summary>How a helper command ended, from the Agent's side.</summary>
public enum HelperResultKind
{
    /// <summary><c>{"ok":true}</c>.</summary>
    Ok,

    /// <summary>The helper answered <c>{"ok":false}</c> with <see cref="HelperResult.Code"/>.</summary>
    Error,

    /// <summary>No helper channel: the helper is not running, or older than 1.7.0.</summary>
    Unreachable,

    /// <summary>The pipe's server is not the helper service; nothing was sent.</summary>
    Refused,

    /// <summary>No answer in time. The helper still carries the command through.</summary>
    Timeout,

    /// <summary>The connection broke, or the answer was not the helper's JSON.</summary>
    Failed,
}

/// <summary>Outcome of one helper command.</summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Code">The helper's error code when <paramref name="Kind"/> is <see cref="HelperResultKind.Error"/>.</param>
/// <param name="Message">The helper's message, or the local failure: for the log only, never decided on or shown.</param>
/// <param name="Version">Helper version (answer to <c>ping</c>).</param>
public sealed record HelperResult(HelperResultKind Kind, HelperErrorCode Code = HelperErrorCode.None, string? Message = null, string? Version = null)
{
    /// <summary>SHA-256 of the helper's executable (<c>ping</c>, 1.8.0+).</summary>
    public string? Build { get; init; }

    /// <summary>The helper is replacing its executable right now (<c>ping</c>, 1.8.0+).</summary>
    public bool Updating { get; init; }

    /// <summary>
    /// <c>ping</c> of a helper that reports its disks (SHELL_CHANGES 86): <see langword="true"/> once nothing is being
    /// attached or prepared any more; <see langword="null"/> for a helper without the field.
    /// </summary>
    public bool? Ready { get; init; }

    /// <summary>The games disks the helper looks after (<c>ping</c>); <see langword="null"/> for a helper without the field.</summary>
    public IReadOnlyList<HelperDisk>? Disks { get; init; }

    /// <summary><see langword="true"/> for <c>{"ok":true}</c>.</summary>
    public bool IsOk => Kind == HelperResultKind.Ok;

    /// <summary>Short form for the log: <c>ok</c>, the helper's error code, or the local failure.</summary>
    public string Describe() => Kind switch
    {
        HelperResultKind.Ok => "ok",
        HelperResultKind.Error => HelperProtocol.WireCode(Code),
        HelperResultKind.Unreachable => "no helper channel",
        HelperResultKind.Refused => "not the helper service",
        HelperResultKind.Timeout => "no answer in time",
        _ => "channel failed",
    };
}

/// <summary>
/// The diskless helper's saves channel (<c>\\.\pipe\club-diskless-helper</c>, SHELL_CHANGES 23): one connection per
/// command, one UTF-8 JSON line without BOM in each direction, the request at most <see cref="MaxRequestBytes"/>.
/// </summary>
public static class HelperProtocol
{
    /// <summary>Pipe name (without <c>\\.\pipe\</c>).</summary>
    public const string PipeName = "club-diskless-helper";

    /// <summary>Windows service name of the helper.</summary>
    public const string ServiceName = "ClubDisklessHelper";

    /// <summary>The helper refuses longer request lines.</summary>
    public const int MaxRequestBytes = 4096;

    /// <summary>Longest answer read; the helper's are a few hundred bytes.</summary>
    public const int MaxReplyBytes = 64 * 1024;

    /// <summary><c>{"op":"ping"}</c>.</summary>
    public static byte[] Ping() => Line(w => w.WriteString("op", "ping"));

    /// <summary><c>{"op":"profile.attach","playerId":…,"windowsUserSid":…}</c>.</summary>
    public static byte[] Attach(string playerId, string windowsUserSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsUserSid);
        return Line(w =>
        {
            w.WriteString("op", "profile.attach");
            w.WriteString("playerId", playerId);
            w.WriteString("windowsUserSid", windowsUserSid);
        });
    }

    /// <summary><c>{"op":"profile.detach","playerId":…}</c>.</summary>
    public static byte[] Detach(string playerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerId);
        return Line(w =>
        {
            w.WriteString("op", "profile.detach");
            w.WriteString("playerId", playerId);
        });
    }

    /// <summary>
    /// The player id the helper keys saves by: the cash desk's <c>User.Id</c> in <c>D</c> form (36 characters with hyphens),
    /// always the same form — <c>N</c> would be another folder on the club server.
    /// </summary>
    public static string PlayerId(Guid userId) => userId.ToString("D");

    /// <summary>Wire form of an error code (<c>serverUnavailable</c>, …).</summary>
    public static string WireCode(HelperErrorCode code) => code switch
    {
        HelperErrorCode.Disabled => "disabled",
        HelperErrorCode.ServerUnavailable => "serverUnavailable",
        HelperErrorCode.OverLimit => "overLimit",
        HelperErrorCode.NotAttached => "notAttached",
        HelperErrorCode.MasterMode => "masterMode",
        HelperErrorCode.ProfileNotFound => "profileNotFound",
        HelperErrorCode.BadRequest => "badRequest",
        HelperErrorCode.Internal => "internal",
        HelperErrorCode.Updating => "updating",
        _ => "none",
    };

    /// <summary>Parses one answer line; anything but the helper's JSON is <see cref="HelperResultKind.Failed"/>.</summary>
    public static HelperResult ParseReply(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return new HelperResult(HelperResultKind.Failed, Message: "answer without \"ok\"");
            }

            string? message = String(root, "message");
            if (ok.GetBoolean())
            {
                return new HelperResult(HelperResultKind.Ok, Message: message, Version: String(root, "version"))
                {
                    Build = String(root, "build"),
                    Updating = root.TryGetProperty("updating", out JsonElement updating) && updating.ValueKind == JsonValueKind.True,
                    Ready = root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind is JsonValueKind.True or JsonValueKind.False ? ready.GetBoolean() : null,
                    Disks = root.TryGetProperty("disks", out JsonElement disks) && disks.ValueKind == JsonValueKind.Array ? ReadDisks(disks) : null,
                };
            }

            HelperErrorCode code = String(root, "error") switch
            {
                "disabled" => HelperErrorCode.Disabled,
                "serverUnavailable" => HelperErrorCode.ServerUnavailable,
                "overLimit" => HelperErrorCode.OverLimit,
                "notAttached" => HelperErrorCode.NotAttached,
                "masterMode" => HelperErrorCode.MasterMode,
                "profileNotFound" => HelperErrorCode.ProfileNotFound,
                "badRequest" => HelperErrorCode.BadRequest,
                "updating" => HelperErrorCode.Updating,
                _ => HelperErrorCode.Internal,
            };
            return new HelperResult(HelperResultKind.Error, code, message);
        }
        catch (JsonException ex)
        {
            return new HelperResult(HelperResultKind.Failed, Message: "answer is not JSON: " + ex.Message);
        }
    }

    /// <summary>
    /// Writes <paramref name="request"/> on a connected channel and reads the one answer line, waiting up to
    /// <paramref name="replyTimeout"/> for it. Never throws except on <paramref name="cancellationToken"/>.
    /// </summary>
    public static async Task<HelperResult> ExchangeAsync(Stream stream, byte[] request, TimeSpan replyTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(replyTimeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : replyTimeout);
        try
        {
            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            string? line = await ReadLineAsync(stream, timeout.Token).ConfigureAwait(false);
            return line is null
                ? new HelperResult(HelperResultKind.Failed, Message: "channel closed without an answer")
                : ParseReply(line);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HelperResult(HelperResultKind.Timeout, Message: $"no answer within {replyTimeout.TotalSeconds:0}s");
        }
        catch (IOException ex)
        {
            return new HelperResult(HelperResultKind.Failed, Message: ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return new HelperResult(HelperResultKind.Failed, Message: ex.Message);
        }
    }

    /// <summary>Reads up to <c>\n</c> (excluded, a trailing <c>\r</c> too); <see langword="null"/> when the stream ends first.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        byte[] chunk = new byte[512];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            buffer.Write(chunk.AsSpan(0, newline >= 0 ? newline : read));
            if (buffer.WrittenCount > MaxReplyBytes)
            {
                throw new InvalidDataException($"answer longer than {MaxReplyBytes} bytes");
            }

            if (newline >= 0)
            {
                return Decode(buffer.WrittenMemory);
            }
        }
    }

    private static string Decode(ReadOnlyMemory<byte> line)
    {
        ReadOnlySpan<byte> bytes = line.Span;
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static List<HelperDisk> ReadDisks(JsonElement disks)
    {
        var list = new List<HelperDisk>();
        foreach (JsonElement disk in disks.EnumerateArray())
        {
            if (disk.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            list.Add(new HelperDisk(String(disk, "library") ?? "?", String(disk, "letter"), String(disk, "state") ?? "attaching"));
        }

        return list;
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[] Line(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        byte[] line = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(line);
        line[^1] = (byte)'\n';
        if (line.Length > MaxRequestBytes)
        {
            throw new ArgumentException($"Helper request is longer than {MaxRequestBytes} bytes");
        }

        return line;
    }
}
