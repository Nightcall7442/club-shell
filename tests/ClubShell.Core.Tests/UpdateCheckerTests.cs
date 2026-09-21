using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using ClubShell.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReturnsExtensions;

namespace ClubShell.Core.Tests;

/// <summary>
/// <see cref="SemanticVersion"/> ordering, <see cref="UpdateManifestExtensions"/> applicability and apply windows,
/// <see cref="UpdateChecker"/> against a substituted <see cref="IServerClient"/> with a fake clock, and RSA-PSS
/// manifest signature verification (<see cref="Signing.VerifyManifest"/>).
/// </summary>
public sealed class UpdateCheckerTests
{
    private const string AgentVersion = "1.4.2";
    private static readonly DateTimeOffset PublishedAt = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    // 00:30 UTC is 05:30 club time (UTC+5): inside the default 04:00-07:00 apply window.
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 21, 0, 30, 0, TimeSpan.Zero));
    private readonly IServerClient _server = Substitute.For<IServerClient>();
    private readonly AgentSettings _settings = new();
    private readonly List<UpdateAvailable> _events = new();
    private readonly UpdateChecker _checker;

    public UpdateCheckerTests()
    {
        var options = Substitute.For<IOptionsMonitor<AgentSettings>>();
        options.CurrentValue.Returns(_settings);
        _checker = new UpdateChecker(_server, options, _clock, NullLogger<UpdateChecker>.Instance)
        {
            Current = new ComponentVersions(AgentVersion, AgentVersion),
        };
        _checker.Available += (_, e) => _events.Add(e);
        _server.GetUpdateManifestAsync(Arg.Any<UpdateChannel>(), Arg.Any<UpdateComponent>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
    }

    #region SemanticVersion

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v2", 2, 0, 0, null)]
    [InlineData("1.5", 1, 5, 0, null)]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [InlineData("1.2.3+build.7", 1, 2, 3, null)]
    [InlineData("1.2.3-rc.2+sha.abc", 1, 2, 3, "rc.2")]
    [InlineData("  V1.0.0 ", 1, 0, 0, null)]
    public void SemanticVersion_parses_documented_forms(string text, int major, int minor, int patch, string? prerelease)
    {
        var version = SemanticVersion.Parse(text);

        version.Should().Be(new SemanticVersion(major, minor, patch, prerelease));
        version.IsPrerelease.Should().Be(prerelease is not null);
        SemanticVersion.TryParse(text, out var parsed).Should().BeTrue();
        parsed.Should().Be(version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("1.2.3.4")]
    [InlineData("1..2")]
    [InlineData("a.b")]
    [InlineData("1.2.3-")]
    [InlineData("-1.0.0")]
    [InlineData("1.2.x")]
    [InlineData("1.-2.3")]
    public void SemanticVersion_rejects_malformed_text(string? text)
    {
        SemanticVersion.TryParse(text, out var parsed).Should().BeFalse();
        parsed.Should().Be(default(SemanticVersion));
        Action parse = () => SemanticVersion.Parse(text!);
        parse.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.9", "1.1.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0-1", "1.0.0-a")]
    [InlineData("1.0.0", "1.0.1-alpha")]
    public void SemanticVersion_orders_like_semver_2(string lowerText, string higherText)
    {
        var lower = SemanticVersion.Parse(lowerText);
        var higher = SemanticVersion.Parse(higherText);

        (lower < higher).Should().BeTrue();
        (higher > lower).Should().BeTrue();
        (lower <= higher).Should().BeTrue();
        (lower >= higher).Should().BeFalse();
        lower.CompareTo(higher).Should().BeNegative();
        higher.CompareTo(lower).Should().BePositive();
        lower.CompareTo(lower).Should().Be(0);
    }

    [Fact]
    public void SemanticVersion_ignores_build_metadata_and_prints_canonically()
    {
        SemanticVersion.Parse("1.2.3+build.9").Should().Be(SemanticVersion.Parse("v1.2.3"));
        SemanticVersion.Parse("1.2.3+build.9").ToString().Should().Be("1.2.3");
        SemanticVersion.Parse("1.2.3-beta.1+sha").ToString().Should().Be("1.2.3-beta.1");
        SemanticVersion.Parse("2").ToString().Should().Be("2.0.0");
        SemanticVersion.Zero.Should().Be(new SemanticVersion(0, 0, 0));
        (SemanticVersion.Parse("1.0.0") <= SemanticVersion.Parse("1.0.0")).Should().BeTrue();
    }

    #endregion

    #region Manifest applicability

    [Theory]
    [InlineData("1.5.0", false, null, "1.4.2", "1.4.2", true)]
    [InlineData("1.4.2", false, null, "1.4.2", "1.4.2", false)]
    [InlineData("1.3.0", false, null, "1.4.2", "1.4.2", false)]
    [InlineData("1.3.0", true, null, "1.4.2", "1.4.2", true)]
    [InlineData("1.4.2", true, null, "1.4.2", "1.4.2", false)]
    [InlineData("1.5.0", false, "1.5.0", "1.4.2", "1.4.2", false)]
    [InlineData("1.5.0", false, "1.4.2", "1.4.2", "1.4.2", true)]
    [InlineData("1.5.0", false, "1.4.0", "1.4.2", "1.4.2", true)]
    [InlineData("1.5.0", false, "", "1.4.2", "1.4.2", true)]
    [InlineData("1.5.0", false, "garbage", "1.4.2", "1.4.2", true)]
    [InlineData("1.5.0", false, "1.4.0", "1.4.2", "junk", false)]
    [InlineData("junk", true, null, "1.4.2", "1.4.2", false)]
    [InlineData("1.5.0", false, null, "unknown", "1.4.2", true)]
    [InlineData("1.5.0-beta.1", false, null, "1.5.0", "1.5.0", false)]
    [InlineData("1.5.0", false, null, "1.5.0-beta.1", "1.5.0", true)]
    public void IsApplicable_enforces_downgrade_protection_and_minAgentVersion(string version, bool mandatory, string? minAgent, string current, string agent, bool expected)
    {
        var manifest = Manifest(version, mandatory: mandatory, minAgentVersion: minAgent);

        manifest.IsApplicable(current, agent).Should().Be(expected);
        new UpdateManifestDocument(manifest, _clock.UtcNow, current, agent).IsApplicable.Should().Be(expected);
    }

    [Fact]
    public void IsNewerThan_and_SemVer_tolerate_unparsable_versions()
    {
        Manifest("1.5.0").IsNewerThan("1.4.2").Should().BeTrue();
        Manifest("1.5.0").IsNewerThan("1.5.0").Should().BeFalse();
        Manifest("1.5.0").IsNewerThan("1.6.0").Should().BeFalse();
        Manifest("1.5.0").IsNewerThan("junk").Should().BeTrue("an unknown installed version never blocks an update");
        Manifest("junk").IsNewerThan("1.0.0").Should().BeFalse();
        Manifest("junk").SemVer().Should().Be(SemanticVersion.Zero);
        Manifest("1.5.0-beta.2").SemVer().Should().Be(new SemanticVersion(1, 5, 0, "beta.2"));
    }

    [Theory]
    [InlineData("04:00", "07:00", "05:00", true)]
    [InlineData("04:00", "07:00", "04:00", true)]
    [InlineData("04:00", "07:00", "06:59", true)]
    [InlineData("04:00", "07:00", "07:00", false)]
    [InlineData("04:00", "07:00", "03:59", false)]
    [InlineData("23:00", "02:00", "23:00", true)]
    [InlineData("23:00", "02:00", "23:30", true)]
    [InlineData("23:00", "02:00", "01:59", true)]
    [InlineData("23:00", "02:00", "02:00", false)]
    [InlineData("23:00", "02:00", "12:00", false)]
    [InlineData("05:00", "05:00", "05:00", false)]
    public void IsInApplyWindow_wraps_midnight_with_inclusive_start_and_exclusive_end(string from, string to, string time, bool expected)
    {
        var window = new TimeWindow(TimeOnly.Parse(from, CultureInfo.InvariantCulture), TimeOnly.Parse(to, CultureInfo.InvariantCulture));
        var localTime = TimeOnly.Parse(time, CultureInfo.InvariantCulture);

        UpdateManifestExtensions.IsInApplyWindow(window, localTime).Should().Be(expected);
        new UpdateApplyWindowSettings { From = window.From, To = window.To }.Contains(localTime).Should().Be(expected, "agent.json settings and the policy window agree");
        UpdateManifestExtensions.IsInApplyWindow(null, localTime).Should().BeTrue("no window means any time");
    }

    [Fact]
    public void Package_file_names_derive_from_component_version_and_url()
    {
        Manifest("1.5.0").PackageFileName().Should().Be("agent-1.5.0.msi");
        Manifest("1.5.0").PackageExtension().Should().Be(".msi");
        (Manifest("1.5.0-beta.1", UpdateComponent.Shell) with { Url = "https://cdn.test/shell-setup.EXE?sig=1" }).PackageFileName().Should().Be("shell-1.5.0-beta.1.exe");
        (Manifest("1.5.0") with { Url = "not a url" }).PackageExtension().Should().Be(".msi");
        Manifest("1.5.0/../x").PackageFileName().Should().Be("agent-1.5.0_.._x.msi", "path characters are neutralized");
        new UpdateManifestDocument(Manifest("1.5.0"), _clock.UtcNow, AgentVersion, AgentVersion).PackageFileName.Should().Be("agent-1.5.0.msi");
    }

    #endregion

    #region UpdateChecker

    [Fact]
    public async Task CheckOnce_raises_Available_for_an_applicable_manifest()
    {
        var manifest = Manifest("1.5.0");
        ServerReturns(UpdateComponent.Agent, manifest);

        var response = await _checker.CheckOnceAsync(CancellationToken.None);

        var available = _events.Should().ContainSingle().Subject;
        available.Manifest.Should().Be(manifest);
        available.Current.Should().Be(AgentVersion);
        response.Current.Should().Be(new ComponentVersions(AgentVersion, AgentVersion));
        response.Agent.Should().Be(manifest);
        response.Shell.Should().BeNull();

        var latest = _checker.LatestAgent!;
        latest.Manifest.Should().Be(manifest);
        latest.CurrentVersion.Should().Be(AgentVersion);
        latest.AgentVersion.Should().Be(AgentVersion);
        latest.FetchedAt.Should().Be(_clock.UtcNow);
        latest.IsApplicable.Should().BeTrue();
        _checker.LatestShell.Should().BeNull();
        _checker.LastCheckAt.Should().Be(_clock.UtcNow);

        await _server.Received(1).GetUpdateManifestAsync(UpdateChannel.Stable, UpdateComponent.Agent, AgentVersion, Arg.Any<CancellationToken>());
        await _server.Received(1).GetUpdateManifestAsync(UpdateChannel.Stable, UpdateComponent.Shell, AgentVersion, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Available_is_raised_once_per_applicable_component_per_check()
    {
        ServerReturns(UpdateComponent.Agent, Manifest("1.5.0"));
        ServerReturns(UpdateComponent.Shell, Manifest("1.6.0", UpdateComponent.Shell));

        await _checker.CheckOnceAsync(CancellationToken.None);

        _events.Should().HaveCount(2);
        _events.Select(e => e.Manifest.Component).Should().Equal(UpdateComponent.Agent, UpdateComponent.Shell);
        _events.Should().OnlyContain(e => e.Current == AgentVersion);
        _checker.LatestShell!.Manifest.Version.Should().Be("1.6.0");

        await _checker.CheckOnceAsync(CancellationToken.None);

        _events.Should().HaveCount(4, "every check re-announces manifests that are still applicable; de-duplication is the applier's job");
    }

    [Theory]
    [InlineData("1.4.2", false, false)]
    [InlineData("1.3.0", false, false)]
    [InlineData("1.3.0", true, true)]
    [InlineData("1.4.2", true, false)]
    [InlineData("1.4.3-beta.1", false, true)]
    [InlineData("not-a-version", true, false)]
    public async Task CheckOnce_applies_downgrade_protection(string version, bool mandatory, bool expected)
    {
        ServerReturns(UpdateComponent.Agent, Manifest(version, mandatory: mandatory));

        var response = await _checker.CheckOnceAsync(CancellationToken.None);

        _events.Count.Should().Be(expected ? 1 : 0);
        (response.Agent is not null).Should().Be(expected);
        (_checker.LatestAgent is not null).Should().Be(expected);
        _checker.LastCheckAt.Should().Be(_clock.UtcNow, "a check completes even when nothing is applicable");
    }

    [Theory]
    [InlineData("1.5.0", false)]
    [InlineData("1.4.2", true)]
    [InlineData("1.4.0", true)]
    [InlineData(null, true)]
    public async Task Shell_manifest_requires_minAgentVersion_not_above_the_running_agent(string? minAgent, bool expected)
    {
        ServerReturns(UpdateComponent.Shell, Manifest("2.0.0", UpdateComponent.Shell, minAgentVersion: minAgent));

        var response = await _checker.CheckOnceAsync(CancellationToken.None);

        (response.Shell is not null).Should().Be(expected);
        _events.Count.Should().Be(expected ? 1 : 0);
    }

    [Fact]
    public async Task Suppressed_versions_are_skipped_until_the_suppression_expires()
    {
        ServerReturns(UpdateComponent.Agent, Manifest("1.5.0"));
        _checker.Suppress(UpdateComponent.Agent, "1.5.0", UpdateChecker.FailedSuppression);

        _checker.IsSuppressed(UpdateComponent.Agent, "1.5.0").Should().BeTrue();
        _checker.IsSuppressed(UpdateComponent.Agent, "1.5.1").Should().BeFalse();
        _checker.IsSuppressed(UpdateComponent.Shell, "1.5.0").Should().BeFalse();

        var response = await _checker.CheckOnceAsync(CancellationToken.None);
        response.Agent.Should().BeNull();
        _checker.LatestAgent.Should().BeNull();
        _events.Should().BeEmpty();

        _clock.Advance(UpdateChecker.FailedSuppression + TimeSpan.FromSeconds(1));
        _checker.IsSuppressed(UpdateComponent.Agent, "1.5.0").Should().BeFalse();

        response = await _checker.CheckOnceAsync(CancellationToken.None);
        response.Agent.Should().NotBeNull();
        _events.Should().ContainSingle();
        UpdateChecker.FailedSuppression.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task Server_failures_yield_no_manifest_and_do_not_throw()
    {
        _server.GetUpdateManifestAsync(Arg.Any<UpdateChannel>(), UpdateComponent.Agent, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServerApiException(ErrorCode.ServerUnavailable, HttpStatusCode.ServiceUnavailable, null, "trace-1"));
        ServerReturns(UpdateComponent.Shell, Manifest("1.5.0", UpdateComponent.Shell));

        var response = await _checker.CheckOnceAsync(CancellationToken.None);

        response.Agent.Should().BeNull();
        response.Shell.Should().NotBeNull("one failing component must not hide the other");
        _events.Should().ContainSingle().Which.Manifest.Component.Should().Be(UpdateComponent.Shell);
        _checker.LastCheckAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public async Task Throwing_listener_does_not_break_the_check()
    {
        ServerReturns(UpdateComponent.Agent, Manifest("1.5.0"));
        _checker.Available += (_, _) => throw new InvalidOperationException("listener boom");

        var response = await _checker.CheckOnceAsync(CancellationToken.None);

        response.Agent.Should().NotBeNull();
        _events.Should().ContainSingle();
    }

    [Fact]
    public async Task Mandatory_flag_propagates_and_bypasses_the_apply_window()
    {
        _settings.Updates.AutoInstall = false;
        _clock.UtcNow = new DateTimeOffset(2026, 9, 21, 7, 0, 0, TimeSpan.Zero); // 12:00 club time: outside 04:00-07:00
        var mandatory = Manifest("1.5.0", mandatory: true);
        ServerReturns(UpdateComponent.Agent, mandatory);

        var response = await _checker.CheckOnceAsync(CancellationToken.None);

        _events.Should().ContainSingle().Which.Manifest.Mandatory.Should().BeTrue();
        response.Agent!.Mandatory.Should().BeTrue();
        _checker.IsInApplyWindow().Should().BeFalse();
        _checker.MayApplyNow(mandatory).Should().BeTrue("mandatory packages ignore auto-install and the window");
        _checker.MayApplyNow(mandatory with { Mandatory = false }).Should().BeFalse();
        JsonDefaults.Serialize(_events[0]).Should().Contain("\"mandatory\":true").And.Contain("\"current\":\"1.4.2\"");
    }

    [Fact]
    public void Apply_window_is_evaluated_in_club_local_time()
    {
        _checker.ApplyWindow.Should().Be(new TimeWindow(new TimeOnly(4, 0), new TimeOnly(7, 0)));
        _checker.IsInApplyWindow().Should().BeTrue("05:30 club time is inside 04:00-07:00");
        _checker.MayApplyNow(Manifest("1.5.0")).Should().BeTrue();

        _clock.LocalOffset = TimeSpan.Zero;
        _checker.IsInApplyWindow().Should().BeFalse("00:30 is outside the window");
        _checker.MayApplyNow(Manifest("1.5.0")).Should().BeFalse();

        _settings.Updates.ApplyWindow = new UpdateApplyWindowSettings { From = new TimeOnly(23, 0), To = new TimeOnly(2, 0) };
        _checker.IsInApplyWindow().Should().BeTrue("the window wraps midnight");

        _settings.Updates.ApplyWindow = null;
        _checker.ApplyWindow.Should().BeNull();
        _checker.IsInApplyWindow().Should().BeTrue("no window means any time");
        _checker.MayApplyNow(Manifest("1.5.0")).Should().BeTrue();

        _settings.Updates.AutoInstall = false;
        _checker.MayApplyNow(Manifest("1.5.0")).Should().BeFalse("auto-install is off");
    }

    [Fact]
    public async Task Policy_overrides_channel_and_auto_install()
    {
        _checker.Channel.Should().Be(UpdateChannel.Stable);
        _checker.AutoInstall.Should().BeTrue();

        _settings.Updates.Channel = UpdateChannel.Beta;
        _settings.Updates.AutoInstall = false;
        _checker.Channel.Should().Be(UpdateChannel.Beta);
        _checker.AutoInstall.Should().BeFalse();

        _checker.Policy = SamplePolicy(new UpdatesPolicy(UpdateChannel.Stable, true));
        _checker.Channel.Should().Be(UpdateChannel.Stable);
        _checker.AutoInstall.Should().BeTrue();

        _checker.Policy = SamplePolicy(new UpdatesPolicy(UpdateChannel.Beta, false));
        await _checker.CheckOnceAsync(CancellationToken.None);
        await _server.Received(1).GetUpdateManifestAsync(UpdateChannel.Beta, UpdateComponent.Agent, AgentVersion, Arg.Any<CancellationToken>());
        await _server.DidNotReceive().GetUpdateManifestAsync(UpdateChannel.Stable, Arg.Any<UpdateComponent>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Current_versions_are_sent_per_component()
    {
        _checker.Current = new ComponentVersions("1.4.2", "1.3.9");

        await _checker.CheckOnceAsync(CancellationToken.None);

        await _server.Received(1).GetUpdateManifestAsync(UpdateChannel.Stable, UpdateComponent.Agent, "1.4.2", Arg.Any<CancellationToken>());
        await _server.Received(1).GetUpdateManifestAsync(UpdateChannel.Stable, UpdateComponent.Shell, "1.3.9", Arg.Any<CancellationToken>());
        _events.Should().BeEmpty();
    }

    [Fact]
    public void Jitter_stays_within_twenty_percent()
    {
        var interval = TimeSpan.FromMinutes(60);
        for (var i = 0; i < 200; i++)
        {
            UpdateChecker.Jitter(interval).TotalMinutes.Should().BeInRange(48, 72);
        }

        UpdateChecker.Jitter(interval, 0).Should().Be(interval);
        UpdateChecker.Jitter(TimeSpan.Zero).Should().Be(TimeSpan.Zero);
        UpdateChecker.Jitter(TimeSpan.FromSeconds(-5)).Should().Be(TimeSpan.FromSeconds(-5));
        UpdateChecker.Jitter(TimeSpan.FromMilliseconds(1)).Should().Be(TimeSpan.FromMilliseconds(1), "a spread below one millisecond is no jitter");
    }

    #endregion

    #region Manifest signatures

    [Fact]
    public void Canonical_manifest_bytes_have_the_documented_shape()
    {
        var manifest = Manifest("1.5.0");

        Encoding.UTF8.GetString(Signing.ManifestCanonicalBytes(manifest)).Should().Be(
            "{\"component\":\"agent\",\"version\":\"1.5.0\",\"url\":\"https://cdn.test/pkg.msi\",\"sha256\":\"" + new string('a', 64) + "\",\"size\":1234,\"publishedAt\":\"2026-09-21T09:00:00.000Z\"}");
        Encoding.UTF8.GetString(Signing.ManifestCanonicalBytes(Manifest("2.0.0-beta.1", UpdateComponent.Shell))).Should().StartWith("{\"component\":\"shell\",\"version\":\"2.0.0-beta.1\"");
        Signing.ManifestCanonicalBytes(manifest with { ReleaseNotes = "other", Mandatory = true, Signature = "x", Channel = UpdateChannel.Beta })
            .Should().Equal(Signing.ManifestCanonicalBytes(manifest), "only the documented fields are covered by the signature");
        Signing.ManifestCanonicalBytes(manifest with { PublishedAt = PublishedAt.ToOffset(TimeSpan.FromHours(5)) })
            .Should().Equal(Signing.ManifestCanonicalBytes(manifest), "publishedAt is normalized to UTC");
    }

    [Fact]
    public void Manifest_signature_verifies_with_the_publisher_key_and_fails_when_tampered()
    {
        using var rsa = RSA.Create(2048);
        var unsigned = Manifest("1.5.0");
        var signature = Convert.ToBase64String(rsa.SignData(Signing.ManifestCanonicalBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var signed = unsigned with { Signature = signature };

        Signing.VerifyManifest(signed, rsa).Should().BeTrue();
        using var publicOnly = Signing.LoadRsaPublicKey(rsa.ExportSubjectPublicKeyInfoPem());
        Signing.VerifyManifest(signed, publicOnly).Should().BeTrue("verification needs only the public key");

        Signing.VerifyManifest(signed with { Version = "9.9.9" }, publicOnly).Should().BeFalse();
        Signing.VerifyManifest(signed with { Url = "https://evil.test/pkg.msi" }, publicOnly).Should().BeFalse();
        Signing.VerifyManifest(signed with { Sha256 = new string('b', 64) }, publicOnly).Should().BeFalse();
        Signing.VerifyManifest(signed with { Size = 1235 }, publicOnly).Should().BeFalse();
        Signing.VerifyManifest(signed with { Component = UpdateComponent.Shell }, publicOnly).Should().BeFalse();
        Signing.VerifyManifest(signed with { PublishedAt = PublishedAt.AddSeconds(1) }, publicOnly).Should().BeFalse();
        Signing.VerifyManifest(signed with { Signature = "" }, publicOnly).Should().BeFalse();
        Signing.VerifyManifest(signed with { Signature = "not base64!" }, publicOnly).Should().BeFalse();
        Signing.VerifyManifest(signed with { ReleaseNotes = "edited", Mandatory = true, Channel = UpdateChannel.Beta }, publicOnly)
            .Should().BeTrue("fields outside the canonical form do not affect the signature");

        using var otherKey = RSA.Create(2048);
        Signing.VerifyManifest(signed, otherKey).Should().BeFalse("a different publisher key must not verify");

        var pkcs1 = Convert.ToBase64String(rsa.SignData(Signing.ManifestCanonicalBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Signing.VerifyManifest(signed with { Signature = pkcs1 }, publicOnly).Should().BeFalse("only RSA-PSS is accepted");
    }

    [Fact]
    public void Update_available_event_round_trips_through_the_contract_serializer()
    {
        var manifest = Manifest("1.5.0", mandatory: true, minAgentVersion: "1.4.0");
        var json = JsonDefaults.Serialize(new UpdateAvailable(manifest, AgentVersion));

        var parsed = JsonDefaults.Deserialize<UpdateAvailable>(json)!;
        parsed.Manifest.Should().Be(manifest);
        parsed.Current.Should().Be(AgentVersion);
        json.Should().Contain("\"channel\":\"stable\"").And.Contain("\"component\":\"agent\"").And.Contain("\"minAgentVersion\":\"1.4.0\"");
    }

    #endregion

    private static UpdateManifest Manifest(string version, UpdateComponent component = UpdateComponent.Agent, bool mandatory = false, string? minAgentVersion = null) =>
        new(UpdateChannel.Stable, component, version, "https://cdn.test/pkg.msi", new string('a', 64), 1234, "", "notes", mandatory, PublishedAt, minAgentVersion);

    private static Policy SamplePolicy(UpdatesPolicy updates) => new(
        12,
        PublishedAt,
        new ShellReplacementPolicy(true, "clubshell-shell.exe"),
        new ProcessAllowlistPolicy(AllowlistMode.Deny, Array.Empty<string>()),
        new UsbPolicy(false, true),
        new WebFilterPolicy(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        new ExplorerPolicy(true, true, true, true, true, true, Array.Empty<string>()),
        new PowerPolicy(),
        updates,
        new AntiCheatPolicy(Array.Empty<AntiCheatKind>(), false),
        new KioskPolicy(300, 600, true));

    private void ServerReturns(UpdateComponent component, UpdateManifest? manifest) =>
        _server.GetUpdateManifestAsync(Arg.Any<UpdateChannel>(), component, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(manifest);
}

/// <summary>Settable <see cref="IClock"/> for tests; <see cref="LocalNow"/> is <see cref="UtcNow"/> shifted by <see cref="LocalOffset"/> (UTC+5 by default).</summary>
internal sealed class FakeClock : IClock
{
    private readonly FakeTimeProvider _provider;

    public FakeClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
        _provider = new FakeTimeProvider(this);
    }

    public DateTimeOffset UtcNow { get; set; }

    public TimeSpan LocalOffset { get; set; } = TimeSpan.FromHours(5);

    public DateTimeOffset LocalNow => UtcNow.ToOffset(LocalOffset);

    public TimeProvider Provider => _provider;

    public void Advance(TimeSpan by) => UtcNow += by;

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly FakeClock _clock;

        public FakeTimeProvider(FakeClock clock)
        {
            _clock = clock;
        }

        public override DateTimeOffset GetUtcNow() => _clock.UtcNow;
    }
}
