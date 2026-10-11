using Microsoft.Data.Sqlite;

namespace Mira.Core;

/// <summary>
/// Something the person typed cannot be used (an empty name, an address that is not one); the message is written for
/// them. The only ArgumentException the apps expect: any other is a fault in Mira, logged and reported, never hidden.
/// </summary>
public sealed class UserInputException(string message) : ArgumentException(message);

/// <summary>The failures Mira expects and words for the person, on Windows and on Mac: the network, the disk, the input.</summary>
public static class ExpectedErrors
{
    /// <summary>
    /// The network (HttpClient's errors and its time limit), the disk and the local cache, a refused file, JSON that does
    /// not read, and a refused input. Not ArgumentException itself, nor ArgumentNullException or
    /// ArgumentOutOfRangeException: those are bugs.
    /// </summary>
    public static bool Are(Exception e) => e is HttpRequestException or IOException or UnauthorizedAccessException or UserInputException
        or OperationCanceledException or System.Text.Json.JsonException or SqliteException;
}
