using System.Runtime.Versioning;

using ClubShell.Windows.Hooks;
using ClubShell.Windows.Native;

using Microsoft.Extensions.Logging;

namespace ClubShell.Windows.Input;

/// <summary>
/// Blocks user input during a lock. Two mechanisms:
/// <list type="bullet">
/// <item><description><b>Hard block</b> via <c>BlockInput</c> — total, but only usable from the interactive
/// desktop (it fails with <c>ERROR_ACCESS_DENIED</c> from session 0) and dangerous if left on, so it carries
/// an auto-unblock timer that must be renewed to stay engaged.</description></item>
/// <item><description><b>Soft lock</b> via a block-all keyboard hook plus a cursor clip — survives across the
/// lock without a watchdog and degrades gracefully.</description></item>
/// </list>
/// Both are released on <see cref="Dispose"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InputBlocker : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _maxHardDuration;
    private readonly ILogger? _logger;

    private bool _hardBlocked;
    private Timer? _autoUnblock;
    private LowLevelKeyboardHook? _softHook;
    private bool _softLocked;
    private bool _disposed;

    /// <summary>Creates the blocker. <paramref name="maxHardBlockDuration"/> caps how long a hard block stays engaged without a <see cref="Renew"/> (default 30 seconds).</summary>
    public InputBlocker(TimeSpan? maxHardBlockDuration = null, ILogger? logger = null)
    {
        _maxHardDuration = maxHardBlockDuration is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    /// <summary>Whether a hard <c>BlockInput</c> is currently engaged.</summary>
    public bool IsHardBlocked
    {
        get
        {
            lock (_gate)
            {
                return _hardBlocked;
            }
        }
    }

    /// <summary>Whether the soft lock (block-all hook + cursor clip) is currently engaged.</summary>
    public bool IsSoftLocked
    {
        get
        {
            lock (_gate)
            {
                return _softLocked;
            }
        }
    }

    /// <summary>
    /// Engages a hard block for at most <paramref name="maxDuration"/> (default: the constructor value); call
    /// <see cref="Renew"/> before it elapses to keep blocking. Returns <see langword="false"/> when
    /// <c>BlockInput</c> is denied (e.g. from session 0).
    /// </summary>
    public bool Block(TimeSpan? maxDuration = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!User32.BlockInput(true))
            {
                _logger?.LogWarning("BlockInput was denied with Win32 error {Error}", Win32Error.Last());
                return false;
            }

            _hardBlocked = true;
            ArmAutoUnblock(maxDuration ?? _maxHardDuration);
            return true;
        }
    }

    /// <summary>Resets the auto-unblock timer while a hard block is engaged.</summary>
    public void Renew(TimeSpan? maxDuration = null)
    {
        lock (_gate)
        {
            if (_hardBlocked)
            {
                ArmAutoUnblock(maxDuration ?? _maxHardDuration);
            }
        }
    }

    /// <summary>Releases a hard block. Safe to call when not blocked.</summary>
    public void Unblock()
    {
        lock (_gate)
        {
            _autoUnblock?.Dispose();
            _autoUnblock = null;
            if (_hardBlocked)
            {
                _ = User32.BlockInput(false);
                _hardBlocked = false;
            }
        }
    }

    /// <summary>
    /// Engages a soft lock: a block-all keyboard hook plus the cursor clipped to <paramref name="clip"/>
    /// (or to a 1×1 rectangle at the current pointer when null). Idempotent.
    /// </summary>
    public void SoftLock(RECT? clip = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_softLocked)
            {
                return;
            }

            LowLevelKeyboardHook hook = new(Array.Empty<KeyCombo>(), _logger) { BlockAll = true };
            hook.Start();
            _softHook = hook;

            RECT rect;
            if (clip is { } c)
            {
                rect = c;
            }
            else if (User32.GetCursorPos(out POINT p))
            {
                rect = new RECT(p.X, p.Y, p.X + 1, p.Y + 1);
            }
            else
            {
                rect = new RECT(0, 0, 1, 1);
            }

            _ = User32.ClipCursor(ref rect);
            _softLocked = true;
        }
    }

    /// <summary>Releases the soft lock. Safe to call when not locked.</summary>
    public void SoftUnlock()
    {
        lock (_gate)
        {
            if (!_softLocked)
            {
                return;
            }

            _ = User32.ClipCursor(0);
            _softHook?.Dispose();
            _softHook = null;
            _softLocked = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Unblock();
        SoftUnlock();
        GC.SuppressFinalize(this);
    }

    private void ArmAutoUnblock(TimeSpan duration)
    {
        _autoUnblock?.Dispose();
        _autoUnblock = duration > TimeSpan.Zero
            ? new Timer(_ => Unblock(), null, duration, Timeout.InfiniteTimeSpan)
            : null;
    }
}
