using System.Security.Cryptography;
using System.Text;
using Mira.Core;
using Mira.Core.Updates;

// Signing of Mira's automatic updates (see SECURITY.md and CONTRIBUTING.md).
//   keygen [--key <file>]                                 creates the signing key; never overwrites one
//   public-key [--key <file>]                             prints the public key to list in UpdateKeys.Trusted
//   check-key [--key <file>]                              checks that the signing key opens and is in UpdateKeys.Trusted
//                                                         (tools/release.ps1, before anything else); warns while
//                                                         UpdateKeys.Trusted has no backup key
//   manifest --version x.y.z --packages <dir> [--others <dir>] [--key <file>] [--test]
//                                                         writes and signs mira-update.json: the Windows files of <dir>,
//                                                         and with --others the Mac disk image and Mira web's plugin found
//                                                         there, checked first (ReleaseChecks.CheckOthers). Refuses a key
//                                                         outside UpdateKeys.Trusted, unless --test (tools/update-check.ps1)
//   verify --packages <dir> [--others <dir>] [--version x.y.z]
//                                                         checks mira-update.json.sig against UpdateKeys.Trusted, then every
//                                                         file it lists (size, SHA-256, .sha256); needs no key
//   notes --version x.y.z --out <file> [--unsigned] [--banner]
//                                                         the release page's text, from the version's entry in WhatsNew.json;
//                                                         --unsigned: Windows files without SignPath's signature, said so;
//                                                         --banner: docs/releases/x.y.z.jpg on top (tools/banner)
//   export-backup --out <file> [--key <file>]             copy protected by a passphrase (PKCS#8, AES-256, PBKDF2)
//   import-backup --in <file> [--key <file>]              makes such a copy, or the backup key, the signing key of the
//                                                         current Windows account
//   keygen-backup --out <file>                            creates the backup key, written only to <file> and protected by
//                                                         a passphrase like export-backup, never kept on this PC; prints
//                                                         its public key, to add to UpdateKeys.Trusted
// The key file is encrypted by Windows (DPAPI) for the current account. It never belongs in the repository.
var defaultKey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mira Release", "update-signing.key");
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var keyPath = Path.GetFullPath(Arg("--key") ?? defaultKey);
var entropy = "Mira update signing key"u8.ToArray();

