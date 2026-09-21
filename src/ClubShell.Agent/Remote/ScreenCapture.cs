using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Native;
using ClubShell.Windows.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Remote;

/// <summary>
/// Fallback capture path: the Agent runs in session 0 and cannot always reach the interactive desktop (e.g. the secure
/// desktop during UAC / the lock screen), so it asks the Shell — which lives in the user's session — to capture instead.
/// Implemented by the IPC layer.
/// </summary>
public interface IShellCaptureRequester
{
    /// <summary>Requests a capture from the Shell; returns the encoded image bytes, or <see langword="null"/> when unavailable.</summary>
    Task<byte[]?> RequestCaptureAsync(int? monitor, int quality, int? maxWidth, CancellationToken cancellationToken);
}

/// <summary>A captured frame as a PNG (content type <c>image/png</c>).</summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Png">Encoded PNG bytes.</param>
public sealed record CaptureResult(int Width, int Height, byte[] Png)
{
    /// <summary>MIME type of <see cref="Png"/>.</summary>
    public const string ContentType = "image/png";
}

/// <summary>
/// Captures the interactive desktop and, for <see cref="ServerCommandType.Screenshot"/>, uploads the result to a
/// pre-signed URL (SERVER_API.md §6.1). It first tries an in-process GDI <c>BitBlt</c> after switching the capture
/// thread to the input desktop (<c>OpenInputDesktop</c> + <c>SetThreadDesktop</c>); when that is not possible it asks the
/// Shell via <see cref="IShellCaptureRequester"/>. Frames are encoded as PNG with an in-file zlib (deflate) writer — the
/// Agent does not reference <c>System.Drawing</c> — so <see cref="ScreenshotCommand.Quality"/> (a JPEG knob) is ignored.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapture
{
    // GENERIC access over the desktop is enough for GetDC/BitBlt and, later, journal-playback input injection.
    private const uint DesktopAccess = NativeConst.DESKTOP_READOBJECTS | NativeConst.DESKTOP_CREATEWINDOW |
        NativeConst.DESKTOP_WRITEOBJECTS | NativeConst.DESKTOP_ENUMERATE | NativeConst.DESKTOP_JOURNALPLAYBACK;
    private const uint CaptureBlt = 0x40000000; // CAPTUREBLT: include layered windows.

    private readonly IShellCaptureRequester _shell;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<ScreenCapture> _logger;

    /// <summary>Creates the capture service.</summary>
    public ScreenCapture(
        IShellCaptureRequester shell,
        IHttpClientFactory httpClientFactory,
        IClock clock,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<ScreenCapture> logger)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _shell = shell;
        _httpClientFactory = httpClientFactory;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Captures monitor <paramref name="monitor"/> (or the primary when <see langword="null"/>) as a PNG, downscaled to
    /// <paramref name="maxWidth"/> when set. Returns <see langword="null"/> when neither path produced a frame.
    /// </summary>
    /// <exception cref="IpcException">Screen capture is disabled by configuration.</exception>
    public async Task<CaptureResult?> CaptureAsync(int? monitor, int quality, int? maxWidth, CancellationToken cancellationToken)
    {
        if (!_settings.CurrentValue.RemoteAdmin.AllowScreenCapture)
        {
            throw new IpcException(IpcError.PolicyDenied("remoteAdmin.allowScreenCapture"));
        }

        try
        {
            var gdi = await GdiCaptureAsync(monitor, maxWidth, cancellationToken).ConfigureAwait(false);
            if (gdi is not null)
            {
                return gdi;
            }

            _logger.LogInformation("GDI capture returned nothing; asking the Shell");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GDI capture failed; asking the Shell");
        }

        var shellBytes = await _shell.RequestCaptureAsync(monitor, quality, maxWidth, cancellationToken).ConfigureAwait(false);
        if (shellBytes is null || shellBytes.Length == 0)
        {
            return null;
        }

        _ = TryReadPngSize(shellBytes, out var width, out var height);
        return new CaptureResult(width, height, shellBytes);
    }

    /// <summary>
    /// Captures per <paramref name="command"/> and <c>PUT</c>s the PNG to <see cref="ScreenshotCommand.UploadUrl"/>.
    /// </summary>
    /// <exception cref="IpcException">Capture is disabled or produced no frame; upload failed.</exception>
    public async Task<ScreenshotResult> CaptureAndUploadAsync(ScreenshotCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var capture = await CaptureAsync(command.Monitor, command.Quality, command.MaxWidth, cancellationToken).ConfigureAwait(false)
            ?? throw new IpcException(IpcError.Of(ErrorCode.Internal, "Screen capture produced no frame"));

        using var content = new ByteArrayContent(capture.Png);
        content.Headers.ContentType = new MediaTypeHeaderValue(CaptureResult.ContentType);

        // A plain (unauthenticated, unsigned) client: UploadUrl is a pre-signed URL, not a server API path.
        var client = _httpClientFactory.CreateClient();
        try
        {
            using var response = await client.PutAsync(command.UploadUrl, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new IpcException(IpcError.Of(ErrorCode.ServerUnavailable, $"Screenshot upload returned {(int)response.StatusCode}"));
            }
        }
        catch (HttpRequestException ex)
        {
            throw new IpcException(IpcError.Of(ErrorCode.ServerUnavailable, "Screenshot upload failed: " + ex.Message));
        }

        _logger.LogInformation("Screenshot {Width}x{Height} ({Bytes} bytes) uploaded", capture.Width, capture.Height, capture.Png.Length);
        return new ScreenshotResult(capture.Width, capture.Height, capture.Png.Length, _clock.UtcNow);
    }

    private Task<CaptureResult?> GdiCaptureAsync(int? monitor, int? maxWidth, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CaptureResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // SetThreadDesktop must run on a dedicated (non-pool) thread: the desktop binding lives with the thread and would
        // corrupt a shared thread-pool thread. The thread exits after the capture, releasing the binding.
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(CaptureOnInputDesktop(monitor, maxWidth));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "clubshell-capture",
        };
        thread.Start();

        return completion.Task.WaitAsync(cancellationToken);
    }

    private CaptureResult? CaptureOnInputDesktop(int? monitor, int? maxWidth)
    {
        using var desktop = User32.OpenInputDesktop(0, false, DesktopAccess);
        if (desktop.IsInvalid)
        {
            _logger.LogDebug("OpenInputDesktop failed ({Error})", Win32Error.Last());
            return null;
        }

        if (!User32.SetThreadDesktop(desktop))
        {
            _logger.LogDebug("SetThreadDesktop failed ({Error})", Win32Error.Last());
            return null;
        }

        var (left, top, width, height) = ResolveCaptureRect(monitor);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var bgra = BitBltToBuffer(left, top, width, height);
        if (bgra is null)
        {
            return null;
        }

        if (maxWidth is { } max && max > 0 && width > max)
        {
            var targetHeight = Math.Max(1, (int)Math.Round((double)height * max / width));
            bgra = Downscale(bgra, width, height, max, targetHeight);
            width = max;
            height = targetHeight;
        }

        return new CaptureResult(width, height, EncodePng(bgra, width, height));
    }

    private static (int Left, int Top, int Width, int Height) ResolveCaptureRect(int? monitor)
    {
        var monitors = MonitorManager.Enumerate();
        if (monitors.Count == 0)
        {
            var w = User32.GetSystemMetrics(NativeConst.SM_CXSCREEN);
            var h = User32.GetSystemMetrics(NativeConst.SM_CYSCREEN);
            return (0, 0, w, h);
        }

        DisplayDevice device;
        if (monitor is { } index)
        {
            device = monitors.FirstOrDefault(m => m.Index == index)
                ?? monitors.FirstOrDefault(m => m.Primary)
                ?? monitors[0];
        }
        else
        {
            device = monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        }

        return (device.Bounds.Left, device.Bounds.Top, device.Width, device.Height);
    }

    private byte[]? BitBltToBuffer(int left, int top, int width, int height)
    {
        var screenDc = User32.GetDC(0);
        if (screenDc == 0)
        {
            return null;
        }

        nint memDc = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            memDc = Gdi32.CreateCompatibleDC(screenDc);
            bitmap = Gdi32.CreateCompatibleBitmap(screenDc, width, height);
            if (memDc == 0 || bitmap == 0)
            {
                return null;
            }

            previous = Gdi32.SelectObject(memDc, bitmap);
            if (!Gdi32.BitBlt(memDc, 0, 0, width, height, screenDc, left, top, NativeConst.SRCCOPY | CaptureBlt))
            {
                _logger.LogDebug("BitBlt failed ({Error})", Win32Error.Last());
                return null;
            }

            // The bitmap must not be selected into a DC while GetDIBits reads it.
            Gdi32.SelectObject(memDc, previous);
            previous = 0;

            var buffer = new byte[width * height * 4];
            var header = BITMAPINFOHEADER.Bgra32TopDown(width, height);
            var scanlines = Gdi32.GetDIBits(memDc, bitmap, 0, (uint)height, ref buffer[0], ref header, NativeConst.DIB_RGB_COLORS);
            if (scanlines == 0)
            {
                _logger.LogDebug("GetDIBits failed ({Error})", Win32Error.Last());
                return null;
            }

            return buffer;
        }
        finally
        {
            if (previous != 0)
            {
                Gdi32.SelectObject(memDc, previous);
            }

            if (bitmap != 0)
            {
                Gdi32.DeleteObject(bitmap);
            }

            if (memDc != 0)
            {
                Gdi32.DeleteDC(memDc);
            }

            User32.ReleaseDC(0, screenDc);
        }
    }

    private static byte[] Downscale(byte[] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var result = new byte[dstWidth * dstHeight * 4];
        for (var y = 0; y < dstHeight; y++)
        {
            var srcY = (int)((long)y * srcHeight / dstHeight);
            var srcRow = srcY * srcWidth * 4;
            var dstRow = y * dstWidth * 4;
            for (var x = 0; x < dstWidth; x++)
            {
                var srcX = (int)((long)x * srcWidth / dstWidth);
                var s = srcRow + (srcX * 4);
                var d = dstRow + (x * 4);
                result[d] = source[s];
                result[d + 1] = source[s + 1];
                result[d + 2] = source[s + 2];
                result[d + 3] = source[s + 3];
            }
        }

        return result;
    }

    /// <summary>Encodes a top-down 32bpp BGRA buffer as a PNG (24-bit RGB, deflate via <see cref="ZLibStream"/>).</summary>
    private static byte[] EncodePng(ReadOnlySpan<byte> bgra, int width, int height)
    {
        // One filter byte (0 = none) then RGB triplets per scanline.
        var stride = 1 + (width * 3);
        var raw = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var src = y * width * 4;
            var dst = y * stride;
            raw[dst++] = 0;
            for (var x = 0; x < width; x++)
            {
                var p = src + (x * 4);
                raw[dst++] = bgra[p + 2]; // R
                raw[dst++] = bgra[p + 1]; // G
                raw[dst++] = bgra[p];     // B
            }
        }

        byte[] idat;
        using (var deflated = new MemoryStream())
        {
            using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            idat = deflated.ToArray();
        }

        using var png = new MemoryStream(idat.Length + 128);
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // color type: truecolour (RGB)
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", idat);
        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        if (!data.IsEmpty)
        {
            stream.Write(data);
        }

        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static bool TryReadPngSize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        if (png.Length >= 24 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47)
        {
            width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
            return width > 0 && height > 0;
        }

        width = 0;
        height = 0;
        return false;
    }

    /// <summary>CRC-32 (IEEE 802.3) over a PNG chunk's type followed by its data.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in type)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var n = 0u; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
