using System.IO;

/// <summary>
/// Runs Mira's checks one after another: a failing check is reported with its reason and the next one still runs; the
/// summary at the end counts both, and the exit code is 1 when one failed. Options:
///   --filter &lt;text&gt;   only the checks whose name contains this text (case ignored; repeat it for several)
///   --list            prints the names of the checks the filter keeps, without running them
/// </summary>
internal sealed class TestRun
{
    private readonly List<string> _filters = [];
    private readonly List<string> _failures = [];
    private readonly bool _list;
    private int _passed, _skipped;

    /// <summary>A folder of its own for this run, under .artifacts/tests.</summary>
    public string Root { get; }

    public TestRun(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--filter" && i + 1 < args.Length) _filters.Add(args[++i]);
            else if (args[i] == "--list") _list = true;
        }
        Root = Path.GetFullPath(Path.Combine(".artifacts", "tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Root);
    }

    public async Task Test(string name, Func<Task> body)
    {
        if (_filters.Count > 0 && !_filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) { _skipped++; return; }
        if (_list) { _passed++; Console.WriteLine(name); return; }
        try
        {
            await body();
            _passed++; Console.WriteLine("PASS  " + name);
        }
        catch (Exception ex)
        {
            _failures.Add(name);
            Console.WriteLine("FAIL  " + name);
            Console.WriteLine("      " + ex.ToString().Replace("\n", "\n      "));
        }
    }

    /// <summary>Prints the summary; returns the process exit code (1 when a check failed or the filter kept none).</summary>
    public int Finish()
    {
        if (_list) { Console.WriteLine($"\n{_passed} test(s)."); return _passed > 0 ? 0 : 1; }
        Console.WriteLine();
        if (_failures.Count > 0)
        {
            Console.WriteLine($"{_failures.Count} test(s) en échec :");
            foreach (var name in _failures) Console.WriteLine("  - " + name);
        }
        Console.WriteLine($"{_passed} test(s) réussi(s), {_failures.Count} en échec" + (_skipped > 0 ? $", {_skipped} hors du filtre." : "."));
        if (_passed + _failures.Count == 0) { Console.WriteLine("Aucun test ne correspond au filtre."); return 1; }
        return _failures.Count > 0 ? 1 : 0;
    }
}
