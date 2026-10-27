# Tests de Mira

Ce qui vérifie Mira, où ça tourne et comment le lancer. Les derniers résultats exécutés à la main, datés, sont dans [testing/latest-results.txt](testing/latest-results.txt) ; chaque exécution de la CI garde les siens.

| Suite | Système | Ce qu’elle couvre | CI |
| --- | --- | --- | --- |
| `tests/Mira.Core.Tests` | tout système (.NET 10) | tout ce qui se passe de WPF | `ci.yml`, sous Linux |
| `tests/Mira.Tests` | Windows | la suite précédente, puis l’app Windows | `ci.yml`, sous Windows |
| `tools/web/unit` | Node.js 22 ou plus | les décisions du lecteur de Mira web, sans navigateur | `web.yml` |
| `tools/web/check.mjs` | Chromium (Playwright) + Jellyfin | Mira web de bout en bout, sur Jellyfin 10.9, 10.10, 12.1, 12.4 et `latest` (le lundi) | `web.yml` |
| `tools/web/safari.mjs` | Safari sur Mac + Jellyfin | lecture HLS et lecteur d’Apple | `web.yml` |
| harnais de `Mira.exe` | Windows | captures, vrai lecteur, intégration Windows, TorLink | `ci.yml` (galerie seulement) |
| harnais de Mira.app | Mac | chaque écran et la lecture | `mac.yml` |

## Les deux projets de tests .NET

Ce sont des exécutables d’assertions, pas des projets `dotnet test` : chaque test est un nom et un bloc de code, lancés l’un après l’autre par `TestRun`. Un test en échec affiche sa raison et les suivants tournent quand même ; le bilan final compte les deux, et le code de sortie vaut 1 si un test a échoué ou si le filtre n’en garde aucun. Chaque exécution travaille dans son propre dossier, sous `.artifacts/tests`.

```powershell
dotnet run --project tests/Mira.Core.Tests/Mira.Core.Tests.csproj -c Release                       # partout
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build                      # Windows, après dotnet build Mira.sln -c Release
dotnet run --project tests/Mira.Core.Tests/Mira.Core.Tests.csproj -c Release -- --filter TorLink   # seulement les tests dont le nom contient ce texte (répétable)
dotnet run --project tests/Mira.Core.Tests/Mira.Core.Tests.csproj -c Release -- --list             # leurs noms, sans les lancer
```

**`tests/Mira.Core.Tests`** (`net10.0`) : un fichier par thème (`ConnectionTests`, `LibraryTests`, `SyncTests`, `PlayerTests`, `UpdateTests`, `SupplyChainTests`, `TorLinkTests`, `RemoteDownloadTests`, `PhoneTests`, `AccessibilityTests`), réunis par `PortableTests`. Il référence Mira.Core et compile aussi, par lien, les fichiers des autres projets qui ne dépendent ni de WPF ni de Jellyfin : `UpdateApplier`, `UpdateLock`, `Updater`, `MetadataCache` et `ShellRegistration` de l’app Windows, `HlsPlaylists`, `ContentPolicy`, `WebFiles` et `TorLinkRelay` de l’extension, `Profile` de l’app Mac (le trousseau y est remplacé par un substitut). Sous Linux et macOS, un fichier « retenu » par `FileShare.None` ne se teste pas : ce n’y est qu’un verrou consultatif.

**`tests/Mira.Tests`** (`net10.0-windows`, WPF) compile tous les fichiers de `tests/Mira.Core.Tests` sauf son `Program.cs`, les lance d’abord, puis ceux qui ne passent que sous Windows : chemins, liens physiques, DPAPI, pare-feu et script élevé (sans élévation : le runner de la CI est administrateur), contrôles WPF (`Program.cs`, `DesktopSupplyChainTests.cs`). Options :

| Option | Ce qu’elle fait |
| --- | --- |
| `--mpv-integration` | vrai libmpv sur un flux HTTP authentifié : reprise, pause, saut, synchronisation. Demande libmpv, `.artifacts/lecture-test.mp4` (plus de 10 s) et le serveur fictif `node tools/mock-jellyfin.mjs` (127.0.0.1:18096). |
| `--torlink-integration` | vrai TorLink dans la pseudo-console, état isolé ; installe celui de Mira dans un profil de test s’il n’y en a pas (nodejs.org et registry.npmjs.org). |
| `--jellyfin-live <profil>` | lecture seule d’un vrai serveur avec la session protégée d’un profil de validation : bibliothèques et derniers ajouts. |
| `--update-server <dossier> <port> <version>` | faux GitHub sur la boucle locale, pour `tools/update-check.ps1`. |
| `--prepare-ui` | crée une session fictive dans `.artifacts/mock-profile`. |

