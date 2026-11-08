using Mira.Core;

/// <summary>« Mode canapé » of Mira for Windows and Mac: the zoom on a TV, where an arrow goes, and a game controller read as presses.</summary>
internal static class CouchTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Mode canapé : les pages agrandies comme sur une fenêtre de 1280 × 720", () =>
        {
            Assert(Couch.Zoom(1920, 1080) == 1.5, "A TV at 1080p: " + Couch.Zoom(1920, 1080));
            // 2560 × 1440 (a 4K TV at 150 %): twice; a 21:9 screen follows its height.
            Assert(Couch.Zoom(2560, 1440) == 2 && Couch.Zoom(2560, 1080) == 1.5, "Larger screens: " + Couch.Zoom(2560, 1080));
            Assert(Couch.Zoom(1280, 720) == 1 && Couch.Zoom(1000, 700) == 1, "Never smaller than made");
            Assert(Couch.Zoom(7680, 4320) == 2.5 && Couch.Zoom(0, 1080) == 1 && Couch.Zoom(double.NaN, 1080) == 1, "Capped, and unknown sizes");
            // Rounded down: never past the 1280 × 720 layout.
            Assert(Couch.Zoom(1366, 768) == 1.06, "1366 × 768: " + Couch.Zoom(1366, 768));
            return Task.CompletedTask;
        });
        await test("Mode canapé : les flèches vont au plus proche dans leur direction, rail et rangées compris", () =>
        {
            // A rail of three buttons at the left, a play button, then two rows of posters, the second further down the page.
            var rail = new CardGrid.Box[] { new(10, 100, 48, 48), new(10, 160, 48, 48), new(10, 600, 48, 48) };
            var play = new CardGrid.Box(110, 300, 160, 48);
            var row1 = Enumerable.Range(0, 6).Select(i => new CardGrid.Box(110 + i * 200, 420, 180, 100)).ToArray();
            var row2 = Enumerable.Range(0, 3).Select(i => new CardGrid.Box(110 + i * 200, 580, 180, 270)).ToArray();
            var all = rail.Append(play).Concat(row1).Concat(row2).ToList();
            int Index(CardGrid.Box box) => all.IndexOf(box);
            int? Go(CardGrid.Box from, CardGrid.Direction way) => Couch.Next(all, from, way);
            Assert(Go(play, CardGrid.Direction.Down) == Index(row1[0]), "Play → the first poster below it");
            Assert(Go(row1[0], CardGrid.Direction.Right) == Index(row1[1]) && Go(row1[1], CardGrid.Direction.Left) == Index(row1[0]), "Along a row");
            Assert(Go(row1[2], CardGrid.Direction.Down) == Index(row2[2]) && Go(row2[1], CardGrid.Direction.Up) == Index(row1[1]), "Row to row, same column");
            // Down from the end of a long row: the nearest poster of the shorter row below.
            Assert(Go(row1[5], CardGrid.Direction.Down) == Index(row2[2]), "Down from past the end of the row below");
            // Left from the first poster: the rail, its button nearest in height even outside the row's lane.
            Assert(Go(row1[0], CardGrid.Direction.Left) is { } toRail && rail.Contains(all[toRail]), "First poster ← the rail");
            Assert(Go(row2[0], CardGrid.Direction.Left) == Index(rail[2]), "The rail's button beside the second row: " + Go(row2[0], CardGrid.Direction.Left));
            Assert(Go(rail[0], CardGrid.Direction.Down) == Index(rail[1]) && Go(rail[1], CardGrid.Direction.Up) == Index(rail[0]), "Up and down the rail");
            Assert(Go(rail[1], CardGrid.Direction.Right) == Index(play) || Go(rail[1], CardGrid.Direction.Right) == Index(row1[0]), "The rail → the page");
            // The edges: nothing past the last poster, above the rail, below the last row.
            Assert(Go(row1[5], CardGrid.Direction.Right) is null && Go(rail[0], CardGrid.Direction.Up) is null && Go(row2[1], CardGrid.Direction.Down) is null && Go(rail[0], CardGrid.Direction.Left) is null, "Edges");
            // The control focused may be in the list, or not; a box without a size is never chosen.
            Assert(Couch.Next([new(0, 0, 0, 0), row1[1]], row1[0], CardGrid.Direction.Right) == 1 && Couch.Next([], play, CardGrid.Direction.Up) is null, "Odd lists");
            // A button half over another (a picture's play button) is beside it, never past it.
            Assert(Couch.Next([new(130, 0, 100, 100)], new(100, 0, 100, 100), CardGrid.Direction.Right) is null, "Overlapping by half");
            return Task.CompletedTask;
        });
        await test("Mode canapé : le focus part du premier contrôle en haut à gauche", () =>
        {
            var boxes = new CardGrid.Box[] { new(300, 106, 40, 40), new(100, 100, 40, 40), new(10, 400, 40, 40), new(0, 0, 0, 0) };
            Assert(Couch.First(boxes) == 1, "Same row read from the left: " + Couch.First(boxes));
            Assert(Couch.First([new(0, 0, 0, 0)]) is null && Couch.First([]) is null, "Nothing to focus");
            return Task.CompletedTask;
        });
        await test("Mode canapé : la manette appuie une fois par bouton, les directions se répètent", () =>
        {
            var reader = new PadReader();
            TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);
            Assert(reader.Read(new(Pad.None), At(0)).Count == 0, "Nothing held");
            Assert(reader.Read(new(Pad.A), At(16)).SequenceEqual([Pad.A]) && reader.Read(new(Pad.A), At(32)).Count == 0, "A held counts once");
            Assert(reader.Read(new(Pad.None), At(48)).Count == 0 && reader.Read(new(Pad.A | Pad.B), At(64)).SequenceEqual([Pad.A, Pad.B]), "Pressed again, with B");
            // The cross held: once, then again after 400 ms, then every 110 ms.
            reader = new PadReader();
            var downs = new List<int>();
            for (var ms = 0; ms <= 700; ms += 10) if (reader.Read(new(Pad.Down), At(ms)).Contains(Pad.Down)) downs.Add(ms);
            Assert(downs.SequenceEqual([0, 400, 510, 620]), "Repeats: " + string.Join(", ", downs));
            // The stick past half way, its larger axis; under half, nothing.
            reader = new PadReader();
            Assert(reader.Read(new(Pad.None, .3, .4), At(0)).Count == 0 && reader.Read(new(Pad.None, .9, -.4), At(10)).SequenceEqual([Pad.Right]), "Stick right");
            Assert(reader.Read(new(Pad.None, .2, -.8), At(20)).SequenceEqual([Pad.Down]), "Stick turned down");
            Assert(reader.Read(new(Pad.Up, .2, -.8), At(30)).SequenceEqual([Pad.Up]), "The cross before the stick");
            // Back from another window with A still held: not pressed again.
            reader.Reset(new(Pad.A | Pad.Up));
            Assert(reader.Read(new(Pad.A | Pad.Up), At(2000)).Count == 0 && reader.Read(new(Pad.Up), At(5000)).Count == 0, "Reset keeps what is held");
            return Task.CompletedTask;
        });
        await test("Mode canapé : chaque bouton de la manette, dans la bibliothèque et dans le lecteur", () =>
        {
            Assert(Couch.For(Pad.A, playing: false) == Couch.Action.Select && Couch.For(Pad.A, playing: true) == Couch.Action.PlayPause, "A");
            Assert(Couch.For(Pad.B, false) == Couch.Action.Back && Couch.For(Pad.View, true) == Couch.Action.Back, "B and ⧉ go back");
            Assert(Couch.For(Pad.Y, false) == Couch.Action.Search && Couch.For(Pad.Y, true) == Couch.Action.Skip, "Y");
            Assert(Couch.For(Pad.X, true) == Couch.Action.Subtitles && Couch.For(Pad.X, false) is null && Couch.For(Pad.Menu, false) is null, "X and ☰");
            Assert(Couch.For(Pad.LeftShoulder, false) == Couch.Action.PreviousPage && Couch.For(Pad.RightShoulder, true) == Couch.Action.JumpForward, "Shoulders");
            Assert(Couch.For(Pad.Left, true) == Couch.Action.Left && Couch.For(Pad.None, false) is null, "Directions");
            // Windows' XInput: A and the cross's left, the stick past its dead zone; within it, still.
            Assert(Couch.FromXInput(0x1004, 0, 0) == new PadState(Pad.A | Pad.Left), "XInput buttons");
            Assert(Couch.FromXInput(0x0030, 32767, -32768) is { Buttons: Pad.Menu | Pad.View, StickX: 1, StickY: -1 } && Couch.FromXInput(0, 7000, -7000) is { StickX: 0, StickY: 0 }, "XInput stick");
            Assert(Couch.Title == "Mode canapé" && Couch.Keys.Contains("Retour arrière"), "French texts");
            return Task.CompletedTask;
        });
    }
}
