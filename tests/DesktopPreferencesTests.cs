using System;
using System.Drawing;
using System.IO;
using DeepSeekHarness;

internal static class DesktopPreferencesTests
{
    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new Exception("Failed: " + scenario);
        Console.WriteLine("PASS " + scenario);
    }

    private static int Main(string[] args)
    {
        string path = Path.Combine(args[0], "preferences-test.txt");
        DesktopPreferences missing = DesktopPreferences.Load(path + ".missing");
        Check(!missing.HasBounds && missing.Maximized && missing.Zoom == 1, "first launch defaults");
        DesktopPreferences saved = new DesktopPreferences();
        saved.Bounds = new Rectangle(-1400, 100, 1000, 700);
        saved.HasBounds = true;
        saved.Maximized = false;
        saved.Zoom = 1.2;
        saved.Save(path);
        DesktopPreferences loaded = DesktopPreferences.Load(path);
        Check(loaded.HasBounds && loaded.Bounds == saved.Bounds && !loaded.Maximized && loaded.Zoom == 1.2,
            "window and zoom round-trip on a negative-coordinate monitor");
        saved.Zoom = 1.3;
        saved.Save(path);
        Check(DesktopPreferences.Load(path).Zoom == 1.3, "atomic replacement of existing preferences");
        File.WriteAllText(path, "damaged preferences");
        Check(!DesktopPreferences.Load(path).HasBounds, "corrupt preferences recover to defaults");
        File.WriteAllText(path, "0|0|1000|700|0|NaN");
        Check(!DesktopPreferences.Load(path).HasBounds, "invalid zoom cannot reach WebView");
        File.WriteAllText(path, "0|0|1000|700|0|2.5");
        Check(DesktopPreferences.Load(path).Zoom == 1.5, "out-of-range zoom is bounded");
        Rectangle work = new Rectangle(0, 0, 1366, 728);
        Rectangle restored = DesktopPreferences.FitBounds(new Rectangle(3000, -800, 1900, 1200), work, new Size(760, 560));
        Check(work.Contains(restored) && restored.Size == work.Size, "removed monitor returns window to visible work area");
        Rectangle leftMonitor = new Rectangle(-1920, 0, 1920, 1040);
        restored = DesktopPreferences.FitBounds(saved.Bounds, leftMonitor, new Size(760, 560));
        Check(restored == saved.Bounds, "connected secondary monitor preserves position");
        Rectangle smallWork = new Rectangle(0, 0, 640, 480);
        restored = DesktopPreferences.FitBounds(saved.Bounds, smallWork, new Size(760, 560));
        Check(smallWork.Contains(restored), "small work area still produces visible bounds");
        return 0;
    }
}
