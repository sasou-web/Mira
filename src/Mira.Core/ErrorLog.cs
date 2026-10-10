namespace Mira.Core;

/// <summary>
/// errors.log in the profile: the type of an unexpected error and the method that failed, enough to find the fault.
/// Never the message: HTTP errors can contain credentials or private media paths. 256 KB at most.
/// </summary>
public static class ErrorLog
{
    public static void Append(string directory, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var log = Path.Combine(directory, "errors.log");
            if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024) File.Move(log, log + ".old", true);
            var site = exception.TargetSite is { } method ? $" {method.DeclaringType?.FullName}.{method.Name}" : "";
            File.AppendAllText(log, $"{DateTimeOffset.Now:O} {exception.GetType().Name}{site}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
