namespace BabyAI.Desktop;

internal enum FramePlacementMode { Restored, Maximized, Minimized }
internal readonly record struct FrameBounds(int X, int Y, int Width, int Height);

// Normal size is kept in DIPs. Maximized/minimized/orb rectangles must never
// replace it, including while a presenter is being restored for an orb resize.
internal sealed class WindowFramePlacement
{
    internal const int MinimumWidthDip = 760;
    internal const int MinimumHeightDip = 520;
    private double _widthDip = 840;
    private double _heightDip = 600;
    internal bool WantsMaximized { get; private set; }

    internal void Remember(bool expanded, FramePlacementMode mode, int width, int height, uint dpi)
    {
        if (!expanded || mode == FramePlacementMode.Minimized) return;
        WantsMaximized = mode == FramePlacementMode.Maximized;
        if (mode != FramePlacementMode.Restored || width <= 0 || height <= 0) return;
        _widthDip = width * 96.0 / ValidDpi(dpi);
        _heightDip = height * 96.0 / ValidDpi(dpi);
    }

    internal FrameBounds ExpandedBounds(int x, int y, FrameBounds work, uint dpi)
    {
        RequireWorkArea(work);
        var width = Math.Min(work.Width, Math.Max(Pixels(MinimumWidthDip, dpi), Pixels(_widthDip, dpi)));
        var height = Math.Min(work.Height, Math.Max(Pixels(MinimumHeightDip, dpi), Pixels(_heightDip, dpi)));
        return Clamp(x, y, width, height, work);
    }

    internal static FrameBounds OrbBounds(int x, int y, FrameBounds work, uint dpi)
    {
        RequireWorkArea(work);
        var size = Math.Min(Math.Min(work.Width, work.Height), Pixels(132, dpi));
        return Clamp(x, y, size, size, work);
    }

    internal static (int X, int Y) MaximizedOffset(FrameBounds monitor, FrameBounds work) =>
        (checked(work.X - monitor.X), checked(work.Y - monitor.Y));

    internal static int Pixels(double dips, uint dpi) => checked((int)Math.Ceiling(dips * ValidDpi(dpi) / 96.0));

    private static uint ValidDpi(uint dpi) => dpi == 0 ? 96 : dpi;

    private static FrameBounds Clamp(int x, int y, int width, int height, FrameBounds work) => new(
        Math.Clamp(x, work.X, checked(work.X + work.Width - width)),
        Math.Clamp(y, work.Y, checked(work.Y + work.Height - height)), width, height);

    private static void RequireWorkArea(FrameBounds work)
    {
        if (work.Width <= 0 || work.Height <= 0) throw new ArgumentOutOfRangeException(nameof(work));
    }
}
