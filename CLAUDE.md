> Tiens ce fichier à jour : décisions, commandes, pièges, état actuel. Condense au-delà de ~150 lignes.

# Mira

## Objectif
Client Jellyfin « cinéma » : une app Windows native avec mpv intégré, sa version Mac, et Mira web pour iPhone, iPad et
Android, servie par le serveur Jellyfin lui-même. Pour un particulier qui regarde sa bibliothèque chez lui et ailleurs.

## Stack
- .NET 8 (`global.json` : SDK 8.0.400, roll forward), C#.
- Windows : WPF (`src/Mira.Desktop`), lecteur libmpv. Mac : Avalonia (`src/Mira.Mac`), libmpv embarquée, `.dmg`.
- Code commun : `src/Mira.Core` (client Jellyfin, synchro, mises à jour signées, nouveautés, TorLink).
- Mira web : HTML, CSS et modules JS sans étape de build (`src/Mira.Web`), vendorisés : hls.js, qrcode-generator.
- Extension Jellyfin : `src/Mira.Jellyfin`, compilée contre Jellyfin.Controller 10.9.11 et chargée par Jellyfin 12.1.
- Jellyfin 10.9 ou plus récent côté serveur.

## Architecture
- `src/Mira.Core/` → `JellyfinClient.cs` (API, dont `InstallWebAppAsync`), `MiraWeb.cs` (dépôt, adresse du QR code), `PlayerText.cs` (noms des pistes en français), `WhatsNew.json` et `WhatsNew.cs` (nouveautés, texte de la page de release), `Updates/` (mises à jour signées).
- `src/Mira.Desktop/` → app Windows ; `MainWindow.Guide.cs` contient « Installer Mira web ».
- `src/Mira.Mac/` → app Mac.
- `src/Mira.Jellyfin/` → `WebController.cs` sert `/Mira` ; `WebFiles.cs` sert les fichiers intégrés à la DLL, versionnés par une empreinte.
- `src/Mira.Web/` → `index.html`, `app.css`, `js/app.js` (routes en `#/...`), `js/api.js`, `js/views/*`, `js/player/` (`player.js`, `profile.js` = profil d'appareil, `video.js` = `<video>` partagée, `tracks.js`).
- `tests/Mira.Tests/` → exécutable d'assertions (pas `dotnet test`).
- `tools/` → `package.ps1` (paquets Windows signés), `mac/package.sh`, `web/` (empaquetage et contrôles de Mira web).
- `.github/workflows/` → `ci.yml` (Windows), `mac.yml` (Mac, `.dmg` joint aux releases), `web.yml` (Chromium et Safari, extension et manifeste joints aux releases).
- Flux de Mira web : téléphone → `http://<serveur>:8096/Mira` → extension → app web → API Jellyfin. Lecture : Safari lit le fichier tel quel, sinon Jellyfin convertit en HLS fMP4 (H.264 d'abord).

## Commandes
- Windows : `dotnet restore Mira.sln --configfile NuGet.Config`, puis `dotnet build Mira.sln -c Release --no-restore`.
- Tests : `dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build`.
- App en démo : `dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo --data .artifacts/dev-profile`.
- Extension et manifeste : `tools/web/package.sh <x.y.z> [base de téléchargement]` → `dist/web/`.
- Médias de test : `tools/web/media.sh <dossier>`. Jellyfin neuf préparé par l'API : `node tools/web/setup.mjs <serveur> <médias> <user> <mdp> [url du manifeste]`.
- Contrôle Chromium (18 points) : `PLAYWRIGHT=<.../playwright/index.mjs> node tools/web/check.mjs <url /Mira/> <user> <mdp> "Courte Web" <dossier>`.
- Contrôle Safari (Mac, `safaridriver -p 4444`) : `node tools/web/safari.mjs <url /Mira/> <user> <mdp> <dossier>`.
- Servir Mira web depuis le disque sans recompiler : lancer Jellyfin avec `MIRA_WEB_DIR=<chemin de src/Mira.Web>`.
- Publication, sur le PC de l'utilisateur, où est la clé de signature : `git pull`, `./tools/package.ps1`, puis `gh release create vX.Y.Z <8 fichiers> --notes-file .artifacts/release-notes-X.Y.Z.md`. Les workflows ajoutent le `.dmg`, `mira-jellyfin-X.Y.Z.zip` et `jellyfin-manifest.json`.

## Conventions
- Interface et docs en français, en tutoyant ; messages de commit en anglais.
- Fins de ligne : CRLF pour `.cs`, `.xaml`, `.axaml` ; LF pour `.sh`, `.md`, `.yml`, `.json`, `.js`, `.mjs`, `.css`, `.html`, `.webmanifest` (`.gitattributes`).
- Chaque version a son entrée dans `WhatsNew.json` (résumé ≤ 100 caractères, 2 à 5 points, icônes dessinées par Mira) : le test « Nouveautés : bienvenue… » le vérifie.
- `CHANGELOG.md` : section « Non publié » en haut, datée à la publication.
- Version : `<Version>` dans les csproj Desktop, Mac et Jellyfin ; ne la change qu'à la préparation d'une release demandée.
- L'utilisateur fusionne les PR lui-même (rebase) et publie depuis son PC.
- Mira web : design sombre (tokens dans `:root` de `app.css`), police Nunito Sans auto-hébergée, icônes Phosphor (`js/icons.js`).

## Décisions (et pourquoi)
- 2026-10-08 — Mac en Avalonia avec libmpv embarquée — partage `Mira.Core` et le même lecteur que Windows.
- 2026-10-09 — iPhone par une app web servie par une extension Jellyfin — ni App Store, ni compte Apple, ni AltStore à rafraîchir, gratuit.
- 2026-10-09 — Dépôt de l'extension = `releases/latest/download/jellyfin-manifest.json` — Jellyfin met l'extension à jour à chaque release.
- 2026-10-09 — Une seule `<video>` partagée, débloquée au premier toucher — iOS n'autorise le son qu'après un geste ; l'épisode suivant démarre ainsi seul.
- 2026-10-09 — Profil d'appareil : H.264 avant HEVC en HLS — conversion le plus souvent sans réencodage.

## Fragile / ne pas toucher sans raison
- Sur iOS en HTTP local : pas de service worker, pas de `crypto.randomUUID`, de presse-papiers ni de Wake Lock.
- L'extension doit hériter de `BasePlugin<BasePluginConfiguration>` : sinon sa version est nulle et Jellyfin la marque « en défaut ».
- `WebFiles.Types` doit connaître chaque extension servie : `.mjs` manquant = page du QR code cassée.
- Commandes du lecteur masquées (`.player.idle`) : `pointer-events: none` ; un test doit les réafficher avant de cliquer.
- Le cadre de la `<video>` épouse l'image (`.player.fitted`) : Safari place les sous-titres en bas du cadre.
- Safari peut attendre un geste avant de lire : les tests partent du bouton « Lecture » de la fiche, pas d'un changement d'adresse.
- Chromium de Playwright ne lit ni H.264 ni AAC : les tests Chromium utilisent des WebM.
- Jellyfin ne garde une reprise qu'après 5 min par défaut : `setup.mjs` met `MinResumeDurationSeconds` à 1 pour les tests.
- Scripts lancés sur Mac : pas de `md5sum` ni de `base64 -w`.

## État actuel
- 0.7.0 publiée le 2026-10-09 : Windows, Mac (`.dmg`) et Mira web (extension et manifeste dans la release).
- Mira web vérifiée en CI : Chromium 18/18 ; Safari sur Mac 11/11 (HLS, sous-titres sur l'image, ±10 s, reprise, épisode suivant). Installée depuis la vraie release sur un Jellyfin 12.1 local.
- Premier essai sur un vrai iPhone par l'utilisateur : « quelques bugs » à relever (pas encore détaillés).
- Prochaine étape : obtenir la liste des bugs (captures d'écran, modèle d'iPhone, version d'iOS, ouverture dans Safari ou depuis l'écran d'accueil), les reproduire, corriger et améliorer Mira web.
- Non vérifié : le bouton « Installer Mira web » dans l'interface Windows lancée (à vérifier avec l'utilisateur).
