using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// Jellyfin's folder (/jellyfin in its Docker image), then the plugins' assemblies. Every method with a body is
// compiled (RuntimeHelpers.PrepareMethod): a call or a field the server lacks throws MissingMethodException or
// MissingFieldException, a type it lacks TypeLoadException. Generic methods are left out (compiled per type argument).
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: mira-bindings <Jellyfin's folder> <plugin.dll>...");
    return 2;
}
var server = Path.GetFullPath(args[0]);
if (!File.Exists(Path.Combine(server, "MediaBrowser.Controller.dll")))
{
    Console.Error.WriteLine($"{server}: no MediaBrowser.Controller.dll");
    return 2;
}
// The runtime's own assemblies first (the default context), then Jellyfin's, as Jellyfin resolves a plugin's.
var context = new AssemblyLoadContext("jellyfin");
context.Resolving += (c, name) => Path.Combine(server, name.Name + ".dll") is var path && File.Exists(path) ? c.LoadFromAssemblyPath(path) : null;
const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
var failures = 0;
foreach (var plugin in args.Skip(1))
{
    var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(plugin));
    Type?[] types;
    try { types = assembly.GetTypes(); }
    catch (ReflectionTypeLoadException ex)
    {
        types = ex.Types;
        foreach (var error in ex.LoaderExceptions.Where(x => x is not null).Select(x => x!.Message).Distinct()) { Console.WriteLine($"{assembly.GetName().Name}: {error}"); failures++; }
    }
    var compiled = 0;
    foreach (var type in types.OfType<Type>())
    {
        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) continue;
            try { RuntimeHelpers.PrepareMethod(method.MethodHandle); compiled++; }
            catch (Exception ex) when (ex is MissingMemberException or TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException or InvalidProgramException)
            {
                Console.WriteLine($"{type.FullName}.{method.Name}: {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }
    }
    Console.WriteLine($"{assembly.GetName().Name}: {compiled} methods compiled against {server}");
}
Console.WriteLine(failures == 0 ? "Every call found in this Jellyfin." : $"{failures} failing: the plugin breaks on this Jellyfin.");
return failures == 0 ? 0 : 1;
