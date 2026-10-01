using System.Security.Cryptography;
using System.Text;
using Mira.Core;
using Mira.Core.Updates;

// Signing of Mira's automatic updates (see docs/SECURITY.md and CONTRIBUTING.md).
//   keygen [--key <file>]                                 creates the signing key; never overwrites one
//   public-key [--key <file>]                             prints the public key to list in UpdateKeys.Trusted
//   manifest --version x.y.z --packages <dir> [--key <file>]   writes and signs mira-update.json for the release files
//   notes --version x.y.z --out <file>                    the release page's text, from the version's entry in WhatsNew.json
//   export-backup --out <file> [--key <file>]             copy protected by a passphrase (PKCS#8, AES-256, PBKDF2)
//   import-backup --in <file> [--key <file>]              restores that copy for the current Windows account
// The key file is encrypted by Windows (DPAPI) for the current account. It never belongs in the repository.
var defaultKey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mira Release", "update-signing.key");
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var keyPath = Path.GetFullPath(Arg("--key") ?? defaultKey);
var entropy = "Mira update signing key"u8.ToArray();

ECDsa Load()
{
    if (!File.Exists(keyPath)) throw new InvalidOperationException($"Clé de signature introuvable : {keyPath}. Crée-la avec « keygen » ou restaure-la avec « import-backup ».");
    var key = ECDsa.Create();
    key.ImportPkcs8PrivateKey(ProtectedData.Unprotect(File.ReadAllBytes(keyPath), entropy, DataProtectionScope.CurrentUser), out _);
    if (key.KeySize != 256) throw new InvalidOperationException("La clé de signature n’est pas une clé P-256.");
    return key;
}
void Save(ECDsa key)
{
    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
    using var file = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    file.Write(ProtectedData.Protect(key.ExportPkcs8PrivateKey(), entropy, DataProtectionScope.CurrentUser));
}
string Passphrase(string prompt)
{
    Console.Error.Write(prompt);
    var text = new StringBuilder();
    for (ConsoleKeyInfo key; (key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter;)
        if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; } else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
    Console.Error.WriteLine();
    return text.ToString();
}

try
{
    switch (args.FirstOrDefault())
    {
        case "keygen":
        {
            if (File.Exists(keyPath)) throw new InvalidOperationException($"Une clé existe déjà : {keyPath}. Elle n’est jamais remplacée.");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Save(key);
            Console.Error.WriteLine($"Clé créée : {keyPath} (chiffrée pour ce compte Windows). Fais-en une copie de secours avec « export-backup ».");
            Console.WriteLine(UpdateSignature.PublicKey(key));
            return 0;
        }
        case "public-key":
        {
            using var key = Load();
            Console.WriteLine(UpdateSignature.PublicKey(key));
            return 0;
        }
        case "manifest":
        {
            var version = ReleaseFeed.ParseVersion(Arg("--version")) ?? throw new InvalidOperationException("Indique --version x.y.z.");
            var packages = Path.GetFullPath(Arg("--packages") ?? throw new InvalidOperationException("Indique --packages <dossier>."));
            using var key = Load();
            var manifest = UpdateManifest.Create(version, packages);
            var json = manifest.ToJson();
            var signature = UpdateSignature.Sign(json, key);
            var publicKey = UpdateSignature.PublicKey(key);
            if (!UpdateSignature.Verify(json, signature, [publicKey])) throw new InvalidOperationException("La signature produite ne se vérifie pas.");
            File.WriteAllBytes(Path.Combine(packages, UpdateManifest.AssetName), json);
            File.WriteAllText(Path.Combine(packages, UpdateManifest.SignatureName), UpdateSignature.Encode(signature), Encoding.ASCII);
            foreach (var file in manifest.Files) Console.Error.WriteLine($"  {file.Kind,-9} {file.Name}  {file.Size} octets  {file.Sha256}");
            if (!UpdateKeys.Trusted.Contains(publicKey)) Console.Error.WriteLine("Attention : cette clé n’est pas dans UpdateKeys.Trusted ; les copies de Mira construites depuis ce code refuseront ce manifeste.");
            Console.WriteLine(Path.Combine(packages, UpdateManifest.AssetName));
            Console.WriteLine(Path.Combine(packages, UpdateManifest.SignatureName));
            return 0;
        }
        case "notes":
        {
            var version = Arg("--version") ?? throw new InvalidOperationException("Indique --version x.y.z.");
            var output = Path.GetFullPath(Arg("--out") ?? throw new InvalidOperationException("Indique --out <fichier>."));
            var release = WhatsNew.All.FirstOrDefault(x => x.Version == version)
                ?? throw new InvalidOperationException($"src/Mira.Core/WhatsNew.json n’a pas d’entrée pour {version} : écris ses points forts avant de publier.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, WhatsNew.ReleaseNotes(release), new UTF8Encoding(false));
            Console.WriteLine(output);
            return 0;
        }
        case "export-backup":
        {
            var output = Path.GetFullPath(Arg("--out") ?? throw new InvalidOperationException("Indique --out <fichier>."));
            using var key = Load();
            var passphrase = Passphrase("Phrase secrète de la copie (12 caractères ou plus) : ");
            if (passphrase.Length < 12) throw new InvalidOperationException("Phrase secrète trop courte.");
            if (Passphrase("Confirme la phrase secrète : ") != passphrase) throw new InvalidOperationException("Les deux saisies diffèrent.");
            var pem = key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
            using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(Encoding.ASCII.GetBytes(pem));
            Console.Error.WriteLine($"Copie de secours écrite : {output}. Range-la hors de ce PC, avec sa phrase secrète à part.");
            return 0;
        }
        case "import-backup":
        {
            var input = Path.GetFullPath(Arg("--in") ?? throw new InvalidOperationException("Indique --in <fichier>."));
            if (File.Exists(keyPath)) throw new InvalidOperationException($"Une clé existe déjà : {keyPath}.");
            using var key = ECDsa.Create();
            key.ImportFromEncryptedPem(File.ReadAllText(input), Passphrase("Phrase secrète de la copie : "));
            Save(key);
            Console.Error.WriteLine($"Clé restaurée : {keyPath}.");
            Console.WriteLine(UpdateSignature.PublicKey(key));
            return 0;
        }
        default:
            Console.Error.WriteLine("Commandes : keygen, public-key, manifest --version x.y.z --packages <dossier>, notes --version x.y.z --out <fichier>, export-backup --out <fichier>, import-backup --in <fichier> ; option --key <fichier>.");
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or IOException or UpdateException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
