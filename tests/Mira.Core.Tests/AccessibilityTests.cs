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
            foreach (var background in new[] { "#000000", "#101010", "#111111", "#191919" })
                foreach (var text in new[] { "#8A8A90", "#A2A2A6" })
                    Assert(Legibility.Contrast(text, background) >= 4.5, $"{text} on {background}: {Legibility.Contrast(text, background):0.00}");
            Assert(Legibility.Contrast("#696972", "#000000") < 4.5 && Legibility.Contrast("#696972", "#111111") < 4.5, "The old faint grey is not caught");
            // Every opaque, light text colour written in the windows' markup and code: the text of a dark ink (a light
            // button) and the translucent ones (over artwork) are judged where they are drawn, not here.
            var repository = TestSupport.Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            var sources = new[] { "src/Mira.Desktop/MainWindow.xaml", "src/Mira.Desktop/Themes/Cinema.xaml", "src/Mira.Mac/App.axaml" }.Select(x => Path.Combine(repository, x))
                .Concat(Directory.EnumerateFiles(Path.Combine(repository, "src", "Mira.Desktop"), "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(Path.Combine(repository, "src", "Mira.Mac"), "*.cs", SearchOption.AllDirectories))
                .Where(x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
            var colour = new Regex("""(?:<(?:TextBlock|Run|TextBox)\b[^>]*?\bForeground="|Property="Foreground" Value="|x:Key="(?:Muted|Faint|Text|TextBody|TextSecondary|TextTertiary)" Color="|Foreground = (?:Brush|Frozen|Ui\.Brush)\("|(?:Muted|Faint) = Brush\(")(#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?)\b""");
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
            Assert(checkedCount > 15, "Too few text colours found: " + checkedCount);
            // The window's text is drawn from the palette (Themes/Cinema.xaml): its glyph and surface tokens are never text.
            var window = File.ReadAllText(Path.Combine(repository, "src/Mira.Desktop/MainWindow.xaml"));
            foreach (Match match in Regex.Matches(window, """<(?:TextBlock|Run|TextBox)\b[^>]*?\bForeground="\{(?:Static|Dynamic)Resource (Dim|Bg|Surface|Field|Raised|Raised2|Line|LineStrong)\}"""))
                faults.Add($"src/Mira.Desktop/MainWindow.xaml:{window[..match.Index].Count(x => x == '\n') + 1} text in {match.Groups[1].Value}");
            Assert(faults.Count == 0, "Text under 4.5:1 on Mira's backgrounds: " + string.Join(", ", faults));
            return Task.CompletedTask;
        });
        await test("Palette : chaque clé une seule fois dans Cinema.xaml et App.axaml (une clé en double fait planter Mira au démarrage, avant tout message)", () =>
        {
            var repository = TestSupport.Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            foreach (var file in new[] { "src/Mira.Desktop/Themes/Cinema.xaml", "src/Mira.Mac/App.axaml" })
            {
                var twice = Regex.Matches(File.ReadAllText(Path.Combine(repository, file)), "x:Key=\"([^\"]+)\"").Select(x => x.Groups[1].Value)
                    .GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
                Assert(twice.Count == 0, file + ": " + string.Join(", ", twice));
            }
            return Task.CompletedTask;
        });
        await test("Réglages en fenêtre : la recherche trouve sans casse ni accents, l’app derrière devient une image douce et sombre", () =>
        {
            Assert(SheetWindow.Matches("Langue des sous-titres préférée", "SOUS-TITRES") && SheetWindow.Matches("Réglages avancés", "reglage avance"), "Case and accents");
            Assert(SheetWindow.Matches("Taille et style des sous-titres", "style taille") && !SheetWindow.Matches("Taille des sous-titres", "taille couleur"), "Every word, in any order");
            Assert(SheetWindow.Matches("Reprendre là où tu t’étais arrêté", "t'étais") && SheetWindow.Matches("Œuvres", "oeuvre"), "Apostrophes and ligatures");
            Assert(SheetWindow.Matches("Lecture", "  ") && !SheetWindow.Matches("Lecture", "audio"), "Empty and missed searches");
            Assert(SheetWindow.Matches("Minimize to the notification area", "mini") && !SheetWindow.Matches("Administrators only", "mini") && SheetWindow.Matches("Built-in Tailscale", "scale") is false,
                "A word found at the start of a word, never inside one");
            // Explanations only find whole words, give or take an ending (Dell, 2026-11-15: « mini » found two « …even minimized »).
            Assert(!SheetWindow.Matches("Notify me of new episodes", "A Windows notification when an episode arrives. While Mira is open, even minimized.", "mini")
                && SheetWindow.Matches("Mini player", "Where it goes", "mini") && SheetWindow.Matches("Close button", "Puts Mira away by the clock while it works", "clock"),
                "Mini: the name, not the explanation");
            Assert(SheetWindow.Matches("Check", "Mira looks for updates once a day", "update") && SheetWindow.Matches("Episodes", "Notify me", "noti")
                && !SheetWindow.Matches("Server", "Configuration of Jellyfin", "config") && SheetWindow.Matches("Server", "Configuration of Jellyfin", "configuration"),
                "An ending or two in an explanation, never a word half typed");
            Assert(SheetWindow.Matches("Quality", "Lower it when the picture is blurry", "flou"), "Synonyms in explanations too");
            // The foot of the list goes while the sections do not fit, and comes back once they fit with it.
            Assert(!SheetWindow.FootShown(true, 520, 480, 50) && SheetWindow.FootShown(true, 470, 480, 50), "Shown: goes once the list overflows");
            Assert(!SheetWindow.FootShown(false, 520, 530, 50) && SheetWindow.FootShown(false, 470, 530, 50), "Gone: back only with room for it");
            Assert(SheetWindow.FootShown(true, 0, 0, 0) && !SheetWindow.FootShown(false, double.NaN, 400, 50), "Not laid out yet: as it was");
            // Enlarged smoothly: corners kept, steps between them in between.
            var two = new byte[] { 0, 0, 0, 255, 200, 200, 200, 255 };
            var wide = SheetWindow.Enlarge(two, 2, 1, 4);
            Assert(wide.Length == 8 * 4 * 4 && wide[0] == 0 && wide[7 * 4] == 200 && wide[3 * 4] > 0 && wide[3 * 4] < wide[4 * 4] && wide[4 * 4] < 200 && wide[3] == 255, string.Join(",", wide.Where((_, i) => i % 4 == 0)));
            // The account page's background: one picture, darker at the edges, no step between neighbours (no ring, no line).
            var plain = new byte[40 * 25 * 4];
            for (var i = 0; i < plain.Length; i++) plain[i] = 200;
            var page = SheetWindow.AccountBackdrop(plain, 40, 25, 300, 200, 1);
            int At(byte[] p, int x, int y) => p[(y * 300 + x) * 4];
            var steepest = 0;
            for (var y = 0; y < 200; y++) for (var x = 1; x < 300; x++) steepest = Math.Max(steepest, Math.Abs(At(page, x, y) - At(page, x - 1, y)));
            for (var x = 0; x < 300; x++) for (var y = 1; y < 200; y++) steepest = Math.Max(steepest, Math.Abs(At(page, x, y) - At(page, x, y - 1)));
            Assert(page.Length == 300 * 200 * 4 && page[3] == 255 && steepest <= 3, $"Smooth: {steepest}");
            Assert(At(page, 150, 70) > 40 && At(page, 150, 70) < 50 && At(page, 0, 199) < 10, $"Picture at a fifth in the middle, dark at the edge: {At(page, 150, 70)}, {At(page, 0, 199)}");
            var black = SheetWindow.AccountBackdrop(null, 0, 0, 300, 200, .25);
            Assert(At(black, 150, 100) is > 3 and < 18 && At(black, 0, 0) < 5, $"No picture: the faint light only: {At(black, 150, 100)}");
            // The person's own words (a friend could not find where Mira updates): any of a group finds the others.
            Assert(SheetWindow.Matches("Mises à jour Mira 0.9.8", "update") && SheetWindow.Matches("Updates Check for updates", "mettre à jour") && SheetWindow.Matches("Updates", "Nouvelle version"), "Updates, in both languages");
            Assert(SheetWindow.Matches("Quality Automatic", "blurry") && SheetWindow.Matches("Built-in Tailscale", "away from home") && SheetWindow.Matches("Sign out or switch account", "déconnexion"), "Blurry, away from home, sign out");
            Assert(SheetWindow.Matches("Updates", "upd") && !SheetWindow.Matches("Quality", "update") && !SheetWindow.Matches("Mises à jour", "update quality"), "Words being typed, and every word still counts");
            foreach (var group in SheetWindow.Synonyms) foreach (var phrase in group) Assert(phrase == SheetWindow.Fold(phrase), "A synonym is written folded: " + phrase);
            // A white dot in the dark spreads into its neighbours and stays as bright in all, a little dimmer than it was.
            const int size = 15;
            var pixels = new byte[size * size * 4];
            var middle = (7 * size + 7) * 4; pixels[middle] = pixels[middle + 1] = pixels[middle + 2] = pixels[middle + 3] = 255;
            SheetWindow.Soften(pixels, size, size);
            var total = 0;
            for (var i = 0; i < pixels.Length; i += 4) { total += pixels[i]; Assert(pixels[i + 3] == 255, "The picture is opaque"); }
            Assert(pixels[middle] > 0 && pixels[middle] < 40 && pixels[(7 * size + 9) * 4] > 0, $"The dot is blurred: {pixels[middle]}");
            Assert(Math.Abs(total - 255 * .62) < 40, $"The light is kept, dimmed: {total}");
            // A red picture keeps its colour, a little less of it.
            var red = new byte[4 * 4 * 4];
            for (var i = 0; i < red.Length; i += 4) { red[i + 2] = 200; red[i + 3] = 255; }
            SheetWindow.Soften(red, 4, 4);
            Assert(red[2] > red[1] + 60 && red[2] < 200 && red[1] > 0, $"Colour: {red[2]}, {red[1]}");
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
}
