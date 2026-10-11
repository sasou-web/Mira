using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Mira.Core;

/// <summary>
/// Readable for everyone: the contrast of Mira's text colours on Windows and on Mac, how long a message stays, the zoom
/// that follows Windows' text size, the arrow keys among cards and Jellyfin's errors put into words. Free of WPF, so that
/// it also runs outside Windows; the screen reader names are checked in Program.cs.
/// </summary>
internal static class AccessibilityTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Lisibilité : contraste d’au moins 4,5:1 pour chaque couleur de texte, sur Windows et sur Mac", () =>
        {
            Assert(Math.Abs(Legibility.Contrast("#FFFFFF", "#000000") - 21) < .01 && Math.Abs(Legibility.Contrast("#000000", "#000000") - 1) < .01, "WCAG ratio of white and black");
            Assert(Math.Abs(Legibility.Contrast("#767676", "#FFFFFF") - 4.54) < .01, "WCAG reference grey");
            // A translucent text is blended over its background: half-transparent white on black is a mid grey.
            Assert(Math.Abs(Legibility.Contrast("#80FFFFFF", "#000000") - Legibility.Contrast("#808080", "#000000")) < .05, "Translucent text not blended");
            // The darkest backgrounds Mira draws text on, and its secondary text: the old greys were under the floor.
            foreach (var background in new[] { "#070708", "#101011", "#111112", "#101012" })
                foreach (var text in new[] { "#8A8A90", "#A2A2A6" })
                    Assert(Legibility.Contrast(text, background) >= 4.5, $"{text} on {background}: {Legibility.Contrast(text, background):0.00}");
            Assert(Legibility.Contrast("#696972", "#070708") < 4.5, "The old faint grey is not caught");
            // Every opaque, light text colour written in the windows' markup and code: the text of a dark ink (a light
            // button) and the translucent ones (over artwork) are judged where they are drawn, not here.
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            var sources = new[] { "src/Mira.Desktop/MainWindow.xaml", "src/Mira.Desktop/Themes/Cinema.xaml", "src/Mira.Mac/App.axaml" }.Select(x => Path.Combine(repository, x))
                .Concat(Directory.EnumerateFiles(Path.Combine(repository, "src", "Mira.Desktop"), "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(Path.Combine(repository, "src", "Mira.Mac"), "*.cs", SearchOption.AllDirectories))
                .Where(x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
            var colour = new Regex("""(?:<(?:TextBlock|Run|TextBox)\b[^>]*?\bForeground="|Property="Foreground" Value="|x:Key="(?:Muted|Faint|Text|TextSecondary)" Color="|Foreground = (?:Brush|Frozen|Ui\.Brush)\("|(?:Muted|Faint) = Brush\(")(#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?)\b""");
            var checkedCount = 0; var faults = new List<string>();
            foreach (var file in sources)
            {
                var source = File.ReadAllText(file);
                foreach (Match match in colour.Matches(source))
                {
                    var hex = match.Groups[1].Value;
                    if (hex.Length == 9 && !hex[1..3].Equals("FF", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Legibility.Contrast(hex, "#000000") < 2.1) continue;
                    checkedCount++;
                    if (Legibility.Contrast(hex, "#111113") < 4.5)
                        faults.Add($"{Path.GetRelativePath(repository, file)}:{source[..match.Index].Count(x => x == '\n') + 1} {hex} ({Legibility.Contrast(hex, "#111113"):0.00})");
                }
            }
            Assert(checkedCount > 50, "Too few text colours found: " + checkedCount);
            Assert(faults.Count == 0, "Text under 4.5:1 on Mira's backgrounds: " + string.Join(", ", faults));
            return Task.CompletedTask;
        });
        await test("Lisibilité : un message reste le temps d’être lu, la taille du texte de Windows agrandit les pages", () =>
        {
            Assert(Legibility.NoticeDuration("Ajouté.") == TimeSpan.FromSeconds(6), "Short message");
            Assert(Legibility.NoticeDuration(new string('a', 120)) == TimeSpan.FromSeconds(11), "Message of 120 characters");
            Assert(Legibility.NoticeDuration(new string('a', 2000)) == TimeSpan.FromSeconds(20), "Long message not capped");
            Assert(Legibility.Zoom(1, 1920, 1080, 960, 600) == 1 && Legibility.Zoom(.9, 1920, 1080, 960, 600) == 1 && Legibility.Zoom(double.NaN, 1920, 1080, 960, 600) == 1, "Zoom without a larger text");
            Assert(Legibility.Zoom(1.5, 1920, 1080, 960, 600) == 1.5, "150 % on a large window");
            // The window holds 960 × 600 at most 1.33 times over: the zoom stops there, rounded down.
            Assert(Legibility.Zoom(2, 1280, 900, 960, 600) == 1.33, "Zoom capped by the window's width: " + Legibility.Zoom(2, 1280, 900, 960, 600));
            Assert(Legibility.Zoom(3, 4000, 4000, 960, 600) == 2.25, "Zoom over Windows' maximum");
            Assert(Legibility.Zoom(1.5, 900, 500, 960, 600) == 1 && Legibility.Zoom(1.5, 0, 0, 960, 600) == 1, "A small window never shrinks the pages");
            return Task.CompletedTask;
        });
        await test("Clavier : les flèches passent d’une affiche à sa voisine, de rangée en rangée", () =>
        {
            // Two rows of three posters, then one alone, all 100 × 150 with 20 between them.
            var grid = new List<CardGrid.Box>();
            for (var i = 0; i < 7; i++) grid.Add(new(i % 3 * 120, i / 3 * 170, 100, 150));
            Assert(CardGrid.Neighbour(grid, 0, CardGrid.Direction.Right) == 1 && CardGrid.Neighbour(grid, 3, CardGrid.Direction.Left) == 2, "Left and right follow reading order");
            Assert(CardGrid.Neighbour(grid, 0, CardGrid.Direction.Left) is null && CardGrid.Neighbour(grid, 6, CardGrid.Direction.Right) is null, "Edges");
            Assert(CardGrid.Neighbour(grid, 1, CardGrid.Direction.Down) == 4 && CardGrid.Neighbour(grid, 5, CardGrid.Direction.Up) == 2, "Up and down keep the column");
            // The last row is shorter: down from the right column lands on the nearest card there.
            Assert(CardGrid.Neighbour(grid, 5, CardGrid.Direction.Down) == 6, "Down to a shorter row");
            Assert(CardGrid.Neighbour(grid, 1, CardGrid.Direction.Up) is null && CardGrid.Neighbour(grid, 6, CardGrid.Direction.Down) is null, "Top and bottom edges");
            // A single row (« Continuer à regarder »): up and down leave it to the page.
            var row = new List<CardGrid.Box> { new(0, 0, 300, 170), new(320, 0, 300, 170) };
            Assert(CardGrid.Neighbour(row, 0, CardGrid.Direction.Down) is null && CardGrid.Neighbour(row, 1, CardGrid.Direction.Up) is null, "A row has no row below");
            Assert(CardGrid.Neighbour(row, 5, CardGrid.Direction.Right) is null && CardGrid.Neighbour([], 0, CardGrid.Direction.Up) is null, "Out of range");
            return Task.CompletedTask;
        });
        await test("Messages : une erreur de Jellyfin est dite en mots, avec ce que tu peux faire", () =>
        {
            Assert(JellyfinClient.StatusMessage(HttpStatusCode.ServiceUnavailable).Contains("démarre"), "503");
            Assert(JellyfinClient.StatusMessage(HttpStatusCode.NotFound).Contains("retiré"), "404");
            Assert(JellyfinClient.StatusMessage(HttpStatusCode.InternalServerError) is var error && error.Contains("code 500") && error.Contains("Journaux"), "500");
            Assert(JellyfinClient.StatusMessage(HttpStatusCode.Conflict).Contains("code 409"), "409");
            foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.NotFound, HttpStatusCode.BadGateway, HttpStatusCode.Conflict })
                Assert(!JellyfinClient.StatusMessage(status).Contains("status code", StringComparison.OrdinalIgnoreCase), "English status text");
            return Task.CompletedTask;
        });
    }

    private static string? Repository()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
                if (File.Exists(Path.Combine(folder.FullName, "Mira.sln"))) return folder.FullName;
        return null;
    }
}