**Où mettre un nouveau test.** Sans WPF : dans `tests/Mira.Core.Tests`, dans le fichier de son thème (un fichier de l’app Windows sans WPF peut y être lié par le `.csproj`). Seulement sous Windows (chemins, liens physiques, DPAPI, WPF) : dans `tests/Mira.Tests/Program.cs`. Le code commun à Windows et Mac va dans Mira.Core, où il se teste partout.

## Mira web

**Tests Node** (`tools/web/unit/*.test.mjs`, `node:test`, sans dépendance) : cascade de secours et adresses des flux (`stream.js`), passages (chapitres compris, mêmes cas que Mira.Core dans `tests/fixtures/chapters.json`), épisode suivant et trickplay (`timeline.js`), plein écran d’Apple (`presentation.js`), noms et choix des pistes (`tracks.js`), « En ce moment sur Jellyfin » (`viewers.js`, mêmes cas que Mira.Core dans `tests/fixtures/viewers.json`), versions et points de « Nouveau dans Mira » (`whats-new.js`, mêmes cas que `WhatsNew.Since` dans `tests/fixtures/whats-new.json`), recherches et liens magnet de « Télécharger sur le PC serveur » (`torlink.js`, mêmes cas que `RemoteDownloads` dans `tests/fixtures/torlink-remote.json`).

```bash
node --test tools/web/unit/*.test.mjs
for f in src/Mira.Web/js/*.js src/Mira.Web/js/*/*.js tools/web/*.mjs tools/web/unit/*.mjs; do node --experimental-default-type=module --check "$f"; done
```

Ces modules, et `dom.js` qu’ils importent, ne touchent à rien du navigateur à leur chargement : Node les importe tels quels. Les tests restent hors de `src/Mira.Web`, dont chaque fichier est intégré à l’extension et servi. Sans `--experimental-default-type=module`, `node --check` laisse passer une redéclaration.

**Contrôles de bout en bout**, contre un vrai Jellyfin où l’extension est installée :

```bash
tools/web/media.sh <dossier>                     # médias de test (ffmpeg) ; « formats » en plus pour safari.mjs
node tools/web/setup.mjs <serveur> <médias> <user> <mdp> [url du manifeste]   # Jellyfin neuf préparé par son API
PLAYWRIGHT=<.../playwright/index.mjs> node tools/web/check.mjs <url /Mira/> <user> <mdp> "Courte Web" <dossier>
safaridriver -p 4444 &  node tools/web/safari.mjs <url /Mira/> <user> <mdp> <dossier>   # Mac ; MIRA_FORMATS=1 pour les formats
```

`check.mjs` parcourt Mira web dans Chromium à la taille d’un iPhone (connexion, chaque écran, lecture en WebM, reprise, épisodes à voir sur l’affiche, bibliothèque d’animés à part, qualité et heure de fin sur la fiche, saga faite par Jellyfin, rangée Bonus, pistes, intro d’après les chapitres, choix gardés sur Jellyfin et retrouvés sur un autre téléphone, téléchargement sur le PC serveur avec un faux Mira pour Windows au relais de l’extension, déconnexion, sécurité, robustesse) et simule le lecteur d’Apple (épisode suivant dans le même plein écran, et dès le générique de fin d’après les chapitres) ; avec `MIRA_STOP` et `MIRA_START` (commandes qui arrêtent et relancent Jellyfin), il ouvre aussi Mira serveur éteint. `safari.mjs` vérifie la lecture HLS et le vrai lecteur d’Apple dans Safari. Pour servir Mira web depuis le disque sans recompiler l’extension, lance Jellyfin avec `MIRA_WEB_DIR=<chemin de src/Mira.Web>` (la page n’est alors pas gardée : « serveur éteint » se teste avec l’extension compilée).

« Lecture impossible » : `check.mjs` coupe chaque flux d’un titre ; l’écran doit dire ce que contient le fichier et chaque essai, et le rapport « Envoyer le détail à Jellyfin » est relu dans les journaux de Jellyfin (`System/Logs`), sans jeton ni dossier.

