using System.Runtime.Versioning;

using ClubShell.Windows.Native;

using Microsoft.Extensions.Logging;

using ContractMonitor = ClubShell.Contracts.Pcs.MonitorInfo;

namespace ClubShell.Windows.Windows;

/// <summary>
/// A physical display, richer than the contract <see cref="ContractMonitor"/>: it carries the device name,
/// full monitor and work-area rectangles, the active mode and the (system) DPI.
/// </summary>
/// <param name="Index">Enumeration index (0-based).</param>
/// <param name="DeviceName">GDI device name, e.g. <c>\\.\DISPLAY1</c>.</param>
/// <param name="Bounds">Monitor rectangle in virtual-desktop coordinates.</param>
/// <param name="WorkArea">Work-area rectangle (monitor minus docked app bars).</param>
/// <param name="Width">Active horizontal resolution in pixels.</param>
/// <param name="Height">Active vertical resolution in pixels.</param>
/// <param name="RefreshHz">Active refresh rate in hertz (0 when unknown).</param>
/// <param name="Dpi">Effective system DPI.</param>
/// <param name="Primary">Whether this is the primary display.</param>
public sealed record DisplayDevice(
    int Index,
    string DeviceName,
    RECT Bounds,
    RECT WorkArea,
    int Width,
    int Height,
    int RefreshHz,
    int Dpi,
    bool Primary);