// A key made anew would sign manifests that every installed copy refuses: a lost key comes back from its backup.
const string RestoreKey = "Restaure-la avec « import-backup » (copie de secours de la clé, ou clé de secours) ; ne lance pas « keygen » : une nouvelle clé serait refusée par toutes les copies installées.";
ECDsa Load()
{
    if (!File.Exists(keyPath)) throw new InvalidOperationException($"Clé de signature introuvable : {keyPath}. {RestoreKey}");
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
// The same protection for a copy of the key and for the backup key: PKCS#8, AES-256, PBKDF2-SHA256, 600 000 rounds.
void WriteBackup(ECDsa key, string output)
{
    var passphrase = Passphrase("Phrase secrète de la copie (12 caractères ou plus) : ");
    if (passphrase.Length < 12) throw new InvalidOperationException("Phrase secrète trop courte.");
    if (Passphrase("Confirme la phrase secrète : ") != passphrase) throw new InvalidOperationException("Les deux saisies diffèrent.");
    var pem = key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
    using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    file.Write(Encoding.ASCII.GetBytes(pem));
}
// A file of the release that is missing, or not exactly the one mira-update.json describes.
void CheckListed(string directory, UpdateFile file)
{
    var path = Path.Combine(directory, file.Name);
    if (!File.Exists(path) || !UpdateClient.Matches(path, file)) throw new InvalidOperationException($"{file.Name} manque dans {directory} ou ne correspond pas à mira-update.json (taille, SHA-256).");
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
        case "check-key":
        {
            // The same refusal as « manifest », in seconds, before tools/release.ps1 builds anything or waits for GitHub.
            ECDsa opened;
            // DPAPI opens the file only for the Windows account (and PC) that wrote it.
            try { opened = Load(); }
            catch (CryptographicException) { throw new InvalidOperationException($"La clé {keyPath} ne s’ouvre pas avec ce compte Windows (elle a été écrite par un autre compte ou un autre PC). Mets ce fichier de côté. {RestoreKey}"); }
            using var key = opened;
            if (!UpdateKeys.Trusted.Contains(UpdateSignature.PublicKey(key)))
                throw new InvalidOperationException($"La clé {keyPath} n’est pas dans UpdateKeys.Trusted : les copies installées refuseraient ce qu’elle signe. {RestoreKey}");
            Console.Error.WriteLine($"Clé de signature : {keyPath}, reconnue par les copies installées.");
            if (UpdateKeys.Trusted.Count < 2)
                Console.Error.WriteLine("Attention : UpdateKeys.Trusted n’a qu’une clé, sans clé de secours. Si elle était perdue, aucune copie installée ne se mettrait plus à jour seule. Crée la clé de secours une fois (« keygen-backup --out <fichier> », hors de ce PC), ajoute sa clé publique à UpdateKeys.Trusted et publie-la dans une release.");
            return 0;
        }
        case "manifest":
        {
            var version = ReleaseFeed.ParseVersion(Arg("--version")) ?? throw new InvalidOperationException("Indique --version x.y.z.");
            var packages = Path.GetFullPath(Arg("--packages") ?? throw new InvalidOperationException("Indique --packages <dossier>."));
            var others = Arg("--others") is { } folder ? Path.GetFullPath(folder) : null;
            var test = args.Contains("--test");
            using var key = Load();
            var publicKey = UpdateSignature.PublicKey(key);
            // Nothing is written with a key installed copies would refuse; only update-check's throwaway key is let through.
            if (!test && !UpdateKeys.Trusted.Contains(publicKey))
                throw new InvalidOperationException($"La clé {keyPath} n’est pas dans UpdateKeys.Trusted : les copies installées refuseraient ce manifeste. {RestoreKey}");
            if (others is not null) ReleaseChecks.CheckOthers(version, others);
            var manifest = UpdateManifest.Create(version, packages, others);
            var json = manifest.ToJson();
            var signature = UpdateSignature.Sign(json, key);
            if (!UpdateSignature.Verify(json, signature, [publicKey])) throw new InvalidOperationException("La signature produite ne se vérifie pas.");
            File.WriteAllBytes(Path.Combine(packages, UpdateManifest.AssetName), json);
            File.WriteAllText(Path.Combine(packages, UpdateManifest.SignatureName), UpdateSignature.Encode(signature), Encoding.ASCII);
            foreach (var file in manifest.Files.Concat(manifest.Others)) Console.Error.WriteLine($"  {file.Kind,-9} {file.Name}  {file.Size} octets  {file.Sha256}");
            if (test) Console.Error.WriteLine("Clé d’essai (--test) : seules les copies lancées avec --update-key acceptent ce manifeste.");
            Console.WriteLine(Path.Combine(packages, UpdateManifest.AssetName));
            Console.WriteLine(Path.Combine(packages, UpdateManifest.SignatureName));
            return 0;
        }
        case "verify":
        {
            var packages = Path.GetFullPath(Arg("--packages") ?? throw new InvalidOperationException("Indique --packages <dossier>."));
            var others = Arg("--others") is { } folder ? Path.GetFullPath(folder) : null;
            var json = File.ReadAllBytes(Path.Combine(packages, UpdateManifest.AssetName));
            var signature = UpdateSignature.Decode(File.ReadAllText(Path.Combine(packages, UpdateManifest.SignatureName), Encoding.ASCII));
            if (signature is null || !UpdateSignature.Verify(json, signature, UpdateKeys.Trusted))
                throw new InvalidOperationException($"mira-update.json n’est pas signé par une clé de UpdateKeys.Trusted : les copies installées le refuseraient. Vérifie la clé de signature (« public-key »). {RestoreKey}");
            // Strict: what this tooling just wrote, so nothing in it is to be skipped.
            var manifest = UpdateManifest.Parse(json, strict: true);
            if (Arg("--version") is { } expected && manifest.Version != ReleaseFeed.ParseVersion(expected))
                throw new InvalidOperationException($"mira-update.json décrit la version {manifest.Version.ToString(3)}, pas {expected}.");
            foreach (var file in manifest.Files) { CheckListed(packages, file); ReleaseChecks.CheckSha256File(Path.Combine(packages, file.Name)); }
            if (others is not null)
            {
                foreach (var kind in UpdateManifest.OtherKinds)
                    if (!manifest.Others.Any(f => f.Kind == kind)) throw new InvalidOperationException($"mira-update.json ne décrit pas {UpdateManifest.OtherName(kind, manifest.Version)}.");
                foreach (var file in manifest.Others) CheckListed(others, file);
                ReleaseChecks.CheckOthers(manifest.Version, others);
            }
            Console.Error.WriteLine($"mira-update.json {manifest.Version.ToString(3)} : signé par une clé de confiance, {manifest.Files.Count + manifest.Others.Count} fichiers conformes.");
            return 0;
        }
        case "notes":
        {
            var version = Arg("--version") ?? throw new InvalidOperationException("Indique --version x.y.z.");
            var output = Path.GetFullPath(Arg("--out") ?? throw new InvalidOperationException("Indique --out <fichier>."));
            var release = WhatsNew.All.FirstOrDefault(x => x.Version == version)
                ?? throw new InvalidOperationException($"src/Mira.Core/WhatsNew.json n’a pas d’entrée pour {version} : écris ses points forts avant de publier.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, WhatsNew.ReleaseNotes(release, signed: !args.Contains("--unsigned"), banner: args.Contains("--banner")), new UTF8Encoding(false));
            Console.WriteLine(output);
            return 0;
        }
        case "export-backup":
        {
            var output = Path.GetFullPath(Arg("--out") ?? throw new InvalidOperationException("Indique --out <fichier>."));
            using var key = Load();
            WriteBackup(key, output);
            Console.Error.WriteLine($"Copie de secours écrite : {output}. Range-la hors de ce PC, avec sa phrase secrète à part.");
            return 0;
        }
        case "import-backup":
        {
            var input = Path.GetFullPath(Arg("--in") ?? throw new InvalidOperationException("Indique --in <fichier>."));
            if (File.Exists(keyPath)) throw new InvalidOperationException($"Une clé existe déjà : {keyPath}.");
            using var key = ECDsa.Create();
            key.ImportFromEncryptedPem(File.ReadAllText(input), Passphrase("Phrase secrète de la copie : "));
            if (key.KeySize != 256) throw new InvalidOperationException("Cette copie n’est pas une clé P-256.");
            Save(key);
            Console.Error.WriteLine($"Clé restaurée : {keyPath}. C’est désormais la clé de signature de ce compte Windows.");
            if (!UpdateKeys.Trusted.Contains(UpdateSignature.PublicKey(key))) Console.Error.WriteLine("Attention : elle n’est pas dans UpdateKeys.Trusted ; les copies installées refuseront ce qu’elle signe.");
            Console.WriteLine(UpdateSignature.PublicKey(key));
            return 0;
        }
        case "keygen-backup":
        {
            // Never stored with DPAPI on this PC: a program running under this account could not use it, nor a thief of
            // the PC. It signs only once restored, after the main key is lost.
            var output = Path.GetFullPath(Arg("--out") ?? throw new InvalidOperationException("Indique --out <fichier>."));
            if (File.Exists(output)) throw new InvalidOperationException($"{output} existe déjà : il n’est jamais remplacé.");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            WriteBackup(key, output);
            Console.Error.WriteLine($"Clé de secours écrite : {output}, protégée par sa phrase secrète, et gardée nulle part ailleurs. Range le fichier hors de ce PC, et la phrase secrète à part.");
            Console.Error.WriteLine("Ajoute sa clé publique (ci-dessous) à UpdateKeys.Trusted, dans src/Mira.Core/Updates/UpdateManifest.cs, et publie-la dans une release signée par la clé principale : les copies installées l’accepteront ensuite si la clé principale est perdue (« import-backup »).");
            Console.WriteLine(UpdateSignature.PublicKey(key));
            return 0;
        }
        default:
            Console.Error.WriteLine("Commandes : keygen, public-key, check-key, manifest --version x.y.z --packages <dossier> [--others <dossier>], verify --packages <dossier> [--others <dossier>] [--version x.y.z], notes --version x.y.z --out <fichier> [--unsigned] [--banner], export-backup --out <fichier>, import-backup --in <fichier>, keygen-backup --out <fichier> ; option --key <fichier>.");
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or IOException or UpdateException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
