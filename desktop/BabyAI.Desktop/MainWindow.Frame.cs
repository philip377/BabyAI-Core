using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    // Dimensions are DIPs; HWND messages use physical pixels.
    private const int ExpandedMinWidth = 760;
    private const int ExpandedMinHeight = 520;
    private FrameSubclass? _frameSubclass;
    private nint _frameHwnd;
    private bool _frameExpanded;
    private SizeInt32 _expandedSize;

    private void InitializeWindowFrame()
    {
        _frameHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _frameSubclass = FrameWindowProc;
        SetWindowSubclass(_frameHwnd, _frameSubclass, 1, 0);
        var dark = 1;
        DwmSetWindowAttribute(_frameHwnd, 20, ref dark, sizeof(int));
        var noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(_frameHwnd, 34, ref noBorder, sizeof(int));
        SetExpandedWindowMode(false);
        Closed += (_, _) => RemoveWindowSubclass(_frameHwnd, _frameSubclass, 1);
    }

    private int DipPixels(double value) => (int)Math.Ceiling(value * GetDpiForWindow(_frameHwnd) / 96.0);

    private void SetExpandedWindowMode(bool expanded)
    {
        if (!expanded && _frameExpanded) _expandedSize = AppWindow.Size;
        _frameExpanded = expanded;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = expanded;
            presenter.SetBorderAndTitleBar(false, false);
        }
        SetWindowRgn(_frameHwnd, 0, true);
        if (expanded)
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            var width = Math.Min(work.Width, Math.Max(DipPixels(ExpandedMinWidth), _expandedSize.Width == 0 ? DipPixels(840) : _expandedSize.Width));
            var height = Math.Min(work.Height, Math.Max(DipPixels(ExpandedMinHeight), _expandedSize.Height == 0 ? DipPixels(600) : _expandedSize.Height));
            var position = AppWindow.Position;
            AppWindow.MoveAndResize(new RectInt32(
                Math.Clamp(position.X, work.X, work.X + work.Width - width),
                Math.Clamp(position.Y, work.Y, work.Y + work.Height - height), width, height));
        }
        else
        {
            var size = DipPixels(132);
            AppWindow.Resize(new SizeInt32(size, size));
            // Windows owns the region after a successful call. Keep orb-only hit area compact.
            var region = CreateEllipticRgn(0, 0, size, size);
            if (SetWindowRgn(_frameHwnd, region, true) == 0) DeleteObject(region);
        }
    }

    private nint FrameWindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        const uint WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084, WmGetMinMaxInfo = 0x0024;
        if (message == 0x02E0) // WM_DPICHANGED: let WinUI update its scale first.
        {
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_frameExpanded) SetExpandedWindowMode(false);
            });
            return result;
        }
        if (message == WmNcCalcSize && wParam != 0) return 0;
        if (_frameExpanded && message == WmGetMinMaxInfo)
        {
            var limits = Marshal.PtrToStructure<FrameMinMax>(lParam);
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            limits.MinTrack = new FramePoint { X = Math.Min(work.Width, DipPixels(ExpandedMinWidth)), Y = Math.Min(work.Height, DipPixels(ExpandedMinHeight)) };
            Marshal.StructureToPtr(limits, lParam, false);
            return 0;
        }
        if (_frameExpanded && message == WmNcHitTest)
        {
            GetWindowRect(hwnd, out var rect);
            var x = (short)(lParam.ToInt64() & 0xffff);
            var y = (short)((lParam.ToInt64() >> 16) & 0xffff);
            var grip = DipPixels(7);
            var left = x < rect.Left + grip;
            var right = x >= rect.Right - grip;
            var top = y < rect.Top + grip;
            var bottom = y >= rect.Bottom - grip;
            if (top) return left ? 13 : right ? 14 : 12;
            if (bottom) return left ? 16 : right ? 17 : 15;
            if (left) return 10;
            if (right) return 11;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)] private struct FramePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct FrameRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FrameMinMax { public FramePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    private delegate nint FrameSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, FrameSubclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, FrameSubclass callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out FrameRect rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
