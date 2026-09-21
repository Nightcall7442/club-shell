using System.Runtime.Versioning;

using ClubShell.Windows.Native;

using Microsoft.Extensions.Logging;

namespace ClubShell.Windows.Windows;

/// <summary>
/// Hides or shows the Windows taskbar for kiosk mode. Hiding sets the primary tray (<c>Shell_TrayWnd</c>),
/// every secondary tray (<c>Shell_SecondaryTrayWnd</c>, one per extra monitor) and the Start button to
/// <c>SW_HIDE</c>; <see cref="SetAutoHide"/> is a softer alternative via the app-bar API. Both are undone on
/// <see cref="Dispose"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TaskbarController : IDisposable
{
    private readonly ILogger? _logger;
    private nint _tray;
    private nint _startButton;
    private bool _hidden;
    private bool _autoHideEnabled;
    private bool _disposed;

    /// <summary>Resolves the taskbar and Start-button handles.</summary>
    public TaskbarController(ILogger? logger = null)
    {
        _logger = logger;
        ResolveHandles();
    }

    /// <summary>Whether the primary taskbar is currently visible.</summary>
    public bool IsVisible => _tray != 0 && User32.IsWindowVisible(_tray);

    /// <summary>Hides the primary taskbar, all secondary taskbars and the Start button.</summary>
    public void Hide()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tray == 0)
        {
            ResolveHandles();
        }

        if (_tray == 0)
        {
            _logger?.LogWarning("Taskbar window (Shell_TrayWnd) not found; nothing to hide");
        }

        SetVisibility(showState: false);
        _hidden = true;
    }

    /// <summary>Shows the primary taskbar, all secondary taskbars and the Start button.</summary>
    public void Show()
    {
        SetVisibility(showState: true);
        _hidden = false;
    }

    /// <summary>Enables or disables taskbar auto-hide via <c>SHAppBarMessage</c>.</summary>
    public void SetAutoHide(bool autoHide)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Shell32.SetTaskbarAutoHide(autoHide);
        _autoHideEnabled = autoHide;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hidden)
        {
            SetVisibility(showState: true);
            _hidden = false;
        }

        if (_autoHideEnabled)
        {
            Shell32.SetTaskbarAutoHide(false);
            _autoHideEnabled = false;
        }

        GC.SuppressFinalize(this);
    }

    private void ResolveHandles()
    {
        _tray = User32.FindWindowW("Shell_TrayWnd", null);
        _startButton = _tray != 0 ? User32.FindWindowExW(_tray, 0, "Start", null) : 0;
        if (_startButton == 0)
        {
            _startButton = User32.FindWindowW("Button", "Start");
        }
    }

    private void SetVisibility(bool showState)
    {
        int command = showState ? NativeConst.SW_SHOW : NativeConst.SW_HIDE;

        if (_tray != 0)
        {
            _ = User32.ShowWindow(_tray, command);
        }

        if (_startButton != 0)
        {
            _ = User32.ShowWindow(_startButton, command);
        }

        // Secondary taskbars are re-enumerated each time: their handles change when explorer restarts.
        nint secondary = 0;
        while ((secondary = User32.FindWindowExW(0, secondary, "Shell_SecondaryTrayWnd", null)) != 0)
        {
            _ = User32.ShowWindow(secondary, command);
        }
    }
}
