using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Updates;

/// <summary>Where the packages of published releases are kept (<c>Updates:StoragePath</c>, under the data volume).</summary>
public sealed class UpdateOptions
{
    public const int MinTokenLength = 24;

    /// <summary>Folder of <c>&lt;channel&gt;/&lt;version&gt;/&lt;file&gt;</c>; relative to the content root.</summary>
    public string StoragePath { get; set; } = "data/updates";

    /// <summary>
    /// The release publisher's token (<c>publishToken</c> of the contract, <c>CLUBSHELL_PUBLISH_TOKEN</c> of publish.ps1), at
    /// least <see cref="MinTokenLength"/> characters; without one the server hosts no releases (<c>501</c>).
    /// </summary>
    public string PublishToken { get; set; } = "";

    public bool PublishEnabled => PublishToken.Length >= MinTokenLength;

    /// <summary>Constant-time comparison with <see cref="PublishToken"/>; always false while publishing is off.</summary>
    public bool IsPublishToken(string? presented) =>
        PublishEnabled && presented is not null
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presented)),
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(PublishToken)));
}

/// <summary>One published release: the publisher's <c>manifest.json</c> and its components as <see cref="UpdateManifest"/>.</summary>
public sealed record Release(string Channel, string Version, JsonElement Manifest, DateTimeOffset PublishedAt)
{
    /// <summary>The component's entry, or <see langword="null"/> when the release does not carry it.</summary>
    public UpdateManifest? Component(UpdateComponent component) =>
        Manifest.TryGetProperty("components", out var all) && all.ValueKind == JsonValueKind.Object
        && all.TryGetProperty(component.ToString().ToLowerInvariant(), out var entry) && entry.ValueKind == JsonValueKind.Object
            ? JsonDefaults.Deserialize<UpdateManifest>(entry.GetRawText())
            : null;
}

/// <summary>
/// Published ClubShell releases (UPDATES.md §8): the latest per channel in <c>update_releases</c>, packages on disk.
/// Versions compare as semver (<see cref="Compare"/>); a pre-release sorts below its release.
/// </summary>
public sealed partial class Releases(NpgsqlDataSource db, UpdateOptions options)
{
    public static readonly string[] Channels = ["stable", "beta"];

    /// <summary>The latest release of <paramref name="channel"/>, or <see langword="null"/>.</summary>
    public async Task<Release?> LatestAsync(string channel)
    {
        await using var c = await db.OpenConnectionAsync();
        var row = await c.QuerySingleOrDefaultAsync<(string Channel, string Version, string Manifest, DateTime PublishedAt)?>(
            "SELECT channel, version, manifest::text, published_at FROM update_releases WHERE channel = @channel", new { channel });
        return row is { } r ? new Release(r.Channel, r.Version, JsonElement.Parse(r.Manifest), new DateTimeOffset(r.PublishedAt, TimeSpan.Zero)) : null;
    }

    /// <summary>Stores <paramref name="manifest"/> as the latest release of <paramref name="channel"/> (an older version too: a rollback).</summary>
    public async Task SaveAsync(string channel, string version, JsonElement manifest, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO update_releases (channel, version, manifest, published_at) VALUES (@channel, @version, @manifest::jsonb, @now)
            ON CONFLICT (channel) DO UPDATE SET version = excluded.version, manifest = excluded.manifest, published_at = excluded.published_at
            """,
            new { channel, version, manifest = manifest.GetRawText(), now });
    }

    /// <summary>
    /// The file of a package on disk for <c>&lt;channel&gt;/&lt;version&gt;/&lt;path&gt;</c>; <c>404</c> for a path that
    /// would leave the folder (any segment not of letters, digits, <c>.</c>, <c>_</c>, <c>-</c>, <c>+</c>, or starting with a dot).
    /// </summary>
    public string FilePath(string channel, string version, string path)
    {
        if (!Channels.Contains(channel, StringComparer.Ordinal) || !VersionPattern().IsMatch(version))
        {
            throw ApiException.NotFound("file");
        }

        var segments = path.Split('/');
        if (segments.Length is 0 or > 4 || segments.Any(s => !SegmentPattern().IsMatch(s) || s.StartsWith('.')))
        {
            throw ApiException.NotFound("file");
        }

        return Path.Combine([options.StoragePath, channel, version, .. segments]);
    }

    /// <summary>A semver <c>MAJOR.MINOR.PATCH[-pre][+build]</c>, else <see langword="null"/>.</summary>
    public static (int Major, int Minor, int Patch, string? Pre)? Parse(string? version)
    {
        var m = version is null ? null : SemverPattern().Match(version.Trim());
        return m is { Success: true }
            ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
               int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), m.Groups[4].Success ? m.Groups[4].Value : null)
            : null;
    }

    /// <summary>
    /// Semver order of two versions; one that does not parse (an agent reporting "", "dev") is older than any that does.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var (x, y) = (Parse(a), Parse(b));
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        var (l, r) = (x.Value, y.Value);
        var c = l.Major.CompareTo(r.Major);
        c = c != 0 ? c : l.Minor.CompareTo(r.Minor);
        c = c != 0 ? c : l.Patch.CompareTo(r.Patch);
        if (c != 0)
        {
            return c;
        }

        return (l.Pre, r.Pre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(l.Pre, r.Pre),
        };
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemverPattern();

    [GeneratedRegex(@"^[0-9A-Za-z.+-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[0-9A-Za-z._+-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
}
