using ClubShell.Windows.Users;

namespace ClubShell.Windows.Tests;

/// <summary>
/// Covers the debt marker of <see cref="ProfileReset"/>. Deleting a profile needs a real account, but the marker is
/// what decides whether the next Agent start resets at all — if it does not survive a crash, a power cut hands the
/// previous player's profile to the next one.
/// </summary>
public sealed class ProfileResetMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clubshell-tests-" + Guid.NewGuid().ToString("N"));

    private ProfileReset NewReset() => new(markerPath: Path.Combine(_dir, "profile-reset.marker"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void DirtyUser_IsNullUntilMarked()
    {
        NewReset().DirtyUser.Should().BeNull();
    }

    [Fact]
    public void MarkDirty_SurvivesANewInstance()
    {
        NewReset().MarkDirty("club");

        // A fresh instance stands in for the next Agent start after a power cut.
        NewReset().DirtyUser.Should().Be("club");
    }

    [Fact]
    public void ClearDirty_PaysTheDebt()
    {
        var reset = NewReset();
        reset.MarkDirty("club");

        reset.ClearDirty();

        NewReset().DirtyUser.Should().BeNull();
    }

    [Fact]
    public void ClearDirty_IsSafeWhenNothingIsOwed()
    {
        var reset = NewReset();

        reset.ClearDirty();

        reset.DirtyUser.Should().BeNull();
    }

    [Fact]
    public void MarkDirty_IsIndependentOfTheLastResetTime()
    {
        var reset = NewReset();
        reset.TouchLastReset();
        reset.MarkDirty("club");

        reset.LastReset.Should().NotBeNull();
        reset.DirtyUser.Should().Be("club");
    }
}
