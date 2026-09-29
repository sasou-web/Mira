<div align="center">
  <img src="src/Mira.Desktop/Assets/mira.png" width="88" alt="Logo Mira" />
  <h1>Mira</h1>
  <p><strong>Ta bibliothèque Jellyfin. Le confort d’un lecteur fait pour elle.</strong></p>
  <p>Un client Windows natif, une interface cinéma et mpv directement dans l’application.</p>
  <p>
    <a href="https://github.com/sasou-web/Mira/releases"><img src="https://img.shields.io/badge/version-0.4.9_preview-e8e8ed?style=flat-square" alt="Version 0.4.9 preview" /></a>
    <img src="https://img.shields.io/badge/Windows-10%20%2F%2011_x64-0078D4?style=flat-square" alt="Windows 10 et 11 x64" />
    <img src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square" alt=".NET 8" />
    <a href="LICENSE"><img src="https://img.shields.io/badge/licence-MIT-white?style=flat-square" alt="Licence MIT" /></a>
    <a href="https://github.com/sasou-web/Mira/actions/workflows/ci.yml"><img src="https://github.com/sasou-web/Mira/actions/workflows/ci.yml/badge.svg" alt="Windows build" /></a>
  </p>
  <p><a href="https://github.com/sasou-web/Mira/releases/tag/v0.4.9">Télécharger pour Windows</a> · <a href="docs/SCREENSHOTS.md">Galerie</a> · <a href="docs/INSTALLATION.md">Installation</a> · <a href="docs/README.en.md">English</a></p>
</div>

![Accueil Mira](docs/screenshots/01-home.png)

> Captures de l’application réelle, avec le catalogue fictif intégré. Aucun film, épisode ou compte Jellyfin n’est fourni. Mira est un projet indépendant, encore en développement.

## Pourquoi Mira ?

Mira garde Jellyfin comme bibliothèque et lui ajoute une interface de bureau centrée sur le visionnage. Parcours tes films et séries, retrouve ta dernière lecture et regarde directement dans la même fenêtre avec **libmpv**.

- **Une bibliothèque visuelle** : bandeau panoramique, couleur adaptée à l’image, recherche, filtres, favoris et fiches avec saisons et épisodes.
- **Une navigation soignée** : typographie embarquée, icônes arrondies, défilement progressif, survol continu et préférence de réduction des animations.
- **Un lecteur intégré** : plein écran, mini-lecteur déplaçable, choix des pistes, sous-titres, vitesse, chapitres et passage à l’épisode suivant.
- **Une reprise fiable** : dernière lecture à gauche, progression locale, statut vu, file d’envoi persistante et reprise de synchronisation après coupure.
- **Une place dans Windows** : logo et raccourci Démarrer, icône de notification, commandes dans l’aperçu de la barre des tâches et session multimédia système.

<table>
  <tr><td><img src="docs/screenshots/02-library.png" alt="Catalogue avec filtres" /></td><td><img src="docs/screenshots/08-subtitles-settings.png" alt="Réglages audio et sous-titres" /></td></tr>
  <tr><td align="center">Catalogue et filtres</td><td align="center">Audio et sous-titres</td></tr>
</table>

[Voir les 14 captures et leurs descriptions →](docs/SCREENSHOTS.md)

## Installer

1. Télécharger **Mira-0.4.9-win-x64.zip** dans la [release](https://github.com/sasou-web/Mira/releases/tag/v0.4.9).
2. Extraire le dossier complet dans un emplacement accessible en écriture, puis lancer `Mira.exe`.
3. Connecter ton serveur Jellyfin — généralement `http://127.0.0.1:8096` sur le même PC.
4. Vérifier **Réglages → Lecture** : un moteur **libmpv x64** est requis. Mira détecte certains moteurs installés avec mpv.net ou Jellyfin MPV Shim ; un chemin peut être choisi manuellement.

**Le runtime .NET est inclus ; libmpv ne l’est pas.** Windows 10 2004+ / Windows 11 x64, interface française, archive de développement non signée. Aucun abonnement ou compte Mira n’est nécessaire. Jellyfin reste lancé pour fournir la bibliothèque.

[Guide complet : moteur vidéo, raccourcis, mise à jour et dépannage](docs/INSTALLATION.md)

## Développer

Prérequis : Windows x64 et SDK .NET 8. Les tests de base n’exigent ni serveur Jellyfin ni libmpv.

```powershell
git clone https://github.com/sasou-web/Mira.git
cd Mira
dotnet restore Mira.sln --configfile NuGet.Config
dotnet build Mira.sln -c Release --no-restore
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build
dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo --data .artifacts/dev-profile
```

```powershell
./tools/package.ps1          # Archive Windows propre + empreinte SHA-256
./tools/capture-gallery.ps1  # Captures avec un profil fictif isolé
```

| Dossier | Rôle |
| --- | --- |
| `src/Mira.Desktop` | Interface WPF, lecteur mpv, intégration Windows |
| `src/Mira.Core` | API Jellyfin, cache SQLite, synchronisation et règles de lecture |
| `tests/Mira.Tests` | Tests exécutables de comportement et de persistance |
| `tools` | Publication, captures, serveur fictif et import des ressources |
| `docs` | Guides, architecture, galerie et validation |

La compilation et les tests de base sont lancés sur Windows dans GitHub Actions. Les essais natifs du lecteur et du système ont aussi été réalisés localement ; ils ne sont pas tous couverts par le runner CI.

## État du projet

Mira **0.4.9** est une préversion utilisable pour tester le projet, avec des évolutions encore nécessaires : transcodage, choix des versions multiples d’un titre, édition des collections, distribution signée et essais approfondis HDR, multicanal et multi-écrans. La compatibilité avec toutes les versions de Jellyfin n’est pas garantie. Les mises à jour sont manuelles.

- [Fonctionnalités détaillées et raccourcis](docs/USER_GUIDE.md)
- [Architecture](docs/ARCHITECTURE.md) et [validation](docs/VALIDATION.md)
- [Historique](CHANGELOG.md) et [contribuer](CONTRIBUTING.md)
- [Signaler un bug](https://github.com/sasou-web/Mira/issues/new/choose) ou [une vulnérabilité](SECURITY.md)

## Licence et crédits

Code original sous [licence MIT](LICENSE). Le logo, les icônes actuelles et les illustrations de démonstration sont dessinés pour Mira. Police **Nunito Sans** sous SIL OFL 1.1 ; composants .NET, SQLite et Windows selon leurs licences respectives. Les anciens SVG Phosphor sont conservés avec leur notice MIT.

Voir [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) pour les attributions et [licenses](licenses) pour les textes inclus. libmpv est un composant externe, non distribué dans l’archive.
