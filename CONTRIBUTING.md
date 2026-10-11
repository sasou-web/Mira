# Contribuer à Mira

Mira est encore une version de développement. Les retours précis sur la lecture, la synchronisation et l’interface sont les bienvenus. L’interface et les échanges peuvent être en français ; English issues and pull requests are welcome too.

## Préparer le projet

Windows 10 2004+ ou Windows 11 x64, Git et le SDK .NET 10 sont nécessaires. Node.js n’est utile que pour le serveur Jellyfin fictif. Une bibliothèque libmpv x64 est nécessaire aux essais de lecture, et une installation de TorLink aux essais TorLink, pas aux tests de base. `tools/import-xterm.ps1` réimporte xterm.js depuis npm en vérifiant son intégrité.

```powershell
git clone https://github.com/sasou-web/Mira.git
cd Mira
dotnet restore Mira.sln --configfile NuGet.Config
dotnet build Mira.sln -c Release --no-restore
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build
dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo --data .artifacts/dev-profile
```

Les projets de tests sont des exécutables d’assertions : utiliser `dotnet run`, et non `dotnet test`. Un échec n’arrête pas les tests suivants ; le bilan final compte les réussis et les échecs, et le code de sortie vaut 1 si un test a échoué. `-- --filter <texte>` ne lance que les tests dont le nom contient ce texte, `-- --list` les nomme. `tests/Mira.Core.Tests` contient les tests sans WPF et tourne aussi sous Linux et macOS ; `tests/Mira.Tests` les lance sous Windows avec ceux de l’app.

## Proposer une modification

1. Ouvrir une issue pour une fonctionnalité importante ou une modification d’architecture.
2. Créer une branche et limiter la pull request à un problème identifiable.
3. Expliquer le comportement avant/après et les vérifications réalisées. Joindre une capture pour une modification visible.
4. Compiler et exécuter les tests ci-dessus. Pour le lecteur et TorLink, suivre les essais isolés de [VALIDATION.md](docs/VALIDATION.md) ; `tools/torlink-check.ps1` n’utilise jamais l’état réel de TorLink.

Utiliser un profil `--data` séparé et des médias de test. Ne pas inclure de dossier `data`, cache SQLite, jeton Jellyfin, mot de passe, média personnel ou journal contenant une URL privée. Les captures du dépôt utilisent la démonstration intégrée.

## Distribution et captures

```powershell
./tools/package.ps1
./tools/capture-gallery.ps1
```

Avant une release, ajouter l’entrée de la version dans `src/Mira.Core/WhatsNew.json` : une phrase de résumé et deux à cinq points courts, avec une icône de Mira. Les tests le vérifient. Ce texte s’affiche dans Mira après la mise à jour ; `package.ps1` en tire aussi `.artifacts/release-notes-<version>.md`, le texte de la page de release sur GitHub. Le détail reste dans `CHANGELOG.md`.