/// <summary>
/// Enumerates attached monitors and changes the display topology (primary monitor, resolution). Resolution
/// changes are validated with <c>CDS_TEST</c> before they are committed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MonitorManager
{
    private const int LogPixelsX = 88; // GetDeviceCaps index for horizontal DPI

    /// <summary>Enumerates every attached monitor with its active mode.</summary>
    public static IReadOnlyList<DisplayDevice> Enumerate()
    {
        List<DisplayDevice> devices = [];
        int dpi = SystemDpi();
        int index = 0;

        bool Callback(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData)
        {
            MONITORINFOEXW info = MONITORINFOEXW.Create();
            if (User32.GetMonitorInfoW(hMonitor, ref info))
            {
                string device = info.DeviceName;
                int width = info.rcMonitor.Width;
                int height = info.rcMonitor.Height;
                int hz = 0;

                DEVMODEW mode = DEVMODEW.Create();
                if (User32.EnumDisplaySettingsW(device, NativeConst.ENUM_CURRENT_SETTINGS, ref mode))
                {
                    width = (int)mode.dmPelsWidth;
                    height = (int)mode.dmPelsHeight;
                    hz = (int)mode.dmDisplayFrequency;
                }

                devices.Add(new DisplayDevice(index, device, info.rcMonitor, info.rcWork, width, height, hz, dpi, info.IsPrimary));
                index++;
            }

            return true;
        }

        _ = User32.EnumDisplayMonitors(0, 0, Callback, 0);
        return devices;
    }

    /// <summary>Enumerates monitors and maps them to the contract <see cref="ContractMonitor"/> DTOs.</summary>
    public static IReadOnlyList<ContractMonitor> EnumerateContract() => ToContract(Enumerate());

    /// <summary>Maps rich <see cref="DisplayDevice"/>s to the contract <see cref="ContractMonitor"/> DTOs.</summary>
    public static IReadOnlyList<ContractMonitor> ToContract(IEnumerable<DisplayDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices
            .Select(d => new ContractMonitor(d.Index, d.Width, d.Height, d.RefreshHz, d.Primary))
            .ToList();
    }

    /// <summary>
    /// Makes <paramref name="deviceName"/> the primary display, shifting all monitors so it sits at the
    /// virtual-desktop origin. Returns <see langword="false"/> when the device is unknown or the change is
    /// rejected.
    /// </summary>
    public static bool SetPrimary(string deviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);

        IReadOnlyList<DisplayDevice> devices = Enumerate();
        DisplayDevice? target = devices.FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        int originX = target.Bounds.Left;
        int originY = target.Bounds.Top;

        foreach (DisplayDevice device in devices)
        {
            DEVMODEW mode = DEVMODEW.Create();
            if (!User32.EnumDisplaySettingsW(device.DeviceName, NativeConst.ENUM_CURRENT_SETTINGS, ref mode))
            {
                continue;
            }

            mode.dmPositionX = device.Bounds.Left - originX;
            mode.dmPositionY = device.Bounds.Top - originY;
            mode.dmFields |= NativeConst.DM_POSITION;

            uint flags = NativeConst.CDS_UPDATEREGISTRY | NativeConst.CDS_NORESET;
            if (string.Equals(device.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                flags |= NativeConst.CDS_SET_PRIMARY;
            }

            _ = User32.ChangeDisplaySettingsExW(device.DeviceName, ref mode, 0, flags, 0);
        }

        // Apply the batched registry changes (NULL device + NULL mode).
        return User32.ChangeDisplaySettingsExW(null, 0, 0) == NativeConst.DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>
    /// Sets the resolution (and optional refresh rate) of <paramref name="deviceName"/>, testing the mode
    /// with <c>CDS_TEST</c> first. Returns <see langword="false"/> when the mode is unsupported or rejected.
    /// </summary>
    public static bool ApplyResolution(string deviceName, int width, int height, int refreshHz = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        DEVMODEW mode = DEVMODEW.Create();
        if (!User32.EnumDisplaySettingsW(deviceName, NativeConst.ENUM_CURRENT_SETTINGS, ref mode))
        {
            return false;
        }

        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        mode.dmFields |= NativeConst.DM_PELSWIDTH | NativeConst.DM_PELSHEIGHT;
        if (refreshHz > 0)
        {
            mode.dmDisplayFrequency = (uint)refreshHz;
            mode.dmFields |= NativeConst.DM_DISPLAYFREQUENCY;
        }

        if (User32.ChangeDisplaySettingsExW(deviceName, ref mode, 0, NativeConst.CDS_TEST, 0) != NativeConst.DISP_CHANGE_SUCCESSFUL)
        {
            return false;
        }

        int result = User32.ChangeDisplaySettingsExW(deviceName, ref mode, 0, NativeConst.CDS_UPDATEREGISTRY, 0);
        return result is NativeConst.DISP_CHANGE_SUCCESSFUL or NativeConst.DISP_CHANGE_RESTART;
    }

    // ponytail: reports the effective system DPI, not per-monitor DPI — per-monitor needs shcore
    // GetDpiForMonitor, an extra dependency a kiosk does not need. Upgrade there if mixed-DPI walls appear.
    private static int SystemDpi()
    {
        nint dc = User32.GetDC(0);
        if (dc == 0)
        {
            return 96;
        }

        try
        {
            int dpi = Gdi32.GetDeviceCaps(dc, LogPixelsX);
            return dpi > 0 ? dpi : 96;
        }
        finally
        {
            _ = User32.ReleaseDC(0, dc);
        }
    }
}

/// <summary>
/// Watches for display-topology changes (resolution, monitor add/remove, primary change) and raises
/// <see cref="Changed"/>. It polls the topology on a timer rather than hosting a message-only window for
/// <c>WM_DISPLAYCHANGE</c>: display changes are rare on a kiosk and polling avoids the full window-class
/// P/Invoke stack.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DisplayChangeWatcher : IAsyncDisposable
{
    private readonly TimeSpan _interval;
    private readonly ILogger? _logger;
    private readonly object _sync = new();

    private string _signature = string.Empty;
    private PeriodicTimer? _timer;
    private Task? _loop;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the watcher. <paramref name="pollInterval"/> defaults to 2 seconds.</summary>
    public DisplayChangeWatcher(TimeSpan? pollInterval = null, ILogger? logger = null)
    {
        _interval = pollInterval is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromSeconds(2);
        _logger = logger;
    }

    /// <summary>Raised when the display topology changes after the watcher has started.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts watching. Idempotent.</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _signature = Signature();
            _cts = new CancellationTokenSource();
            _timer = new PeriodicTimer(_interval);
            _loop = Task.Run(() => RunAsync(_timer, _cts.Token));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        PeriodicTimer? timer;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
            cts = _cts;
            timer = _timer;
            _loop = null;
            _cts = null;
            _timer = null;
        }

        GC.SuppressFinalize(this);

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        timer?.Dispose();

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        cts?.Dispose();
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                string current = Signature();
                if (!string.Equals(current, _signature, StringComparison.Ordinal))
                {
                    _signature = current;
                    Raise();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "A DisplayChangeWatcher.Changed handler threw");
        }
    }

    private static string Signature() =>
        string.Join(
            ";",
            MonitorManager.Enumerate().Select(static d =>
                $"{d.Index}:{d.DeviceName}:{d.Width}x{d.Height}@{d.RefreshHz}:{d.Bounds.Left},{d.Bounds.Top}:{(d.Primary ? 1 : 0)}"));
}
