using System.Windows;
using System.Windows.Media;
using Mira.Core;

namespace Mira.Desktop.Services;

public static class DemoLibrary
{
    public static List<MediaItem> Items() =>
    [
        Item("demo-1", L("Les heures bleues", "The Blue Hours"), "Movie", 2025, 118, [L("Aventure", "Adventure"), "Animation"], L("Sur une île oubliée, deux voyageurs suivent les lumières d’une forêt qui ne s’éveille qu’à la tombée du jour.", "On a forgotten island, two travelers follow the lights of a forest that only wakes at dusk."), 36),
        Item("demo-2", L("Au-delà des dunes", "Beyond the Dunes"), "Movie", 2024, 142, [L("Science-fiction", "Science Fiction")], L("Une traversée du désert, à la recherche de ce que le temps a laissé derrière lui.", "A journey across the desert, in search of what time left behind.")),
        Item("demo-3", L("La dernière orbite", "The Last Orbit"), "Series", 2025, 48, [L("Science-fiction", "Science Fiction")], L("Un signal inattendu traverse le silence. Pour l’équipage d’Aster, tout commence ici.", "An unexpected signal breaks the silence. For the crew of the Aster, it all begins here.")),
        Item("demo-4", L("Les jours d’été", "Summer Days"), "Movie", 2023, 96, [L("Drame", "Drama")], L("Un été sur la côte, des retrouvailles et toutes les choses qu’on n’avait pas su se dire.", "A summer on the coast, a reunion, and all the things that were never said."), 22),
        Item("demo-5", L("Là-haut, les étoiles", "Up Among the Stars"), "Series", 2025, 24, ["Animation"], L("Chaque nuit, une nouvelle constellation apparaît au-dessus du village.", "Every night, a new constellation appears above the village.")),
        Item("demo-6", L("Une autre rive", "Another Shore"), "Movie", 2024, 109, [L("Aventure", "Adventure")], L("Une cabane au bord de l’eau. Une lettre. Un départ qui change tout.", "A cabin by the water. A letter. A departure that changes everything.")),
        Item("demo-7", L("Après la pluie", "After the Rain"), "Movie", 2025, 103, [L("Drame", "Drama")], L("Les chemins de trois inconnus se croisent dans une ville en mouvement.", "Three strangers’ paths cross in a city that never stands still.")),
        Item("demo-8", L("Le chant du monde", "Song of the World"), "Series", 2024, 32, [L("Documentaire", "Documentary")], L("Des histoires de paysages et des voix de celles et ceux qui les habitent.", "Stories of landscapes, and the voices of the people who live in them."))
    ];
    private static MediaItem Item(string id, string title, string type, int year, int minutes, string[] genres, string overview, int progress = 0) => new()
    {
        Id = id,
        Name = title,
        Type = type,
        ProductionYear = year,
        Genres = genres,
        Overview = overview,
        RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        CommunityRating = 8.1f,
        UserData = new() { PlaybackPositionTicks = TimeSpan.FromMinutes(progress).Ticks, IsFavorite = id is "demo-1" or "demo-5" }
    };
    public static DrawingImage Artwork(int index, bool landscape = false)
    {
        string[][] palettes = [
            ["#16272D", "#487B7D", "#ABC5AA", "#162D39", "#0C1B26"],
            ["#302031", "#AD6557", "#FFD4A0", "#8C514E", "#33283C"],
            ["#141B3A", "#414D85", "#CBD3F4", "#2C345A", "#171B35"],
            ["#64445F", "#F6A378", "#FFE2A9", "#5C7380", "#294E60"],
            ["#181F39", "#506B99", "#E8CDA9", "#374465", "#202945"],
            ["#173434", "#729B89", "#ECDEAD", "#376963", "#1D4845"]
        ];
        var colors = palettes[Math.Abs(index) % palettes.Length];
        var w = landscape ? 1400d : 500d; var h = landscape ? 660d : 740d;
        Brush B(string c) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new LinearGradientBrush((Color)ColorConverter.ConvertFromString(colors[0]), (Color)ColorConverter.ConvertFromString(colors[1]), 90), null, new Rect(0, 0, w, h));
            var random = new Random(42 + index);
            for (var i = 0; i < 85; i++)
            {
                var x = random.NextDouble() * w; var y = random.NextDouble() * h * 0.66;
                dc.PushOpacity(random.NextDouble() * 0.45 + 0.08); dc.DrawEllipse(B("#E1E5EE"), null, new Point(x, y), 1.1, 1.1); dc.Pop();
            }
            dc.PushOpacity(0.09); dc.DrawEllipse(B(colors[2]), null, new Point(w * 0.70, h * 0.30), h * 0.26, h * 0.26); dc.Pop();
            dc.PushOpacity(0.14); dc.DrawEllipse(B(colors[2]), null, new Point(w * 0.70, h * 0.30), h * 0.18, h * 0.18); dc.Pop();
            dc.DrawEllipse(B(colors[2]), null, new Point(w * 0.70, h * 0.30), h * 0.09, h * 0.09);
            void Mountain(double baseline, double peak, Brush color, int seed)
            {
                var geometry = new StreamGeometry(); using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(0, h), true, true); ctx.LineTo(new Point(0, baseline), true, false);
                    var rng = new Random(seed);
                    for (int i = 1; i <= 12; i++) ctx.LineTo(new Point(w * i / 12, baseline - rng.NextDouble() * peak), true, false);
                    ctx.LineTo(new Point(w, h), true, false);
                }
                geometry.Freeze(); dc.DrawGeometry(color, null, geometry);
            }
            Mountain(h * 0.75, h * 0.24, B(colors[3]), index + 5);
            Mountain(h * 0.92, h * 0.25, B(colors[4]), index + 12);
            if (index % 3 == 0)
            {
                for (var i = 0; i < 22; i++)
                {
                    var x = w * i / 21; var y = h * (0.76 + random.NextDouble() * 0.12); var size = h * (0.08 + random.NextDouble() * 0.12);
                    var tree = Geometry.Parse($"M {x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{(y - size).ToString(System.Globalization.CultureInfo.InvariantCulture)} l {(-size * 0.23).ToString(System.Globalization.CultureInfo.InvariantCulture)},{size.ToString(System.Globalization.CultureInfo.InvariantCulture)} h {(size * 0.46).ToString(System.Globalization.CultureInfo.InvariantCulture)} Z");
                    dc.DrawGeometry(B("#102428"), null, tree);
                }
            }
            // A delicate orbit / horizon is vector art, rendered at the actual display resolution.
            dc.PushOpacity(0.25); dc.DrawLine(new Pen(B(colors[2]), 1), new Point(w * 0.12, h * 0.90), new Point(w * 0.88, h * 0.90)); dc.Pop();
        }
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
}
