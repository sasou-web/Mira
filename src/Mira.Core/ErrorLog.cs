namespace Mira.Core;

/// <summary>
/// errors.log in the profile: the type of an unexpected error and the method that failed, enough to find the fault.
/// Never the message: HTTP errors can contain credentials or private media paths. 256 KB at most.
/// </summary>
public static class ErrorLog
{
    public static void Append(string directory, Exception exception)
    {
        var site = exception.TargetSite is { } method ? $" {method.DeclaringType?.FullName}.{method.Name}" : "";
        Write(directory, $"{exception.GetType().Name}{site}");
    }
    /// <summary>Something Mira put right by itself, worth knowing when it comes back: names of types, never anything of the person's.</summary>
    public static void Note(string directory, string note) => Write(directory, note);
    private static void Write(string directory, string line)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var log = Path.Combine(directory, "errors.log");
            if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024) File.Move(log, log + ".old", true);
            File.AppendAllText(log, $"{DateTimeOffset.Now:O} {line}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
