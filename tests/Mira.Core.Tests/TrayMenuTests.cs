using Mira.Core;
using static TestSupport;

/// <summary>The menu of Mira's icon in Windows' notification area (« Carte »): what it shows, its text, and where it opens.</summary>
internal static class TrayMenuTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Menu de la zone de notification : la carte seulement en lecture, ni « Épisode suivant » ni « Réduire » quand ils ne servent pas", () =>
        {
            Assert(TrayMenuPlan.For(playing: true, canNext: true, windowShown: true) == new TrayMenuPlan.Parts(true, true, true), "An episode with a next one, Mira shown");
            Assert(TrayMenuPlan.For(true, false, true) == new TrayMenuPlan.Parts(true, false, true), "A film: no « Next episode »");
            Assert(TrayMenuPlan.For(false, true, false) == new TrayMenuPlan.Parts(false, false, false), "Nothing playing, Mira put away: no card, no « Minimize »");
            var episode = new MediaItem { Type = "Episode", Name = " Le signal ", SeriesName = "La dernière orbite", ParentIndexNumber = 1, IndexNumber = 3, ProductionYear = 2025 };
            Assert(TrayMenuPlan.Line(episode, paused: false) == "S1 · É3 · Le signal", TrayMenuPlan.Line(episode, false));
            Assert(TrayMenuPlan.Line(new MediaItem { Type = "Episode", Name = "Le signal", IndexNumber = 12 }, false) == "É12 · Le signal", "An episode without season (an anime)");
            Assert(TrayMenuPlan.Line(new MediaItem { Type = "Movie", Name = "Spider-Verse", ProductionYear = 2018 }, paused: true) == "2018 · En pause", "A film paused");
            Assert(TrayMenuPlan.Line(new MediaItem { Type = "Movie", Name = "Sans année" }, false) == "", "Nothing to say");
            Assert(TrayMenuPlan.Progress(822, 2412) is (var share, "13:42", "40:12") && Math.Abs(share - 822d / 2412) < 1e-9, "13:42 of 40:12");
            Assert(TrayMenuPlan.Progress(2890, 7020) is (_, "48:10", "1:57:00"), "Hours");
            Assert(TrayMenuPlan.Progress(-4, double.NaN) == (0, "0:00", "0:00") && TrayMenuPlan.Progress(90, 60) is (1, "1:00", "1:00"), "Out of bounds: clamped");
            return Task.CompletedTask;
        });

        await test("Menu de la zone de notification : au-dessus de la barre des tâches, dans l’écran, quel que soit le côté de la barre et l’échelle", () =>
        {
            ScreenFit.Pixels Work(double left, double top, double right, double bottom) => new(left, top, right - left, bottom - top);
            // Taskbar at the bottom of a 1920 × 1080 screen: centred on the icon, 10 px above the bar.
            var bottom = Work(0, 0, 1920, 1040);
            Assert(TrayMenuPlan.Place(1700, 1060, bottom, 320, 470, 1) == new ScreenFit.Pixels(1540, 560, 320, 470), "Bottom: " + TrayMenuPlan.Place(1700, 1060, bottom, 320, 470, 1));
            Assert(TrayMenuPlan.Place(1905, 1060, bottom, 320, 470, 1).Left == 1920 - 8 - 320, "The last icon, at the screen's edge: kept 8 px inside");
            Assert(TrayMenuPlan.Place(1700, 20, Work(0, 40, 1920, 1080), 320, 470, 1).Top == 50, "Taskbar at the top: under it");
            Assert(TrayMenuPlan.Place(30, 700, Work(60, 0, 1920, 1080), 320, 470, 1) == new ScreenFit.Pixels(70, 465, 320, 470), "Taskbar on the left: beside it, centred on the icon");
            Assert(TrayMenuPlan.Place(1890, 1000, Work(0, 0, 1860, 1080), 320, 470, 1) == new ScreenFit.Pixels(1530, 602, 320, 470), "Taskbar on the right, an icon low: kept on the screen");
            // A second screen at 150 %, right of the first: its own work area, gap and margin at its scale.
            var second = Work(1920, 0, 4480, 1400);
            Assert(TrayMenuPlan.Place(4300, 1430, second, 480, 705, 1.5) == new ScreenFit.Pixels(4480 - 12 - 480, 1400 - 15 - 705, 480, 705), "150 %: " + TrayMenuPlan.Place(4300, 1430, second, 480, 705, 1.5));
            // Opened from the overflow window or the keyboard, the pointer inside the work area: above the pointer.
            Assert(TrayMenuPlan.Place(800, 500, bottom, 320, 470, 1) == new ScreenFit.Pixels(640, 20, 320, 470), "Above the pointer");
            Assert(TrayMenuPlan.Place(800, 100, bottom, 320, 470, 1).Top == 8, "No room above: as high as the screen allows");
            Assert(TrayMenuPlan.Place(800, 1060, Work(0, 0, 1920, 400), 320, 470, 1).Top == 8, "Taller than the screen: its top stays on it");
            return Task.CompletedTask;
        });
    }
}
