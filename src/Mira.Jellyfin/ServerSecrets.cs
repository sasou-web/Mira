using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mira.Jellyfin;

/// <summary>
/// The extension's secrets on the server (the tailnet's OAuth client, the API keys and password of « Sous-titres
/// automatiques », the codes of the guests' links, the proof key): written beside the plugins' settings, never in
/// clear. On Windows each one is sealed by DPAPI for the account Jellyfin runs as (« dpapi:… »): a copy of the file, a
/// backup or another account of the PC reads nothing. Elsewhere the files are its account's alone (0600). A secret
/// written in clear before (0.9.15 and older) is still read, and sealed at the next save (<see cref="Plain"/>).
/// </summary>
public static class ServerSecrets
{
    public const string Prefix = "dpapi:";
    // Ties the sealed data to this use: DPAPI data of the same account made for something else does not open here.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mira.Jellyfin server secret v1");

    [ThreadStatic]
    private static int _plain;

    /// <summary>Whether a secret is sealed when written: only Windows has DPAPI.</summary>
    public static bool Sealing => OperatingSystem.IsWindows();

    /// <summary>The secret as it is written: sealed on Windows, as it is elsewhere; empty stays empty.</summary>
    public static string Seal(string value)
    {
        if (value.Length == 0 || !OperatingSystem.IsWindows())
        {
            return value;
        }

        return Prefix + Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(value), Entropy));
    }

    /// <summary>
    /// The secret as written read back: opened when sealed (null when it cannot be: another Windows account, another PC,
    /// damaged), as it is when in clear (counted for <see cref="Plain"/>).
    /// </summary>
    public static string? Open(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            _plain++;
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy));
        }
        catch (Exception error) when (error is FormatException or SealingFailure)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a secret was read in clear on this thread since the last call: the file is then written again, so that it
    /// is sealed (Windows) and its account's alone (elsewhere).
    /// </summary>
    public static bool Plain()
    {
        var plain = _plain > 0;
        _plain = 0;
        return plain;
    }

    /// <summary>
    /// Text written where only Jellyfin's account reads it: a file beside <paramref name="file"/>, made 0600 from its
    /// creation (not Windows), then moved over it at once (a reader never sees half of it).
    /// </summary>
    public static void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var next = file + ".next";
        File.Delete(next);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(next, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(text);
        }

        File.Move(next, file, true);
    }

    /// <summary>A file written before by anyone's rules made its account's alone (0600), not on Windows. Never throws.</summary>
    public static void Restrict(string? file)
    {
        if (file is null || OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (File.Exists(file) && (File.GetUnixFileMode(file) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not the owner of the file: left as it is.
        }
    }

    /// <summary>A string property kept sealed in its file (<see cref="Seal"/>, <see cref="Open"/>).</summary>
    public sealed class Sealed : JsonConverter<string>
    {
        public override bool HandleNull => false;

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String ? Open(reader.GetString()) : throw new JsonException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Seal(value));
    }

    /// <summary>DPAPI refused (the account has no profile loaded…): an IOException, so that a file's save keeps the change in memory, as for a full disk.</summary>
    private sealed class SealingFailure(int code) : IOException($"DPAPI failed ({code})");

    /// <summary>DPAPI by crypt32 itself: the plugin brings no library of its own (Jellyfin loads Mira.Jellyfin.dll only).</summary>
    [SupportedOSPlatform("windows")]
    private static class Dpapi
    {
        private const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("crypt32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, protect: true);

        public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

        private static byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            var input = GCHandle.Alloc(data, GCHandleType.Pinned);
            var salt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            var output = default(Blob);
            try
            {
                var inputBlob = new Blob { Size = data.Length, Data = input.AddrOfPinnedObject() };
                var saltBlob = new Blob { Size = entropy.Length, Data = salt.AddrOfPinnedObject() };
                var done = protect
                    ? CryptProtectData(ref inputBlob, "Mira", ref saltBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref saltBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!done)
                {
                    throw new SealingFailure(Marshal.GetLastWin32Error());
                }

                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    LocalFree(output.Data);
                }

                input.Free();
                salt.Free();
            }
        }
    }
}
