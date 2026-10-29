using System.IO;
using System.Security.Cryptography;
using Mira.Desktop.Services;

/// <summary>The checks of mira-tailscale.exe that need no Windows: the file Mira runs is the one it was built with.</summary>
internal static class TailnetExecutableTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(TestRun run)
    {
        await run.Test("Tailscale intégré : mira-tailscale.exe reconnu signé ou non, refusé modifié", () =>
        {
            // A PE32+ image as Go writes it: checksum zero, no certificate table, a length that is a multiple of 512.
            var built = new byte[2048];
            "MZ"u8.CopyTo(built); BitConverter.GetBytes(0x80).CopyTo(built, 0x3c);
            "PE\0\0"u8.CopyTo(built.AsSpan(0x80)); BitConverter.GetBytes((ushort)0x20b).CopyTo(built, 0x80 + 24);
            for (var i = 0x400; i < built.Length; i++) built[i] = (byte)(i * 7);
            string Digest(byte[] bytes) { using var stream = new MemoryStream(bytes); return TailnetExecutable.Digest(stream); }
            var expected = Convert.ToHexStringLower(SHA256.HashData(built));
            Assert(Digest(built) == expected, "An unsigned build does not have its plain SHA-256");
            // Signed since (SignPath): a checksum, the certificate table's entry, the signature appended.
            var signature = new byte[64]; signature.AsSpan().Fill(0x5a);
            var signed = built.Concat(signature).ToArray();
            BitConverter.GetBytes(0x12345u).CopyTo(signed, 0x80 + 24 + 64);
            BitConverter.GetBytes((uint)built.Length).CopyTo(signed, 0x80 + 24 + 112 + 4 * 8); BitConverter.GetBytes((uint)signature.Length).CopyTo(signed, 0x80 + 24 + 112 + 4 * 8 + 4);
            Assert(Digest(signed) == expected, "The same file once signed was not recognised");
            var changed = signed.ToArray(); changed[0x500] ^= 1;
            Assert(Digest(changed) != expected, "A changed byte of the code went unnoticed");
            var other = built.ToArray(); other[0x700] ^= 1;
            Assert(Digest(other) != expected, "Another build went unnoticed");
            // Not a PE at all: every byte counts.
            var text = "pas un exécutable"u8.ToArray();
            Assert(Digest(text) == Convert.ToHexStringLower(SHA256.HashData(text)), "A file that is not a PE was not hashed whole");
            Assert(TailnetExecutable.Blocked(new System.ComponentModel.Win32Exception(225)) && TailnetExecutable.Blocked(new IOException("virus", unchecked((int)0x800700E1))) && !TailnetExecutable.Blocked(new IOException("busy", unchecked((int)0x80070020))),
                "Windows' « virus » refusals are not told from other errors");
            return Task.CompletedTask;
        });
    }
}
