using BabyAI.Desktop;

var checks = 0;
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}
var main = new FrameBounds(0, 0, 1920, 1040);
var placement = new WindowFramePlacement();
Require(placement.ExpandedBounds(120, 80, main, 96) == new FrameBounds(120, 80, 840, 600), "Default size changed");
Require(!placement.WantsMaximized, "Initial window is maximized");

// User resizes at 150%, maximizes, minimizes, collapses to an orb, moves to a
// 100% monitor, then opens and restores. Only the real normal size survives.
placement.Remember(true, FramePlacementMode.Restored, 1200, 900, 144);
placement.Remember(true, FramePlacementMode.Maximized, 3840, 2080, 192);
Require(placement.WantsMaximized, "Maximized mode was not remembered");
placement.Remember(true, FramePlacementMode.Minimized, 160, 27, 192);
Require(placement.WantsMaximized, "Minimize forgot former maximization");
placement.Remember(false, FramePlacementMode.Restored, 132, 132, 96);
Require(placement.WantsMaximized, "Orb rectangle forgot expanded mode");
var normal = placement.ExpandedBounds(20, 20, main, 96);
Require(normal == new FrameBounds(20, 20, 800, 600), "Maximized/minimized/orb pixels overwrote normal DIP size");
placement.Remember(true, FramePlacementMode.Restored, normal.Width, normal.Height, 96);
Require(!placement.WantsMaximized, "Restore kept stale maximization");
placement.Remember(true, FramePlacementMode.Minimized, 0, 0, 96);
Require(!placement.WantsMaximized, "Minimizing normal window invented maximization");
Require(placement.ExpandedBounds(0, 0, new(0, 0, 3840, 2080), 192).Width == 1600, "Normal size did not scale to 200%");

// Taskbars can be on any side and monitors can have negative coordinates.
foreach (var work in new[]
{
    new FrameBounds(0, 40, 1920, 1040), new FrameBounds(40, 0, 1880, 1080),
    new FrameBounds(-1920, -1080, 1920, 1040), new FrameBounds(-1880, 0, 1880, 1080),
    new FrameBounds(1920, -200, 2560, 1400), new FrameBounds(0, 0, 640, 360)
})
{
    var expanded = placement.ExpandedBounds(int.MaxValue, int.MinValue, work, 144);
    Require(expanded.X >= work.X && expanded.Y >= work.Y &&
        expanded.X + expanded.Width <= work.X + work.Width &&
        expanded.Y + expanded.Height <= work.Y + work.Height, "Expanded window escaped work area");
    var orb = WindowFramePlacement.OrbBounds(int.MinValue, int.MaxValue, work, 144);
    Require(orb.X >= work.X && orb.Y >= work.Y && orb.X + orb.Width <= work.X + work.Width &&
        orb.Y + orb.Height <= work.Y + work.Height && orb.Width == orb.Height,
        "Orb left work area or lost circular shape");
}
Require(placement.ExpandedBounds(0, 0, new(0, 0, 640, 360), 192) == new FrameBounds(0, 0, 640, 360),
    "Minimum-size rule made a small work area inaccessible");
Require(WindowFramePlacement.OrbBounds(0, 0, new(0, 0, 80, 60), 144) == new FrameBounds(0, 0, 60, 60),
    "Tiny work area produced an offscreen orb");
Require(WindowFramePlacement.MaximizedOffset(new(-1920, 0, 1920, 1080), new(-1880, 40, 1880, 1040)) == (40, 40),
    "Secondary monitor maximization offset is not monitor-relative");
Require(WindowFramePlacement.MaximizedOffset(new(1920, -1080, 2560, 1440), new(1920, -1080, 2520, 1400)) == (0, 0),
    "Right/bottom taskbar shifted maximized origin");

var small = new WindowFramePlacement();
small.Remember(true, FramePlacementMode.Restored, 200, 100, 96);
Require(small.ExpandedBounds(0, 0, main, 96) == new FrameBounds(0, 0, 760, 520), "Resize minimum was lost");
foreach (var dpi in new uint[] { 96, 120, 144, 168, 192, 240 })
{
    var scaled = new WindowFramePlacement();
    scaled.Remember(true, FramePlacementMode.Restored, WindowFramePlacement.Pixels(880, dpi), WindowFramePlacement.Pixels(640, dpi), dpi);
    var target = scaled.ExpandedBounds(0, 0, main, 96);
    Require(target.Width == 880 && target.Height == 640, "DPI conversion accumulated a size change");
    Require(WindowFramePlacement.Pixels(132, dpi) > 0, "Invalid orb scale");
}
Require(WindowFramePlacement.Pixels(132, 0) == 132, "Failed DPI query did not use 96 DPI fallback");
foreach (var work in new[] { new FrameBounds(0, 0, 0, 1), new FrameBounds(0, 0, 1, 0), new FrameBounds(0, 0, -1, 100) })
{
    var rejected = false;
    try { placement.ExpandedBounds(0, 0, work, 96); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Require(rejected, "Invalid display geometry was accepted");
}
Console.WriteLine($"PASS: {checks} window placement checks; normal/maximized/minimized/orb state, DPI, work areas and negative monitors; no UI/native calls.");