Reprise un peu avant : `check.mjs` arrête un titre à 34 s ; il doit reprendre 5 s avant, puis pile au point d’arrêt une fois « Non » choisi dans **Reprendre un peu avant**.

Sous-titres gardés sur Jellyfin : `check.mjs` simule l’extension de sous-titres de Jellyfin (`Items/{id}/RemoteSearch/Subtitles`) ; la feuille doit lister ses résultats par langue, garder le fichier choisi sur Jellyfin et le proposer à la lecture.

## Harnais des applications

Ils sont dans `src/Mira.Desktop/Checks` et ne tournent qu’avec un profil isolé (`--data`), le plus souvent avec la démo (`--demo`). Chacun écrit son résultat dans son dossier de sortie. `dotnet build` les compile (propriété `MiraValidation`, vraie par défaut) ; les paquets (`tools/package.ps1`) et `dist/Mira` (`tools/publish.ps1`) sont construits avec `-p:MiraValidation=false` : ces options y ferment Mira aussitôt (code 2), et `package.ps1` refuse un `Mira.dll` qui contient encore les harnais.

| Commande | Ce qu’elle vérifie |
| --- | --- |
| `Mira.exe --demo --data <profil> --public-gallery <dossier>` | les 14 captures de la galerie (`gallery-result.txt`) ; `tools/capture-gallery.ps1` les copie dans `docs/screenshots` |
| `Mira.exe --data <profil> --readme-gallery <dossier>` | les captures du README, la vraie fenêtre connectée au Jellyfin de `MIRA_GALLERY_SERVER` (`_USER`, `_PASSWORD`), au double de sa taille ; lancé par `gallery.yml` après `tools/gallery/media.sh` et `setup.mjs` (films libres, affiches de TMDb), avec `tools/gallery/web.mjs` pour Mira web |
| `Mira.exe --demo --data <profil> --visual-check <dossier>` | rendus, disposition et mouvements (`result.txt`, `layout.txt`, `motion-checks.txt`) |
| `Mira.exe --demo --data <profil> --test-media <vidéo> --player-check <dossier>` | le vrai lecteur : modes, plein écran, menus, raccourcis, commandes visibles dans une capture de la fenêtre (`player-window-capture.png`), bandes des barres et clic d’activation qui ne mettent pas en pause (`--perf-probe` mesure en plus les images par seconde) ; `tools/player-check.ps1` le lance avec la libmpv de `MpvPackage` (SHA-256 vérifiés) et un clip de 18 s à chapitres fait par ffmpeg (`imageio-ffmpeg` épinglé par son empreinte), à chaque exécution de `ci.yml` sur le runner Windows (fenêtre hors de l’écran, plein écran ramené à la taille du moniteur) |
| `Mira.exe … --windows-check <dossier>` | session multimédia de Windows et vrai mpv (`failure.txt` si la fenêtre ne se crée pas) |
| `tools/torlink-check.ps1 [-Executable dist/Mira/Mira.exe]` | TorLink réel dans Mira, téléchargements fictifs rangés dans une bibliothèque isolée |
| `tools/update-check.ps1` | mises à jour de bout en bout (archive, portable, installateur) avec un flux local et une clé d’essai |
| `Mira --data <profil> --self-check <dossier> --mock --media <vidéo> --short <vidéo>` (Mac) | chaque écran et la lecture, contre un Jellyfin simulé (`results.txt`) ; `--video-probe <vidéo>` diagnostique le rendu |

`--visual-check` demande un profil avec une session protégée valide, ou `--demo`. Le contrôle de TorLink ouvre une fenêtre visible et n’envoie de vraies touches que si elle a le premier plan ; il ne touche ni à l’état réel de TorLink ni à Jellyfin. Avec une vidéo chapitrée, le contrôle du lecteur vérifie aussi les passages à sauter :

```powershell
ffmpeg -i .artifacts/lecture-test.mp4 -i .artifacts/chapitres.txt -map_metadata 1 -map_chapters 1 -c copy .artifacts/lecture-chapitres.mkv
```

Les captures sont faites sur le fond de la fenêtre (opaque). Ce qui demande un vrai Windows (élévation, Restart Manager, veille, multi-écran, Narrateur) reste à essayer à la main : voir [VALIDATION.md](VALIDATION.md).

## Résultats

`docs/testing/latest-results.txt` garde la sortie des suites lancées à la main, avec la date, le commit et le système. Régénère-le à chaque release, à partir de ce qui a vraiment tourné ; pour le reste, renvoie aux exécutions de la CI.
