# Contribuer à Mira

Mira est encore une version de développement. Les retours précis sur la lecture, la synchronisation et l’interface sont les bienvenus. L’interface et les échanges peuvent être en français ; English issues and pull requests are welcome too.

## Préparer le projet

Windows 10 2004+ ou Windows 11 x64, Git et le SDK .NET 8 sont nécessaires. Node.js n’est utile que pour le serveur Jellyfin fictif. Une bibliothèque libmpv x64 est nécessaire aux essais de lecture, et une installation de TorLink aux essais TorLink, pas aux tests de base. `tools/import-xterm.ps1` réimporte xterm.js depuis npm en vérifiant son intégrité.

```powershell
git clone https://github.com/sasou-web/Mira.git
cd Mira
dotnet restore Mira.sln --configfile NuGet.Config
dotnet build Mira.sln -c Release --no-restore
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build
dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo --data .artifacts/dev-profile
```

Le projet de tests est un exécutable d’assertions : utiliser `dotnet run`, et non `dotnet test`. Il s’arrête avec un code d’échec si une assertion ne passe pas.

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

### Signature des mises à jour

Les copies installées ne se mettent à jour que depuis une release qui publie `mira-update.json` et `mira-update.json.sig`, signés par une clé listée dans `UpdateKeys.Trusted` (`src/Mira.Core/Updates/UpdateManifest.cs`). `package.ps1` les écrit dans `dist/packages` quand la clé est sur le PC ; il faut ensuite les joindre à la release avec les trois fichiers.

```powershell
dotnet run --project tools/Mira.Release -c Release -- keygen                        # une seule fois : crée la clé, affiche la clé publique
dotnet run --project tools/Mira.Release -c Release -- export-backup --out E:\mira-signing-backup.pem   # copie de secours protégée par une phrase secrète
dotnet run --project tools/Mira.Release -c Release -- import-backup --in E:\mira-signing-backup.pem    # sur un nouveau PC ou compte Windows
```

La clé reste chiffrée par Windows pour le compte courant dans `%APPDATA%\Mira Release\update-signing.key`, hors du dépôt ; ne jamais la committer ni la partager. Tout programme lancé sous ce compte Windows peut s’en servir : un compte réservé aux releases limite ce risque. Sans elle, les nouvelles releases ne sont pas proposées aux copies installées. Pour changer de clé, ajouter la nouvelle clé publique à `UpdateKeys.Trusted` dans une release encore signée par l’ancienne.

Les copies installées interrogent `api.github.com/repos/sasou-web/Mira/releases`. En cas de renommage ou de transfert du dépôt, GitHub redirige cette adresse et les téléchargements, que Mira suit ; ne pas recréer ensuite un dépôt sous l’ancien nom, qui prendrait la place de la redirection. `./tools/update-check.ps1` vérifie de bout en bout l’archive, l’exécutable portable et l’installateur, avec un flux local et une clé d’essai ; il rend ensuite le raccourci Démarrer à la copie qu’il désignait.

L’[architecture](docs/ARCHITECTURE.md) décrit le rendu natif mpv, les couches Windows et l’envoi de progression. Le code original est sous [licence MIT](LICENSE) ; les composants tiers conservent leurs licences.
