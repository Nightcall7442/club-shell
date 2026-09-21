using System.Globalization;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Pcs;

namespace ClubShell.Core.Updates;

/// <summary>Semantic version <c>MAJOR.MINOR.PATCH[-prerelease][+build]</c> (build metadata ignored for ordering).</summary>
/// <param name="Major">Major.</param>
/// <param name="Minor">Minor.</param>
/// <param name="Patch">Patch.</param>
/// <param name="Prerelease">Pre-release identifiers (<c>beta.1</c>) or <see langword="null"/> for a release.</param>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<SemanticVersion>
{
    /// <summary><c>0.0.0</c>.</summary>
    public static SemanticVersion Zero { get; } = new(0, 0, 0);

    /// <summary><see langword="true"/> for pre-release versions.</summary>
    public bool IsPrerelease => Prerelease is not null;

    /// <summary>Parses <paramref name="text"/>; throws <see cref="FormatException"/> when invalid.</summary>
    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"Invalid semantic version '{text}'");

    /// <summary>Parses <c>[v]MAJOR[.MINOR[.PATCH]][-prerelease][+build]</c>.</summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            span = span[..plus];
        }

        string? prerelease = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..].ToString();
            span = span[..dash];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        Span<int> parts = stackalloc int[3];
        var count = 0;
        while (true)
        {
            var dot = span.IndexOf('.');
            var part = dot >= 0 ? span[..dot] : span;
            if (count == 3 || part.Length == 0 || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out parts[count]))
            {
                return false;
            }

            count++;
            if (dot < 0)
            {
                break;
            }

            span = span[(dot + 1)..];
        }

        version = new SemanticVersion(parts[0], count > 1 ? parts[1] : 0, count > 2 ? parts[2] : 0, prerelease);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        if (Prerelease is null)
        {
            return other.Prerelease is null ? 0 : 1;
        }

        return other.Prerelease is null ? -1 : ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>Less-than.</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>Greater-than.</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>Less-or-equal.</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Greater-or-equal.</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() =>
        Prerelease is null
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}");

    private static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var length = Math.Min(leftParts.Length, rightParts.Length);
        for (var i = 0; i < length; i++)
        {
            var leftNumeric = int.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            var result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftParts[i], rightParts[i]),
            };
            if (result != 0)
            {
                return result;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }
}

/// <summary>Failure of an update stage (download, verification, staging, apply).</summary>
public sealed class UpdateException : Exception
{
    /// <summary>Creates a generic failure.</summary>
    public UpdateException()
        : this(UpdatePhase.Failed, "Update failed")
    {
    }

    /// <summary>Creates a generic failure with a message.</summary>
    public UpdateException(string message)
        : this(UpdatePhase.Failed, message)
    {
    }

    /// <summary>Creates a generic failure with a message and cause.</summary>
    public UpdateException(string message, Exception innerException)
        : this(UpdatePhase.Failed, message, innerException)
    {
    }

    /// <summary>Creates a failure of <paramref name="phase"/>.</summary>
    public UpdateException(UpdatePhase phase, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Phase = phase;
    }

    /// <summary>Phase that failed.</summary>
    public UpdatePhase Phase { get; }
}

/// <summary>A manifest as fetched from <c>GET /updates/{channel}/manifest</c>, with the local context it was evaluated against.</summary>
/// <param name="Manifest">Server manifest.</param>
/// <param name="FetchedAt">Fetch time.</param>
/// <param name="CurrentVersion">Installed version of the component at fetch time.</param>
/// <param name="AgentVersion">Running Agent version at fetch time (for <see cref="UpdateManifest.MinAgentVersion"/>).</param>
public sealed record UpdateManifestDocument(
    UpdateManifest Manifest,
    DateTimeOffset FetchedAt,
    string CurrentVersion,
    string AgentVersion)
{
    /// <summary><see langword="true"/> when the package should be installed (<see cref="UpdateManifestExtensions.IsApplicable"/>).</summary>
    public bool IsApplicable => Manifest.IsApplicable(CurrentVersion, AgentVersion);

    /// <summary>Local package file name.</summary>
    public string PackageFileName => Manifest.PackageFileName();
}

/// <summary>Version comparison and apply-window helpers over <see cref="UpdateManifest"/> (ARCHITECTURE.md §3, §11).</summary>
public static class UpdateManifestExtensions
{
    /// <summary>Parsed <see cref="UpdateManifest.Version"/> (<see cref="SemanticVersion.Zero"/> when unparsable).</summary>
    public static SemanticVersion SemVer(this UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return SemanticVersion.TryParse(manifest.Version, out var version) ? version : SemanticVersion.Zero;
    }

    /// <summary><see langword="true"/> when the package version is strictly greater than <paramref name="currentVersion"/>.</summary>
    public static bool IsNewerThan(this UpdateManifest manifest, string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return SemanticVersion.TryParse(manifest.Version, out var package)
            && (!SemanticVersion.TryParse(currentVersion, out var current) || package > current);
    }

    /// <summary>
    /// Downgrade protection and dependency check: the package must be newer than <paramref name="currentVersion"/>
    /// (or merely different when <see cref="UpdateManifest.Mandatory"/> flags a server-side rollback), and
    /// <see cref="UpdateManifest.MinAgentVersion"/>, when set, must not exceed <paramref name="agentVersion"/>.
    /// </summary>
    public static bool IsApplicable(this UpdateManifest manifest, string currentVersion, string agentVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!SemanticVersion.TryParse(manifest.Version, out var package))
        {
            return false;
        }

        var hasCurrent = SemanticVersion.TryParse(currentVersion, out var current);
        var versionOk = !hasCurrent || package > current || (manifest.Mandatory && package != current);
        if (!versionOk)
        {
            return false;
        }

        if (manifest.MinAgentVersion is { Length: > 0 } minimum && SemanticVersion.TryParse(minimum, out var min))
        {
            return SemanticVersion.TryParse(agentVersion, out var agent) && agent >= min;
        }

        return true;
    }

    /// <summary><see langword="true"/> when <paramref name="localTime"/> falls inside <paramref name="window"/> (wraps midnight); a <see langword="null"/> window means any time.</summary>
    public static bool IsInApplyWindow(TimeWindow? window, TimeOnly localTime)
    {
        if (window is null)
        {
            return true;
        }

        return window.From <= window.To
            ? localTime >= window.From && localTime < window.To
            : localTime >= window.From || localTime < window.To;
    }

    /// <summary>Extension of the package derived from <see cref="UpdateManifest.Url"/> (<c>.msi</c> unless the URL ends in <c>.exe</c>).</summary>
    public static string PackageExtension(this UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return ".exe";
        }

        return ".msi";
    }

    /// <summary>Local file name <c>&lt;component&gt;-&lt;version&gt;.msi|exe</c> (characters unsafe in file names replaced by <c>_</c>).</summary>
    public static string PackageFileName(this UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var component = manifest.Component == UpdateComponent.Agent ? "agent" : "shell";
        var invalid = Path.GetInvalidFileNameChars();
        var version = new string(manifest.Version.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return $"{component}-{version}{manifest.PackageExtension()}";
    }
}
