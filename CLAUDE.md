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
- `src/Mira.Desktop/` → app Windows ; `MainWindow.Guide.cs` contient la carte « Mira sur ton téléphone » (étapes de `src/Mira.Core/PhoneSetup.cs`, « Tout préparer », QR code).
- `src/Mira.Mac/` → app Mac.
- `src/Mira.Jellyfin/` → `WebController.cs` sert `/Mira` ; `WebFiles.cs` sert les fichiers intégrés à la DLL, versionnés par une empreinte.
- `src/Mira.Web/` → `index.html`, `app.css`, `js/app.js` (routes en `#/...`, bandeau « serveur injoignable », mise à jour par révision), `js/api.js`, `js/views/*`, `js/player/` (`player.js` = lecteur d'Apple ou commandes de Mira, `profile.js` = profil d'appareil, `video.js` = `<video>` partagée et `appleNative()`, `tracks.js` = noms et choix des pistes).
- `src/Mira.Core/JellyfinStartup.cs` + `src/Mira.Desktop/Services/JellyfinAutostart.cs` → Jellyfin joignable par le téléphone (pare-feu pour la maison et Tailscale, service, relance) ; `Mira.exe --jellyfin-startup <port>` = copie élevée, une seule autorisation. `src/Mira.Core/JellyfinFirewall.cs` juge les règles du pare-feu.
- `tests/Mira.Tests/` → exécutable d'assertions (pas `dotnet test`).
- `tools/` → `release.ps1` (publication en une commande), `package.ps1` (paquets Windows signés), `publish.ps1` (copie locale `dist/Mira`), `mac/package.sh`, `web/` (empaquetage et contrôles de Mira web).
- `.github/workflows/` → `ci.yml` (Windows), `mac.yml` (Mac, `.dmg` joint aux releases), `web.yml` (Chromium et Safari, extension et manifeste joints aux releases).
- Flux de Mira web : téléphone → `http://<serveur>:8096/Mira` → extension → app web → API Jellyfin. Lecture : Safari lit le fichier tel quel, sinon Jellyfin convertit en HLS fMP4 (H.264 d'abord).

## Commandes
- Windows : `dotnet restore Mira.sln --configfile NuGet.Config`, puis `dotnet build Mira.sln -c Release --no-restore`.
- Tests : `dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build`.
- App en démo : `dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo --data .artifacts/dev-profile`.
- Extension et manifeste : `tools/web/package.sh <x.y.z> [base de téléchargement]` → `dist/web/`.
- Médias de test : `tools/web/media.sh <dossier>`. Jellyfin neuf préparé par l'API : `node tools/web/setup.mjs <serveur> <médias> <user> <mdp> [url du manifeste]`.
- Contrôle Chromium (53 points, 55 avec `MIRA_STOP`/`MIRA_START`, commandes qui arrêtent et relancent Jellyfin) : `PLAYWRIGHT=<.../playwright/index.mjs> node tools/web/check.mjs <url /Mira/> <user> <mdp> "Courte Web" <dossier>`. Le lecteur d'Apple y est simulé.
- Dans le conteneur cloud : Jellyfin par Docker (`dockerd &`, image `jellyfin/jellyfin:12.1.20260915-010956`, `--network host`) ; le SDK .NET d'Ubuntu n'a pas les cibles WPF : SDK de Microsoft depuis les `.deb` de packages.microsoft.com (jammy), extraits avec `dpkg-deb -x`, puis `dotnet build -p:EnableWindowsTargeting=true` (compile seulement).
- CI d'une branche : `web.yml` et `ci.yml` se lancent par `workflow_dispatch` (outil GitHub `actions_run_trigger`).
- Contrôle Safari (Mac, `safaridriver -p 4444`, 26 points dont le lecteur d'Apple, 34 avec `MIRA_FORMATS=1` et les médias de `media.sh <dossier> formats`) : `node tools/web/safari.mjs <url /Mira/> <user> <mdp> <dossier>`.
- Servir Mira web depuis le disque sans recompiler : lancer Jellyfin avec `MIRA_WEB_DIR=<chemin de src/Mira.Web>`.
- Publication, sur le PC de l'utilisateur, où est la clé de signature : `git checkout main; git pull --ff-only; .\tools\release.ps1` (le `git pull` d'abord : un clone resté en 0.7.1 n'a pas encore le script). Il vérifie gh, la clé, Inno Setup, un arbre propre, `main` identique à GitHub et l'absence du tag, lance `package.ps1`, vérifie que `mira-update.json` décrit ces fichiers (taille, SHA-256), puis crée la release `vX.Y.Z` avec ses 8 fichiers et ses notes ; il remplace un brouillon laissé par un envoi interrompu. `-DryRun` fait tout sauf publier ; `-SkipBuild` ne reprend que des paquets qu'il a construits depuis le même commit (`dist/packages/source-commit.txt`). Les workflows ajoutent le `.dmg`, `mira-jellyfin-X.Y.Z.zip` et `jellyfin-manifest.json`. Le reste (fusion de la PR, vérification de la release) se fait depuis la session.

## Conventions
- Interface et docs en français, en tutoyant ; messages de commit en anglais.
- Fins de ligne : CRLF pour `.cs`, `.xaml`, `.axaml` ; LF pour `.sh`, `.md`, `.yml`, `.json`, `.js`, `.mjs`, `.css`, `.html`, `.webmanifest` (`.gitattributes`).
- Chaque version a son entrée dans `WhatsNew.json` (résumé ≤ 100 caractères, 2 à 5 points, icônes dessinées par Mira) : le test « Nouveautés : bienvenue… » le vérifie.
- `CHANGELOG.md` : section « Non publié » en haut, datée à la publication.
- Version : `<Version>` dans les csproj Desktop, Mac et Jellyfin ; ne la change qu'à la préparation d'une release demandée.
- Les PR se fusionnent en rebase (depuis la session, à la demande de l'utilisateur) ; la publication se fait depuis son PC.
- Mira web : design sombre (tokens dans `:root` de `app.css`), police Nunito Sans auto-hébergée, icônes Phosphor (`js/icons.js`).

## Décisions (et pourquoi)
- 2026-10-08 — Mac en Avalonia avec libmpv embarquée — partage `Mira.Core` et le même lecteur que Windows.
- 2026-10-09 — iPhone par une app web servie par une extension Jellyfin — ni App Store, ni compte Apple, ni AltStore à rafraîchir, gratuit.
- 2026-10-09 — Dépôt de l'extension = `releases/latest/download/jellyfin-manifest.json` — Jellyfin met l'extension à jour à chaque release.
- 2026-10-09 — Une seule `<video>` partagée, débloquée au premier toucher — iOS n'autorise le son qu'après un geste ; l'épisode suivant démarre ainsi seul.
- 2026-10-09 — Profil d'appareil : H.264 avant HEVC en HLS — conversion le plus souvent sans réencodage.
- 2026-10-09 — iPhone et iPad : lecture uniquement dans le lecteur plein écran d'Apple (pas de `playsinline`) — demandé par l'utilisateur (commandes d'Apple). Audio et sous-titres choisis sur la fiche : le menu d'Apple ne change pas l'audio d'une conversion.
- 2026-10-10 — Dynamic Island : défaut d'iOS pour les apps de l'écran d'accueil (WebKit la masque par identifiants d'app qui ne correspondent pas ; en plein écran, iOS 27 la demande même), rien dans la page n'y change rien ; seul levier : session audio `ambient`, la seule mêlable, que le mode silencieux coupe (aucun type ne donne les deux, sources de WebKit) : option « Masquer la Dynamic Island », iPhone seulement, qui le dit.
- 2026-10-10 — Flux refusé par le lecteur (Jellyfin répond) : redemandé la vidéo convertie, puis tout converti (`lastResortProfile`, H.264 SDR ≤ 1080p, AAC stéréo), de là où il en était — une qualité plus basse ne change rien (Jellyfin copie la vidéo sous le plafond, l'audio toujours) ; profil aligné sur jellyfin-web pour Safari (`VideoRangeType`, `IsInterlaced`, niveau HEVC).
- 2026-10-10 — Onglets : pas de View Transition (les barres givrées perdent leur flou) ; l'écran d'un onglet attend d'être prêt (`ready`, 250 ms au plus) puis l'écran quitté se fond dans un calque fixe (`dissolve`) ; cartes mises à jour sur place (`reconcile`/`cardSig`).
- 2026-10-10 — Sous-titres d'une conversion dans le flux HLS (`appleStream` réécrit l'adresse en `SubtitleMethod=Hls`), liste principale servie par `/Mira/hls/…` (extension) : AirPlay, un seul jeu de pistes, et Jellyfin date ses WebVTT HLS pour des segments TS (`MPEGTS:900000`, 10 s de retard en fMP4).
- 2026-10-10 — ASS/SSA en texte (profils `External`, lus en `.vtt`), partout : sinon Jellyfin les incruste en réencodant toute la vidéo ; la mise en forme est perdue.
- 2026-10-10 — Navigation d'app iOS : View Transitions (glissement à droite), onglets qui reviennent au bas de l'historique ; le glissement depuis le bord est celui d'iOS (apps de l'écran d'accueil, depuis 12.2), Mira ne redessine pas ce retour.
- 2026-10-09 — Épisode suivant dans le même lecteur (`history.replaceState`) — retirer la `<video>` du document quitte le plein écran.
- 2026-10-09 — `/Mira/` gardée un mois par le téléphone, révision vérifiée à chaque démarrage — l'app s'ouvre PC éteint et attend le serveur ; pas de service worker en HTTP.
- 2026-10-10 — Pare-feu lu et ouvert par le guide, pour le Wi-Fi de la maison et pour Tailscale, avec ou sans service : un Jellyfin sans service n'a que les règles de l'invite de Windows (bloqué sur les profils non cochés, ou partout si elle n'a pas été validée), Tailscale déclare son réseau privé, la box est souvent publique. Les blocages qui s'appliquent perdent ces profils (pas supprimés), puis règle « Mira - Jellyfin ».
- 2026-10-10 — Téléphone : une seule carte du guide, étapes cochées, « Tout préparer » (une autorisation de Windows), QR code à l'adresse Tailscale quand elle est prête — demandé par l'utilisateur (« beaucoup plus simple pour les nouveaux utilisateurs ») ; l'adresse Tailscale marche aussi à la maison, Tailscale activé.
- 2026-10-09 — Démarrage avec Windows : `sc config start= auto`, `sc failure` + `failureflag 1` (pas `AppExit Restart` : un arrêt voulu reste un arrêt), règles de pare-feu `localsubnet` et `100.64.0.0/10` pour `jellyfin.exe` — l'installateur n'ajoute aucune règle pour son service.

## Fragile / ne pas toucher sans raison
- Sur iOS en HTTP local : pas de service worker, pas de `crypto.randomUUID`, de presse-papiers ni de Wake Lock.
- L'extension doit hériter de `BasePlugin<BasePluginConfiguration>` : sinon sa version est nulle et Jellyfin la marque « en défaut ».
- `WebFiles.Types` doit connaître chaque extension servie : `.mjs` manquant = page du QR code cassée.
- Commandes du lecteur masquées (`.player.idle`) : `pointer-events: none` ; un test doit les réafficher avant de cliquer.
- Le cadre de la `<video>` épouse l'image (`.player.fitted`) : Safari place les sous-titres en bas du cadre.
- Lecteur d'Apple : les `<track>` doivent être ajoutées **avant** `src` (WebKit les passe à AVFoundation au chargement). Les changements de pistes ne sont suivis qu'en plein écran et 1,5 s après le chargement (sinon le navigateur en fait de faux choix). Sur Mac, le plein écran demande un toucher (plein écran d'élément) ; sur iPhone, non après le premier geste.
- Pendant son démarrage, Jellyfin 12 répond à `System/Info/Public` (en camelCase) et 503 au reste : seul `System/Ping` prouve qu'il est prêt.
- `EnableSubtitlesInManifest` n'est lu par Jellyfin que pour `live.m3u8` : pour un film, c'est `SubtitleMethod=Hls` dans l'adresse (sans `SubtitleStreamIndex`, tous les sous-titres sont `DEFAULT=NO`). Jellyfin écrit `SubtitleMethod=Encode` même pour -1 (aucun) : ce n'est pas une incrustation.
- Jellyfin ne convertit jamais ASS/SSA par le profil (`SupportsSubtitleConversionTo`) : sans profil `External` pour eux, il les incruste.
- WebKit : régler une piste sur le mode qu'elle a déjà ne compte pas ; une piste jamais réglée par la page est choisie par WebKit (langue, défaut, forcée). Un choix dans le menu d'Apple change aussi les réglages de sous-titres de l'iPhone.
- WebKit garde `:active` pendant un défilement commencé sur l'élément : appuis par `press.js` (`.pressed`), `:active` pour la souris seulement.
- Erreur de flux : n'attendre le serveur que si `ping()` échoue (sinon boucle de réouvertures sur une erreur 500) ; `<track kind="forced">` n'existe que dans Safari (ailleurs : metadata, jamais affiché).
- Lecteur d'Apple : sur un changement de source, `webkitpresentationmodechanged` peut arriver après que Mira a redemandé le plein écran ; la fermeture pendant l'épisode suivant se lit sur `presentation()` (`switchFrom`).
- ffmpeg : `-shortest` compte aussi les sous-titres (le film de test ASS faisait 3 s) : durée par `-t`.
- Jellyfin : segments HLS TS à 10 s, fMP4 à 0 ; ses WebVTT HLS ont `MPEGTS:900000` en dur. hls.js s'en moque, le lecteur d'Apple non.
- View Transitions : le changement d'écran se fait dans un rappel différé ; `enter()` et tout ce qui touche l'écran affiché vont dans `swap`.
- Vérifier la syntaxe des modules avec `node --experimental-default-type=module --check` (sans l'option, une redéclaration passe).
- Avec `MIRA_WEB_DIR`, la page n'est pas gardée (no-cache) : tester « serveur éteint » avec l'extension compilée.
- Fichiers `.srt` de test : pas d'accents (Jellyfin devine mal l'encodage d'un fichier d'une ligne).
- Safari peut attendre un geste avant de lire : les tests partent du bouton « Lecture » de la fiche, pas d'un changement d'adresse.
- Chromium de Playwright ne lit ni H.264 ni AAC : les tests Chromium utilisent des WebM.
- Jellyfin ne garde une reprise qu'après 5 min par défaut : `setup.mjs` met `MinResumeDurationSeconds` à 1 pour les tests.
- Scripts lancés sur Mac : pas de `md5sum` ni de `base64 -w`.
- Jellyfin 12.1 n'écrit `allowVideoStreamCopy=false` dans l'adresse que pour HLS ; il met `VideoCodec=` même quand il copie : `copiesVideo` lit `TranscodeReasons`. Il ne lit plus `BreakOnNonKeyFrames` ni `MinSegments` (VOD), ni les étiquettes de codec (`codec_tag_string?` en 12.0/12.1).
- Jellyfin 12.1 ne sait pas reconvertir en VP9 un VP9 de niveau inconnu (`level -99`, ffmpeg 222) : le test de la cascade dans Chromium part d'un film H.264.
- Une image `loading=lazy` dans un écran pas encore affiché ne se charge jamais : `ready` ne décode que les `img[loading="eager"]`.
- Une carte gardée par `reconcile` reçoit le nouvel objet (`card.item`) : ses menus lisent `card.item`.
- Pare-feu de Windows : une règle de blocage l'emporte sur toute autorisation ; l'invite « Autoriser l'accès » crée des blocages pour les profils non cochés. Lu et modifié par `HNetCfg.FwPolicy2` en `dynamic` (IDispatch, par nom : une interface `ComImport` mal ordonnée appellerait une autre méthode, dans une copie administrateur) ; propriétés indexées : `policy.FirewallEnabled[profil]`.
- Profil de chaque carte réseau : `MSFT_NetConnectionProfile` (WMI, `WbemScripting.SWbemLocator` en `dynamic`, sans droits). Le nom de carte (`InterfaceAlias`) est le `NetworkInterface.Name` de .NET.
- Les contrôles du Mac (VM) refusent le HEVC 10 bits HDR10 copié (« Media failed to decode ») : il passe par la vidéo convertie.

## État actuel
- 0.7.3 publiée le 2026-10-10 (tag `v0.7.3`, commit `9248e03`), retours du 3e essai sur iPhone (iOS 27) : titres refusés redemandés la vidéo convertie puis tout convertis (cascade `fallback`), profil aligné sur jellyfin-web (plages HDR/Dolby Vision, entrelacé, niveau HEVC), onglets qui attendent leur écran entier et se fondent, cartes mises à jour sur place, Recherche sans focus à l'arrivée, « Masquer la Dynamic Island » qui dit son prix, plus de vibration. Vérifié : CI Chromium 54/54, CI Safari 34/34 (8 formats dans le vrai lecteur d'Apple, dont le HEVC 10 bits HDR10 refusé avant et lu converti après), Windows et Mac, revue adverse (17 défauts corrigés). Détails : `docs/VALIDATION.md`.
- Non vérifié sur un vrai iPhone : quels titres échouaient chez l'utilisateur (codecs inconnus : lui demander l'erreur affichée et les infos du média si ça recommence), la cascade et le fondu sous iOS 27, la barre du bas dans Recherche (cause probable : clavier ouvert puis fermé dans une app de l'écran d'accueil), le menu des sous-titres d'un vrai animé, l'ouverture PC éteint ; sur un vrai PC : « Tout préparer » (autorisation de Windows, pare-feu, service).
- Prochaine étape : fusion de la PR #26 (téléphone en un clic), puis essai chez l'ami et sur l'iPhone de l'utilisateur avec « Tout préparer ».
- Accès Tailscale d'un ami (PC sans service Jellyfin, page blanche à l'adresse 100.x) : aucune règle de pare-feu pour `jellyfin.exe`, Jellyfin qui refusait Tailscale et Mira web absent ; réglé à la main. Correctif et parcours simplifié (« Mira sur ton téléphone », « Tout préparer ») : PR sasou-web/Mira#26.
- L'utilisateur veut que je fasse tout ce que je peux moi-même (fusion des PR comprise) et ne lui laisse que ce qui exige son PC, son serveur ou son iPhone.
