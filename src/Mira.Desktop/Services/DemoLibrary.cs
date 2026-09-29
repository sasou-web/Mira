using System.Windows;
using System.Windows.Media;
using Mira.Core;

namespace Mira.Desktop.Services;

public static class DemoLibrary
{
    public static List<MediaItem> Items() =>
    [
        Item("demo-1", "Les heures bleues", "Movie", 2025, 118, ["Aventure", "Animation"], "Sur une île oubliée, deux voyageurs suivent les lumières d’une forêt qui ne s’éveille qu’à la tombée du jour.", 36),
        Item("demo-2", "Au-delà des dunes", "Movie", 2024, 142, ["Science-fiction"], "Une traversée du désert, à la recherche de ce que le temps a laissé derrière lui."),
        Item("demo-3", "La dernière orbite", "Series", 2025, 48, ["Science-fiction"], "Un signal inattendu traverse le silence. Pour l’équipage d’Aster, tout commence ici."),
        Item("demo-4", "Les jours d’été", "Movie", 2023, 96, ["Drame"], "Un été sur la côte, des retrouvailles et toutes les choses qu’on n’avait pas su se dire.", 22),
        Item("demo-5", "Là-haut, les étoiles", "Series", 2025, 24, ["Animation"], "Chaque nuit, une nouvelle constellation apparaît au-dessus du village."),
        Item("demo-6", "Une autre rive", "Movie", 2024, 109, ["Aventure"], "Une cabane au bord de l’eau. Une lettre. Un départ qui change tout."),
        Item("demo-7", "Après la pluie", "Movie", 2025, 103, ["Drame"], "Les chemins de trois inconnus se croisent dans une ville en mouvement."),
        Item("demo-8", "Le chant du monde", "Series", 2024, 32, ["Documentaire"], "Des histoires de paysages et des voix de celles et ceux qui les habitent.")
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
