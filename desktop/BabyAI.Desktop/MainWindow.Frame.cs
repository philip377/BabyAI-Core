using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    // Dimensions are DIPs; HWND messages use physical pixels.
    private const int ExpandedMinWidth = WindowFramePlacement.MinimumWidthDip;
    private const int ExpandedMinHeight = WindowFramePlacement.MinimumHeightDip;
    private FrameSubclass? _frameSubclass;
    private nint _frameHwnd;
    private bool _frameExpanded;
    private readonly WindowFramePlacement _framePlacement = new();
    private bool _frameChanging;
    private bool _frameDpiChanging;
    private bool _frameClosed;

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
        AppWindow.Changed += FrameWindowChanged;
        Closed += (_, _) =>
        {
            _frameClosed = true;
            AppWindow.Changed -= FrameWindowChanged;
            RemoveWindowSubclass(_frameHwnd, _frameSubclass, 1);
        };
    }

    private int DipPixels(double value) => WindowFramePlacement.Pixels(value, GetDpiForWindow(_frameHwnd));

    private void RememberExpandedPlacement()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var mode = presenter.State switch
        {
            OverlappedPresenterState.Maximized => FramePlacementMode.Maximized,
            OverlappedPresenterState.Minimized => FramePlacementMode.Minimized,
            _ => FramePlacementMode.Restored
        };
        // Presenter state and size events can arrive separately during maximize.
        // WINDOWPLACEMENT retains the real normal rectangle even in that gap;
        // its width/height are usable without mixing workspace/screen origins.
        var placement = new FrameNativePlacement { Length = Marshal.SizeOf<FrameNativePlacement>() };
        var width = 0;
        var height = 0;
        if (GetWindowPlacement(_frameHwnd, ref placement))
        {
            mode = placement.ShowCommand switch
            {
                3 => FramePlacementMode.Maximized,
                2 => FramePlacementMode.Minimized,
                _ => mode
            };
            width = placement.Normal.Right - placement.Normal.Left;
            height = placement.Normal.Bottom - placement.Normal.Top;
        }
        _framePlacement.Remember(_frameExpanded, mode, width, height,
            GetDpiForWindow(_frameHwnd));
    }

    private void FrameWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_frameClosed) return;
        if (!_frameChanging && !_frameDpiChanging) RememberExpandedPlacement();
        SyncWindowMaximizeButton();
    }

    private FrameBounds FrameWorkArea()
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        return new(work.X, work.Y, work.Width, work.Height);
    }

    private void ApplyFrameBounds(FrameBounds bounds) =>
        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));

    private void ToggleMaximizeWindow()
    {
        if (!_frameExpanded || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        RememberExpandedPlacement();
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        else presenter.Maximize();
        SyncWindowMaximizeButton();
    }

    private void MinimizeWindow()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        RememberExpandedPlacement();
        presenter.Minimize();
        SyncWindowMaximizeButton();
    }

    // Called after showing the HWND from the tray. Show/Activate alone does not
    // restore a minimized presenter, and must not lose its former maximization.
    private void RestoreWindowFromTray()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var maximize = _frameExpanded && _framePlacement.WantsMaximized;
        _frameChanging = true;
        try
        {
            if (presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
            if (maximize) presenter.Maximize();
            else if (presenter.State == OverlappedPresenterState.Restored)
            {
                var position = AppWindow.Position;
                ApplyFrameBounds(_frameExpanded
                    ? _framePlacement.ExpandedBounds(position.X, position.Y, FrameWorkArea(), GetDpiForWindow(_frameHwnd))
                    : WindowFramePlacement.OrbBounds(position.X, position.Y, FrameWorkArea(), GetDpiForWindow(_frameHwnd)));
            }
        }
        finally { _frameChanging = false; SyncWindowMaximizeButton(); }
    }

    private void SyncWindowMaximizeButton()
    {
        if (_frameClosed) return;
        var maximized = _frameExpanded && AppWindow.Presenter is OverlappedPresenter presenter &&
            (presenter.State == OverlappedPresenterState.Maximized ||
             presenter.State == OverlappedPresenterState.Minimized && _framePlacement.WantsMaximized);
        var label = maximized ? "Обычный размер" : "Развернуть окно";
        WindowMaximizeIcon.Glyph = maximized ? "\uE923" : "\uE922";
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(WindowMaximizeButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WindowMaximizeButton, label);
    }

    private void BeginWindowDrag()
    {
        if (!_frameExpanded || !GetCursorPos(out var cursor)) return;
        ReleaseCapture();
        // Native caption movement provides DPI-aware dragging and drag-to-restore.
        var coordinates = new nint((cursor.X & 0xffff) | ((cursor.Y & 0xffff) << 16));
        SendMessageW(_frameHwnd, 0x00A1, 2, coordinates); // WM_NCLBUTTONDOWN, HTCAPTION
    }

    private bool IsFrameCaption(int screenX, int screenY, FrameRect window)
    {
        try
        {
            if (!Root.IsLoaded || WindowDragHandle.XamlRoot is null ||
                WindowDragHandle.ActualWidth <= 0 || WindowDragHandle.ActualHeight <= 0) return false;
            var origin = WindowDragHandle.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
            var scale = WindowDragHandle.XamlRoot.RasterizationScale;
            var x = (screenX - window.Left) / scale;
            var y = (screenY - window.Top) / scale;
            return x >= origin.X && x < origin.X + WindowDragHandle.ActualWidth &&
                y >= origin.Y && y < origin.Y + WindowDragHandle.ActualHeight;
        }
        // The tree may detach during close/reparent. Never let managed XAML
        // exceptions escape the native window procedure or invent a drag area.
        catch { return false; }
    }

    private void SetExpandedWindowMode(bool expanded)
    {
        if (expanded && _frameExpanded) { SyncWindowMaximizeButton(); return; }
        if (!_frameDpiChanging) RememberExpandedPlacement();
        _frameChanging = true;
        try
        {
            _frameExpanded = expanded;
            var presenter = AppWindow.Presenter as OverlappedPresenter;
            if (presenter is not null)
            {
                // A minimized maximized window can restore to Maximized first.
                if (presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
                if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
                presenter.IsResizable = expanded;
                presenter.IsMaximizable = expanded;
                presenter.IsMinimizable = true;
                presenter.SetBorderAndTitleBar(false, false);
            }
            SetWindowRgn(_frameHwnd, 0, true);
            var position = AppWindow.Position;
            if (expanded)
            {
                ApplyFrameBounds(_framePlacement.ExpandedBounds(position.X, position.Y, FrameWorkArea(), GetDpiForWindow(_frameHwnd)));
                if (_framePlacement.WantsMaximized) presenter?.Maximize();
            }
            else
            {
                var bounds = WindowFramePlacement.OrbBounds(position.X, position.Y, FrameWorkArea(), GetDpiForWindow(_frameHwnd));
                ApplyFrameBounds(bounds);
                // Windows owns the region after a successful call.
                var region = CreateEllipticRgn(0, 0, bounds.Width, bounds.Height);
                if (SetWindowRgn(_frameHwnd, region, true) == 0) DeleteObject(region);
            }
        }
        finally { _frameChanging = false; SyncWindowMaximizeButton(); }
    }

    private nint FrameWindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        const uint WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084, WmGetMinMaxInfo = 0x0024;
        if (message == 0x02E0) // WM_DPICHANGED: let WinUI update its scale first.
        {
            _frameDpiChanging = true;
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (_frameClosed) { _frameDpiChanging = false; return; }
                try
                {
                    if (!_frameExpanded) SetExpandedWindowMode(false);
                    else if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Restored)
                    {
                        var position = AppWindow.Position;
                        ApplyFrameBounds(_framePlacement.ExpandedBounds(position.X, position.Y, FrameWorkArea(), GetDpiForWindow(_frameHwnd)));
                    }
                }
                finally { _frameDpiChanging = false; SyncWindowMaximizeButton(); }
            })) _frameDpiChanging = false;
            return result;
        }
        if (_frameExpanded && message == 0x00A3 && wParam == 2) // WM_NCLBUTTONDBLCLK, HTCAPTION
        {
            ToggleMaximizeWindow();
            return 0;
        }
        if (message == WmNcCalcSize && wParam != 0) return 0;
        if (_frameExpanded && message == WmGetMinMaxInfo)
        {
            var limits = Marshal.PtrToStructure<FrameMinMax>(lParam);
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            limits.MinTrack = new FramePoint { X = Math.Min(work.Width, DipPixels(ExpandedMinWidth)), Y = Math.Min(work.Height, DipPixels(ExpandedMinHeight)) };
            var monitor = new FrameMonitor { Size = Marshal.SizeOf<FrameMonitor>() };
            if (GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref monitor))
            {
                var offset = WindowFramePlacement.MaximizedOffset(
                    new(monitor.Monitor.Left, monitor.Monitor.Top, monitor.Monitor.Right - monitor.Monitor.Left, monitor.Monitor.Bottom - monitor.Monitor.Top),
                    new(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top));
                limits.MaxPosition = new FramePoint { X = offset.X, Y = offset.Y };
                limits.MaxSize = new FramePoint { X = monitor.Work.Right - monitor.Work.Left, Y = monitor.Work.Bottom - monitor.Work.Top };
                limits.MaxTrack = new FramePoint { X = Math.Max(limits.MaxTrack.X, limits.MaxSize.X), Y = Math.Max(limits.MaxTrack.Y, limits.MaxSize.Y) };
            }
            Marshal.StructureToPtr(limits, lParam, false);
            return 0;
        }
        if (_frameExpanded && message == WmNcHitTest)
        {
            if (!GetWindowRect(hwnd, out var rect)) return DefSubclassProc(hwnd, message, wParam, lParam);
            var x = (short)(lParam.ToInt64() & 0xffff);
            var y = (short)((lParam.ToInt64() >> 16) & 0xffff);
            var maximized = AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Maximized;
            var grip = DipPixels(7);
            var left = x < rect.Left + grip;
            var right = x >= rect.Right - grip;
            var top = y < rect.Top + grip;
            var bottom = y >= rect.Bottom - grip;
            if (!maximized)
            {
                if (top) return left ? 13 : right ? 14 : 12;
                if (bottom) return left ? 16 : right ? 17 : 15;
                if (left) return 10;
                if (right) return 11;
            }
            if (IsFrameCaption(x, y, rect)) return 2;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)] private struct FramePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct FrameRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FrameMinMax { public FramePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [StructLayout(LayoutKind.Sequential)] private struct FrameMonitor { public int Size; public FrameRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct FrameNativePlacement
    {
        public int Length;
        public uint Flags, ShowCommand;
        public FramePoint MinPosition, MaxPosition;
        public FrameRect Normal;
    }
    private delegate nint FrameSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, FrameSubclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, FrameSubclass callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out FrameRect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint hwnd, ref FrameNativePlacement placement);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out FramePoint point);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref FrameMonitor info);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