Les fichiers de release sont produits dans `dist/packages`, depuis des dossiers neufs, sans toucher au profil d’une installation locale : archive, exécutable portable et, si [Inno Setup 6](https://jrsoftware.org/isinfo.php) est installé (ou indiqué par `-Iscc`), l’installateur décrit par `installer/Mira.iss`. Aucun ne contient libmpv. Le script de captures génère un profil temporaire et exporte les vues publiques dans `docs/screenshots`.

Pour publier, sur le PC qui a la clé de signature : `git checkout main; git pull --ff-only; .\tools\release.ps1`. Le script vérifie gh, la clé, Inno Setup, un arbre propre et sans fichier hors de git, que `main` est celui de GitHub, que `ci.yml` y a réussi et que la version n’est pas encore publiée, puis construit les paquets Windows. Il crée ensuite la release `vX.Y.Z` en **brouillon** sur ce commit, avec les fichiers Windows : un brouillon n’est visible que des mainteneurs et n’a pas encore de tag. Il lance `mac.yml` et `web.yml` pour ce brouillon : ils construisent depuis le même commit, y joignent l’image disque Mac et Mira web (`mira-jellyfin-X.Y.Z.zip`, `jellyfin-manifest.json`), et le script attend leur réussite, ou s’arrête avec l’adresse de l’exécution qui a échoué. Il télécharge alors ces fichiers, les vérifie, signe `mira-update.json` pour toutes les plateformes, vérifie cette signature et que le brouillon a exactement ses 12 fichiers, puis publie la release d’un coup, avec ses notes. `-DryRun` fait tout sauf publier : le brouillon reste invisible, et le lancement suivant le remplace ; `-SkipBuild` reprend les paquets qu’il a construits depuis le même commit. `mac.yml` et `web.yml` ne se lancent que depuis `main` : une modification de ces workflows doit y être avant la release.

Le script signale aussi si les releases immuables ne sont pas activées sur le dépôt. Activées une fois (**Settings → General → Releases → Enable release immutability**), elles empêchent de modifier une release publiée, ses fichiers comme son tag ; le script vérifie alors la release publiée avec `gh release verify` (gh 2.81 ou plus récent).

### Signature des mises à jour

Les copies installées ne se mettent à jour que depuis une release qui publie `mira-update.json` et `mira-update.json.sig`, signés par une clé listée dans `UpdateKeys.Trusted` (`src/Mira.Core/Updates/UpdateManifest.cs`). Le manifeste décrit les fichiers Windows (`files`, la seule partie que lisent les copies 0.5.2 à 0.7.4) et, à partir de la 0.7.5, l’image disque Mac et l’extension Jellyfin (`others`) : Mira pour Windows n’installe Mira web qu’après l’avoir vérifiée. `release.ps1` signe en dernier, une fois tous les fichiers de la release réunis ; `package.ps1` ne signe plus, sauf avec `-SigningKey` (clé d’essai de `update-check.ps1`). La signature d’une clé absente de `UpdateKeys.Trusted` est refusée.

```powershell
dotnet run --project tools/Mira.Release -c Release -- verify --version X.Y.Z --packages dist\packages --others dist\release   # signature et fichiers, sans la clé
dotnet run --project tools/Mira.Release -c Release -- export-backup --out E:\mira-signing-backup.pem   # copie de secours de la clé, protégée par une phrase secrète
dotnet run --project tools/Mira.Release -c Release -- import-backup --in E:\mira-signing-backup.pem    # sur un nouveau PC ou compte Windows, ou si la clé a disparu
dotnet run --project tools/Mira.Release -c Release -- keygen-backup --out E:\mira-backup-key.pem       # une seule fois : la clé de secours
```

La clé reste chiffrée par Windows pour le compte courant dans `%APPDATA%\Mira Release\update-signing.key`, hors du dépôt ; ne jamais la committer ni la partager. Tout programme lancé sous ce compte Windows peut s’en servir : un compte réservé aux releases limite ce risque. Sans elle, les nouvelles releases ne sont pas proposées aux copies installées.

**Si la clé est introuvable, ne lance pas `keygen`** : une nouvelle clé signerait des releases que toutes les copies installées refusent. Restaure-la avec `import-backup`, depuis sa copie de secours (`export-backup`) ou depuis la clé de secours. Celle-ci se crée une seule fois, avec `keygen-backup` : elle n’est écrite que dans le fichier indiqué, chiffrée par sa phrase secrète, jamais dans le profil Windows. Range le fichier hors du PC (clé USB, coffre) et la phrase secrète ailleurs. La commande affiche sa clé publique : ajoute-la à `UpdateKeys.Trusted` et publie-la dans une release signée par la clé principale ; les copies mises à jour accepteront ensuite la clé de secours si la principale est perdue. Pour changer de clé, ajouter de même la nouvelle clé publique à `UpdateKeys.Trusted` dans une release encore signée par l’ancienne.

Les copies installées interrogent `api.github.com/repos/sasou-web/Mira/releases`. En cas de renommage ou de transfert du dépôt, GitHub redirige cette adresse et les téléchargements, que Mira suit ; ne pas recréer ensuite un dépôt sous l’ancien nom, qui prendrait la place de la redirection. `./tools/update-check.ps1` vérifie de bout en bout l’archive, l’exécutable portable et l’installateur, avec un flux local et une clé d’essai ; il rend ensuite le raccourci Démarrer à la copie qu’il désignait.

L’[architecture](docs/ARCHITECTURE.md) décrit le rendu natif mpv, les couches Windows et l’envoi de progression. Le code original est sous [licence MIT](LICENSE) ; les composants tiers conservent leurs licences.
