# Contribuer à Mira

Mira est encore une version de développement. Les retours précis sur la lecture, la synchronisation et l’interface sont les bienvenus. L’interface et les échanges peuvent être en français ; English issues and pull requests are welcome too.

## Préparer le projet

Windows 10 2004+ ou Windows 11 x64, Git et le SDK .NET 8 sont nécessaires. Node.js n’est utile que pour le serveur Jellyfin fictif. Une bibliothèque libmpv x64 est nécessaire aux essais de lecture, pas aux tests de base.

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
4. Compiler et exécuter les tests ci-dessus. Pour le lecteur, suivre les essais isolés de [VALIDATION.md](docs/VALIDATION.md).

Utiliser un profil `--data` séparé et des médias de test. Ne pas inclure de dossier `data`, cache SQLite, jeton Jellyfin, mot de passe, média personnel ou journal contenant une URL privée. Les captures du dépôt utilisent la démonstration intégrée.

## Distribution et captures

```powershell
./tools/package.ps1
./tools/capture-gallery.ps1
```

Le paquet est produit dans `dist/packages`, depuis un dossier neuf, sans toucher au profil d’une installation locale. Il ne contient pas libmpv. Le script de captures génère un profil temporaire et exporte les vues publiques dans `docs/screenshots`.

L’[architecture](docs/ARCHITECTURE.md) décrit le rendu natif mpv, les couches Windows et l’envoi de progression. Le code original est sous [licence MIT](LICENSE) ; les composants tiers conservent leurs licences.
