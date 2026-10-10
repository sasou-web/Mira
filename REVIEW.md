# Revue complète de Mira 0.7.3

Revue du dépôt `sasou-web/Mira` au commit `9248e03` (= `main` = la 0.7.3 publiée le 2026-10-10), faite sur la branche `review/fixes-zl287w`.
Aucun fichier de code n'a été modifié : ce rapport et la mise à jour de `CLAUDE.md` sont les seuls changements.
Chaque problème a été trouvé dans le code puis revérifié par une seconde lecture indépendante (et une troisième pour
les sévérités P1 proposées). Ce qui n'a pas pu être mesuré ici (Mira installé sur ton PC) est marqué **estimation**,
avec la commande PowerShell pour le mesurer (section 7).

> Ta demande décrivait `WindowManager.exe` dans `C:\Program Files (x86)\WindowManager\`, lancé par une tâche planifiée
> avec `-AUTOSTART -TASKSCHEDULER -WAITFORDATAPATH`. **Ce n'est pas Mira** : aucune trace dans le dépôt (ça ressemble à
> WindowManager de DeskSoft). Mira est `Mira.exe`, 64 bits, installé par utilisateur dans `%LOCALAPPDATA%\Programs\Mira`,
> sans tâche planifiée ni démarrage avec Windows. Les questions Windows de ta demande sont appliquées à `Mira.exe`.

## Résumé

Mira est solide sur ce qui compte le plus : builds Release à 0 avertissement, CI verte sur Windows, Mac et web (Chromium
54/54 refait ici), mises à jour Windows signées ECDSA et bien vérifiées, **zéro télémétrie**, aucun processus enfant
avec fenêtre non voulue, aucune commande construite depuis une entrée non validée, pas de XSS dans Mira web.
**Aucun P0 ni P1 confirmé** (trois P1 proposés ont été ramenés en P2 par deux vérifications indépendantes chacun).
82 problèmes : 0 P0, 0 P1, 38 P2, 44 P3. Les 5 pires :
1. **Échéances** : .NET 8 sans correctif après le **10/11/2026** et runner `macos-14` retiré le **02/11/2026** (DEP-01, DEP-06).
2. **Code exécuté hors de ta chaîne signée** : extension Jellyfin mise à jour seule depuis `releases/latest`, TorLink installé avec ~226 paquets npm non figés (SEC-02, DEP-03).
3. **Mira peut rester bloqué** : processus invisible qui empêche toute relance, base SQLite abîmée jamais mise de côté, délai réseau dépassé avalé sans avis (ROB-01, ROB-02, ROB-03).
4. **Exposition réseau** : pare-feu ouvert à Jellyfin sur les réseaux publics, repli HTTP silencieux, jeton jamais révoqué à la déconnexion (SEC-03, SEC-05, SEC-06).
5. **Dette de structure** : `MainWindow` = 27 fichiers partiels et ~170 champs, tests dans un seul fichier de 176 Ko, ~2/3 du code sans test (QUA-01, QUA-03, QUA-04).

## Ce qui a été réellement exécuté

| Commande / contrôle | Résultat |
|---|---|
| `dotnet build Mira.sln -c Release -p:EnableWindowsTargeting=true` (+ Mira.Mac, Mira.Jellyfin), SDK 8.0.425 | **0 avertissement, 0 erreur** (analyseurs par défaut). Compile seulement : WPF ne tourne pas sous Linux. |
| CI `ci.yml` (Windows : build, 77 tests de `Mira.Tests`, captures, paquets) sur cette branche | **succès** (run 38081397221) |
| CI `mac.yml` (paquet `.dmg`, self-check) / `web.yml` (Chromium + Safari) | **succès** (runs 38081401263 / 38081399366) |
| `tools/web/check.mjs` en local contre Jellyfin 12.1 (Docker) et l'extension compilée | **54/54** |
| Analyse stricte (`AnalysisMode=Recommended` + SecurityCodeScan 5.6.7, sur une copie) | 77 avertissements uniques : CA1305 ×14 (culture), CA1859 ×11, CA1031 ×8 (6 dans les harnais), CA1806 ×7 (retours Win32 ignorés, cosmétiques), CA2000 ×5, CA1001 ×5, SCS0018 ×2 (**faux positifs**, vérifiés), SCS0005 ×4 (`Random` de la démo), IDE0052/IDE0060 (code mort, QUA-08) |
| `dotnet format --verify-no-changes` | **échoue** : 398 écarts d'espacement, 46 SYSLIB1054, 22 CA1861, 21 CA1859… (QUA-07) |
| `dotnet list package --vulnerable --include-transitive` | `SQLitePCLRaw.lib.e_sqlite3 2.1.6` **High** GHSA-2m69-gcr7-jv3q (Core, Desktop, Mac, Release, Tests) ; `System.Text.Json 8.0.4` High dans Mira.Jellyfin, **non livré** (fourni par le serveur) |
| `npm audit` des paquets vendorisés (hls.js 1.7.3, qrcode-generator 2.0.4, xterm 6.0.0, addon-fit 0.11.0) | 0 vulnérabilité, dernières versions, fichiers identiques octet pour octet aux paquets npm |
| `npm audit` de `torlnk@1.9.0` (installé par Mira) | 5 High via `ip` (GHSA-2p57-rm9w-gvfp), **aucun correctif amont** |
| eslint 9 (recommended + règles strictes, 24 fichiers) / `node --check` / shellcheck 0.11 (3 scripts) | 0 problème / OK / 0 problème |
| `git grep` TODO, FIXME, HACK, XXX | 0 hors vendor (`xterm.css`) |
| Recherche de secrets et de chemins absolus | aucun secret ; seuls chemins : l'exemple fictif `C:\Users\Toi\…` ; identifiants par défaut dans `tools/web/check.mjs:15` (SEC-17) |
| Reproductions sur copies de `Mira.Core` | base SQLite aux pages abîmées → `SqliteException 11` dans `Prune`, jamais mise de côté (ROB-02) ; serveur muet → `TaskCanceledException` après 12,0 s (ROB-03) ; `.torrent` de 64 Mo → ~1,2 Go et 10 s (SEC-14) |
| **Non exécutable ici** | tout ce qui demande Windows réel : démarrage, RAM/CPU au repos, veille, DPI, multi-écran, UAC, SmartScreen, pare-feu. Marqué « estimation », commandes en section 7. |

## 1. Cartographie

### Stack et versions

| Partie | Techno | Version | Rôle |
|---|---|---|---|
| `src/Mira.Core` | .NET 8, C# (Nullable) | SDK 8.0.400 roll forward (`global.json`) | client Jellyfin (REST + WebSocket), cache SQLite, file de synchro, mises à jour signées, installateurs figés (mpv, Node, Jellyfin), TorLink |
| `src/Mira.Desktop` | WPF + NotifyIcon WinForms, x64, `asInvoker`, PerMonitorV2 | net8.0-windows10.0.19041.0 | app Windows ; libmpv par P/Invoke dans un HWND enfant ; WebView2 1.0.4258.31 + xterm.js 6 (terminal TorLink) ; DPAPI |
| `src/Mira.Mac` | Avalonia | 11.3.22 | app Mac (arm64), libmpv Homebrew embarquée dans le `.dmg` |
| `src/Mira.Jellyfin` | extension Jellyfin | Jellyfin.Controller 10.9.11 (chargée par 12.1) | sert Mira web sur `/Mira`, réécrit les listes HLS sur `/Mira/hls` |
| `src/Mira.Web` | HTML/CSS/modules JS sans build | hls.js 1.7.3, qrcode-generator 2.0.4 | app web iPhone/iPad/Android |
| Dépendances .NET | Microsoft.Data.Sqlite 8.0.20 (→ SQLite **3.41.2**, 2023), SharpCompress 1.0.0, ProtectedData 8.0.0 | | |
| Téléchargés à la demande | libmpv 20260927, Node.js 24.21.0, `torlnk@1.9.0` + ~226 paquets npm, Jellyfin 12.1 | SHA-256 figés sauf npm | |
| Outils | Inno Setup 6, `tools/*.ps1`, `tools/Mira.Release` (signature ECDSA P-256), `tools/web/*.mjs` (Playwright) | | |
| Taille | C# 18 883 lignes (121 fichiers), JS 4 325 lignes hors vendor | | |

### Schéma

```text
                     ┌──────────────── PC Windows de l'utilisateur ────────────────┐
 Mira.exe (WPF) ─────┤ data\ : session.protected (DPAPI), settings.json, device-id,  │
  │  libmpv (HWND)   │        library-<hash>.db (SQLite WAL : cache, progress,       │
  │  WebView2 ── node.exe (TorLink, job KillOnClose) ── pairs BitTorrent           │
  │                  │        completed, outbox), images\, mpv\, node\, torlink\,   │
  │                  │        webview2\, updates\, errors.log                        │
  │ REST + WebSocket │                                                               │
  ▼                  │  Service JellyfinServer (auto, relances) ◄── pare-feu         │
 Jellyfin ◄──────────┤   └─ extension Mira : /Mira (fichiers intégrés), /Mira/hls    │
  ▲   ▲              └───────────────────────────────────────────────────────────────┘
  │   └── Mira web (iPhone/Android) : http://<serveur>:8096/Mira → localStorage (jeton)
  └────── Mira.app (Mac) : session.json en clair (0600)
 GitHub releases ──► mira-update.json signé (Windows) ; jellyfin-manifest.json (extension, non signé)
```

### Flux de données
- **Connexion** : adresse saisie → jusqu'à 3 candidates sondées sur `System/Info/Public` → `Users/AuthenticateByName` → jeton chiffré DPAPI (`LocalProfile.cs:30-34`). Le mot de passe n'est jamais stocké.
- **Bibliothèque** : REST (`HttpClient`, délai 12 s) → objets `MediaItem` → cache JSON dans SQLite (« home », vues) → interface. WebSocket (`LibraryChanged`, `UserDataChanged`) déclenche une actualisation après 600 ms ; actualisation de secours toutes les 45 s fenêtre active.
- **Lecture** : `PlaybackInfo` → flux direct `Videos/{id}/stream?static=true` (ou fichier local si serveur en boucle locale) → libmpv avec l'en-tête `Authorization`. Progression écrite d'abord dans SQLite (outbox) puis envoyée dans l'ordre start → progress → stop (`SyncService`, boucle 3 s).
- **Mira web** : même origine que Jellyfin ; Safari lit le fichier tel quel sinon HLS fMP4 converti ; la liste principale passe par `/Mira/hls` (extension).
- **Mises à jour** : `api.github.com` → `mira-update.json` + signature → téléchargement plafonné, SHA-256 → application à la fermeture.

### État et persistance
| App | Où | Quoi |
|---|---|---|
| Windows | `data\` à côté de `Mira.exe` (`App.xaml.cs:35`) ; `--data <dossier>` sinon | voir schéma ; écritures atomiques (tmp + move) ; base SQLite par serveur et compte, purgée à l'ouverture (`LibraryStore.cs:24`) |
| Mac | `~/Library/Application Support/Mira` | `session.json` **en clair** (droits 0600), réglages, cache SQLite |
| Web | `localStorage` de l'origine Jellyfin | `mira.session` (jeton), `mira.device`, `mira.home.*`, `mira.library.*`, réglages ; `sessionStorage` pour la navigation |
| Extension | DLL seulement | fichiers de Mira web intégrés, révision = empreinte |

### Points d'entrée (`Mira.exe`)
Normal (fenêtre) ; `--data <dossier>`, `--demo` ; sans fenêtre : `--apply-update` (`App.xaml.cs:17`), `--jellyfin-startup <port>` (copie **élevée**, `:19`), `--register-windows [--shortcut]` (`:20`), `--install-engine` (`:41`) ; relance : `--after <pid>`, `--updated-from` ; mises à jour de test : `--update-feed`, `--update-key` (`Updater.cs:79-84`) ; **harnais livrés dans l'exe** : `--visual-check`, `--player-check`, `--windows-check`, `--qa-window`, `--public-gallery`, `--torlink-check`, `--perf-probe`, `--test-media`, `--loop-test-media` (QUA-02). Aucun protocole d'URL enregistré : une page web ne peut pas lancer Mira avec ces options.

### Démarrage (Windows)
`OnStartup` : options sans fenêtre → attente d'un `--after` (30 s max) → `UpdateLock.WaitIdle` (30 s max, sur le thread de l'interface) → mutex `Local\Mira-<SHA-256 du profil>` → construction de `MainWindow` (BAML de 81 Ko, profil, réglages, DPAPI) → `Show` → `OnLoaded` ouvre SQLite **sur le thread de l'interface** puis actualise. Mira **ne démarre pas avec Windows** ; seul le service Jellyfin (réglé par Mira en démarrage automatique) démarre au boot. Démarrage à froid **estimé** à 1,5-3 s (pas de ReadyToRun).

## 2. Build et qualité
- **Warnings** : 0 en Release ; seul `Mira.Jellyfin` a `TreatWarningsAsErrors`. L'analyse stricte ne trouve aucun défaut grave (détail ci-dessus).
- **Tests** : 77 tests (414 assertions) dans un seul `tests/Mira.Tests/Program.cs` de 1 809 lignes, sans framework, arrêt au premier échec, Windows seulement. Les parties sensibles de `Mira.Core` sont bien testées (signature du manifeste, téléchargement vérifié, file de synchro, sélection de pistes, listes HLS). **Couverture estimée** (statique, la CI ne la mesure pas) : Core 83 % des méthodes publiques nommées, Desktop 56 %, Mac 0, extension : `HlsPlaylists` seul ; ≈ 7 000 lignes sous test sur ≈ 21 300. Sans test : `MainWindow` (5 215 lignes, dont `PlayAsync`), `UpdateApplier.Run`, `WebFiles`, `WebController`, tout le JS sauf par Playwright.
- **Longues fonctions** : `player.js` `create()` 1 032 lignes ; C# : `RunTorLinkCheckAsync` 149 l. (harnais), `PlayAsync` 95, `OnStartup` 76 ; médiane 8 lignes sur 958 méthodes. Code très dense : 1 142 lignes > 160 caractères, maximum 493.
- **Erreurs avalées** : 81 `catch` vides côté `src`, tous typés et filtrés (pas de `catch {}` nu) ; le défaut est l'absence de toute trace et l'inclusion d'`ArgumentException` dans les erreurs « attendues » (ROB-05).
- **Duplication** : libmpv Windows/Mac (45 lignes identiques), 38 tracés d'icônes, passages/épisode suivant (QUA-06).
- **Code mort** : quasi nul (`_heroRequestedId`, `LoadHeroImageAsync`). **TODO/FIXME** : aucun.

## 3. Sécurité et vie privée

### Ce qui est bien fait (vérifié dans le code)
- Mises à jour Windows : ECDSA P-256 vérifiée sur les octets bruts avant lecture (`UpdateClient.cs:98-102`), version = tag, jamais de retour arrière, redirections suivies à la main (HTTPS, 4 hôtes GitHub, 5 sauts), tailles plafonnées, revérification juste avant l'installation (`Updater.cs:225`).
- Aucune validation TLS contournée ; jeton jamais dans une URL sur Windows/Mac (en-tête pour mpv) ; `errors.log` = type + méthode seulement, 256 Ko max (`App.xaml.cs:66-74`).
- mpv durci (`config=no`, `LoadLibraryEx` sans dossier courant ni PATH) ; WebView2 du terminal fermé (navigation, réseau d'arrière-plan, DevTools, SmartScreen coupés, CSP `connect-src 'none'`).
- Téléchargements tiers figés par taille + SHA-256 (mpv, Node, Jellyfin) ; npm lancé avec `--ignore-scripts`.
- Mira web : DOM construit par `textContent`, CSP stricte (`script-src 'self'`, `connect-src 'self'`), pas de traversée de chemin sur `/Mira`, proxy HLS sans SSRF.
- Clé de signature des releases hors du dépôt, jamais écrasée, copie de secours PBKDF2 600 000 itérations ; dépôt avec détection de secrets et push protection.

### Appels réseau (inventaire complet) — aucune télémétrie, aucun analytics
| # | Qui | Vers | Pourquoi / quand | Données | Désactivable |
|---|---|---|---|---|---|
| W1-W3 | Mira Windows, libmpv | **ton** Jellyfin (REST, WebSocket, flux) | bibliothèque, lecture ; secours 45 s, progression 3 s, keep-alive 25 s | `Authorization: … DeviceId=<GUID aléatoire>, Version, Token` | non (c'est la fonction) |
| W4 | Mira Windows | adresse tapée (≤ 3 candidates) | trouver le serveur avant le mot de passe | rien d'identifiant, puis identifiants sur l'adresse retenue | — (voir SEC-05) |
| W5 | Mira Windows | api.github.com, github.com, *.githubusercontent.com | mises à jour : 20 s après l'ouverture puis toutes les 6 h | IP, `User-Agent: Mira/0.7.3` | **oui** : Réglages → Mises à jour → « Mettre à jour automatiquement » → décoché |
| W6 / W7 / W9 | Mira Windows | downloads.sourceforge.net / nodejs.org / repo.jellyfin.org | libmpv / Node / installateur Jellyfin, **sur action** | IP, UA Mira | oui (ne pas cliquer) |
| W8 | npm lancé par Mira | registry.npmjs.org | installer TorLink, **sur action** | IP, UA npm ; audit, fund, update-notifier coupés | oui |
| W12 | TorLink (node) | pairs, DHT, trackers, UPnP/NAT-PMP | téléchargements, **tant que Mira tourne** une fois TorLink ouvert | IP, infohash | oui (ne pas l'activer) |
| W13 | runtime WebView2 | Microsoft (diagnostics, mises à jour Edge) | hors du contrôle de Mira | selon Windows | Paramètres Windows → Confidentialité → Diagnostics |
| M2 | Mira Mac | api.github.com | **à chaque lancement** | IP, `User-Agent: Mira-Mac/x.y.z` | **non** (SEC-11) |
| P1 | Mira web | même origine seulement | API, médias ; test de débit 1 Mo par lecture hors réseau local (PERF-03) | jeton en en-tête, `ApiKey` dans les URL vidéo/sous-titres (imposé par `<video>`) | non |
| J1 | extension | 127.0.0.1:<port> | réécrire les listes HLS | la requête du téléphone | non |
| J2 | **Jellyfin** | github.com/sasou-web/Mira/releases/latest/download/jellyfin-manifest.json | mise à jour **automatique** de l'extension, au démarrage et toutes les 24 h | IP, UA Jellyfin | oui : Jellyfin → Tableau de bord → Extensions → Dépôts → retirer « Mira » (SEC-02) |

### Processus enfants
19 lancements inventoriés. **Exigence `CREATE_NO_WINDOW` respectée** pour tous les enfants console cachés : `sc.exe`, `netsh.exe` (chemin absolu dans System32, `JellyfinAutostart.cs:105-106`), `node.exe`/npm (`TorLinkSetup.cs:55-64`) ont `UseShellExecute=false` + `CreateNoWindow=true`. Mira ne lance **jamais** `cmd.exe` ni `powershell.exe`. Aucune commande construite depuis une entrée non validée (port entier, chemin sans guillemet, nom de service `[A-Za-z0-9_-]`, `ArgumentList` partout). Seules exceptions visibles et voulues : le repli console de TorLink (sur clic) et les deux élévations (`runas` impose `ShellExecute`, qui ignore `CreateNoWindow` ; elles visent des programmes graphiques, rien ne clignote). Node de TorLink rattaché à un job `KillOnJobClose` : pas d'orphelin.

### Binaire non signé : conséquences et options réalistes
Conséquences aujourd'hui : SmartScreen « Windows a protégé votre PC » au premier lancement de l'installateur et de la version portable ; UAC « Éditeur : inconnu » pour `--jellyfin-startup` et l'installateur de Jellyfin ; risque de faux positif antivirus plus élevé pour un exe qui télécharge et lance d'autres exe. La signature ECDSA de Mira protège les **mises à jour**, pas le premier téléchargement.

| Option | Coût | Ce que verraient tes amis | À savoir |
|---|---|---|---|
| **SignPath Foundation** (recommandé) | gratuit | éditeur « SignPath Foundation », réputation SmartScreen à construire | projet OSI (Mira est MIT), build entièrement automatisé en CI (règle aussi DEP-10), politique de signature publiée ; à confirmer pour le SDK WebView2 (licence Microsoft) |
| Microsoft Store (MSIX) | gratuit (compte individuel) | **aucun avertissement** (le Store re-signe) | pièce d'identité + selfie chez Microsoft ; MSIX impose `data` hors du dossier d'installation et des mises à jour par le Store (refonte L) ; règle aussi SEC-04 (dossier non modifiable) |
| Certificat OV (DigiCert, Sectigo, Certum…) | ~150-300 $/an | ton nom, réputation à construire | clé matérielle obligatoire depuis 2023 |
| Azure Artifact Signing | ~9,99 $/mois | — | **non accessible** : particuliers des États-Unis et du Canada seulement |
| EV | 400 $+/an | comme OV | ne contourne plus SmartScreen depuis 2024 : inutile |

## 4. Windows (réponses à tes questions)
- **x86 / x64** : `Mira.exe` est x64 uniquement (`Mira.Desktop.csproj:9`, `package.ps1:35`), installé par utilisateur ; « Program Files (x86) » ne concerne pas Mira (seulement la recherche du mpv de Jellyfin MPV Shim, WIN-08). Pas de build ARM64.
- **Impact au démarrage de session** : aucun pour Mira ; le service Jellyfin est forcé en démarrage automatique non différé avec relances sans fin (WIN-05).
- **Instance unique** : mutex par session et par profil, canal nommé restreint à l'utilisateur ; défauts : processus fantôme après un échec (ROB-01), relance muette pendant la fermeture, faux « déjà ouvert » (WIN-03).
- **Veille / reprise** : rien n'est géré explicitement ; récupération implicite (WebSocket, file 3 s, secours 45 s) ; l'empêchement de veille repose sur la valeur par défaut de mpv (ROB-12).
- **Écran / DPI / multi-écran** : PerMonitorV2 déclaré, plein écran sur le bon moniteur, icônes vectorielles ; taille calculée sur l'écran principal mais fenêtre centrée sur l'écran de la souris (WIN-02), coins du mini-lecteur figés au DPI d'entrée (WIN-07), Viewbox qui réduit les textes à 200 % (estimation).
- **Installation / désinstallation / mise à jour** : installation sans droits administrateur, mises à jour atomiques ; restent après désinstallation : 2 règles de pare-feu, réglage du service, Jellyfin, `data\` si gardé, `%TEMP%\.net\Mira\*` du portable (WIN-06).

## 5. Performance et robustesse
**Mesuré** (Linux, copie de `Mira.Core`) : tour à vide de la synchro 0,035 ms ; ouverture de `LibraryStore` 0,7-2,2 ms ; cache « home » 60 titres = 201 Ko / 3,1 ms, 600 titres = 2 Mo / 26,8 ms. Mira web : 1er lancement 20 requêtes, 218 Ko décodés (≈ 118 Ko compressés), ensuite 1 requête de ~1 Ko ; grille de 3 000 films = 22 586 nœuds, 27 250 écouteurs, 13 Mo de tas, rafraîchissement d'un seul bloc de 2,98 Mo.
**Estimations** (à mesurer, section 7) : RAM au repos 250-330 Mo sur l'accueil (images décodées trop grandes, PERF-01), 400-700 Mo après navigation ; CPU ~0 % réduit (réveils toutes les 3 s et 20 s) ; au premier plan sur l'accueil, un rappel par image même carrousel en pause (PERF-02, coût non mesuré).
**Minuteurs** : synchro 3 s permanente, lecteur 100 ms en lecture seulement, secours 45 s fenêtre active, TorLink 20 s permanent, mises à jour 6 h, WebSocket 25 s.
**Fichier corrompu / chemin absent / disque plein** : `settings.json` illisible → remis à zéro sans le dire (ROB-09) ; base SQLite aux pages abîmées → Mira bloqué (ROB-02) ; disque plein → images jetées (ROB-08), fermeture qui laisse un processus (ROB-01) ; `data` non inscriptible au 1er lancement du portable → processus invisible (ROB-01). Bien géré : `torlink-imports.json` abîmé, en-tête SQLite invalide, dossier TorLink absent, disque plein à l'installation de mpv/Node.

## 6. UX/UI
- **Design system** : Mira web en a un vrai (16 tokens, 356 `var()`), Windows presque pas (4 tokens dont 2 inutilisés, 167 couleurs en dur, 25 tailles de police, 142 marges distinctes) ; palettes Windows/Mac/web qui divergent (UX-07).
- **États** : chargement (squelettes), 5 états vides distincts, erreurs avec Réessayer sur la fiche ; manquent un état hors connexion persistant (UX-06) et un vrai état d'erreur de lecture sur Windows (UX-04). Erreurs de connexion et d'installation précises et actionnables ; tutoiement constant.
- **Clavier** : bons raccourcis du lecteur et retour cohérent (Échap, Alt+←, bouton souris) ; rail après les 60 affiches dans l'ordre de tabulation, pas de flèches dans la grille, raccourcis Windows ≠ Mac (UX-10).
- **Accessibilité** : 26/26 boutons à icône nommés ; mais 7 boutons principaux et 6 champs sans nom, statuts non annoncés (UX-02), réglages Windows d'animation/taille du texte/contraste ignorés (UX-03), 4 petits textes sous 4,5:1 (UX-08), zoom bloqué dans Mira web (UX-05).
- **Look « template »** : identité réelle (icônes maison, monogramme, bouton teinté par l'affiche, ton des textes) sur une structure très Netflix et une page Réglages calquée sur Windows 11 ; Expander Aero2 non thémé. Pas de look « IA » générique.

## Tous les problèmes

Sévérité : **P0** bloquant / faille exploitable / fuite de données ; **P1** bug sérieux ou risque réel à corriger vite ; **P2** robustesse, performance, maintenabilité, dette notable ; **P3** mineur ou cosmétique. Effort : **S** < 1 h, **M** ≤ 1 jour, **L** > 1 jour.

| ID | Sév. | Catégorie | Emplacement | Problème | Correction proposée | Effort |
|---|---|---|---|---|---|---|
| DEP-01 | P2 (échéance 10/11) | Dépendances | `Directory.Build.props:3`, `global.json:3`, `src/Mira.Desktop/Mira.Desktop.csproj:4` | Tout cible .NET 8, publié en self-contained : plus aucun correctif du runtime embarqué après le 10/11/2026. Aujourd'hui rien ne manque (0.7.2 embarque 8.0.31). | Passer en .NET 10 LTS (support jusqu'au 14/11/2028) ; **fixer `net8.0` dans `Mira.Jellyfin.csproj`** (sinon l'extension ne se charge plus sur Jellyfin 10.9/10.10) ; `release.ps1` affiche la version de `System.Private.CoreLib.dll` publiée. | M |
| DEP-06 | P2 (échéance 02/11) | Build/CI | `.github/workflows/mac.yml:22`, `.github/workflows/web.yml:79`, `tools/mac/package.sh:20` | `macos-14` est retiré le 02/11/2026 (coupures programmées avant, dont le 12/10) : la prochaine release n'aurait pas de `.dmg` et le contrôle Safari casse ; la version minimale de macOS est déduite du runner. | Passer à `macos-15` ; écrire la version minimale en dur dans `package.sh` et l'annoncer dans le README. | S |
| DEP-02 | P2 | Dépendances | `src/Mira.Core/Mira.Core.csproj:3`, `THIRD_PARTY_NOTICES.md:8` | SQLite 3.41.2 (2023) embarqué via SQLitePCLRaw 2.1.6, marqué vulnérable (High) et déprécié. | Microsoft.Data.Sqlite 8.0.31 (testé ici : 0 vulnérable, build à 0 avertissement), ou 10.0.x avec DEP-01 ; mettre à jour les notices. | S |
| DEP-05 | P2 | Build/CI | `Directory.Build.props:2`, `src/Mira.Jellyfin/Mira.Jellyfin.csproj:12` | NuGetAudit ne regarde que les paquets directs : la faille SQLite (transitive) n'apparaît nulle part ; pas de Dependabot ni de lockfile. | `NuGetAuditMode=all` + NU1903/NU1904 en erreur ; suppression documentée de GHSA-8g4q pour l'extension ; `.github/dependabot.yml` (nuget, github-actions). | S |
| DEP-03 | P2 | Sécurité | `src/Mira.Core/TorLink/TorLinkPackage.cs:5`, `:19`, `src/Mira.Desktop/TorLink/TorLinkSetup.cs:60` | `npm install torlnk@1.9.0` : seul le paquet de tête est figé, ~226 dépendances résolues au jour de l'activation (`webtorrent ^2.4.1`…). Le commentaire ligne 5 laisse croire l'inverse. Code exécuté sous ton compte, qui peut lire ta session DPAPI. | Embarquer un `package-lock.json` relu et lancer `npm ci --omit=dev --ignore-scripts` (sha512 de chaque archive vérifié) ; ou une archive `node_modules` construite en CI, figée par SHA-256 comme Node. | M |
| SEC-02 | P2 | Sécurité | `src/Mira.Core/MiraWeb.cs:12`, `src/Mira.Core/JellyfinClient.cs:252`, `.github/workflows/web.yml:152` | « Installer Mira web » enregistre `releases/latest/download/jellyfin-manifest.json` : Jellyfin installe seul toute nouvelle extension (24 h), **sans la signature ECDSA** qui protège Mira, dans le processus du service. Un compte GitHub compromis suffit. | Inscrire le SHA-256 du zip de l'extension dans `mira-update.json` signé et enregistrer un manifeste figé par version (`releases/download/vX.Y.Z/…`) après vérification par Mira ; en attendant, le dire dans SECURITY.md. | M |
| SEC-08 | P2 | Sécurité | `tools/Mira.Release/Program.cs:17`, `:23`, `src/Mira.Core/Updates/UpdateManifest.cs:103` | Une seule clé de confiance, protégée par DPAPI CurrentUser (entropie constante) : tout programme de ta session peut la déchiffrer ; les copies installées appliquent les mises à jour sans confirmation. | Ajouter maintenant une **clé de secours** (publique dans `UpdateKeys.Trusted`, privée hors du PC) ; à terme clé non exportable (TPM via `Microsoft Platform Crypto Provider`, ou YubiKey PIV). | M |
| DEP-08 | P2 | Build/CI | `tools/Mira.Release/Program.cs:75`, `tools/release.ps1:43`, `:89` | `manifest` signe avec n'importe quelle clé et sort en 0 si elle n'est pas de confiance ; `release.ps1` ne vérifie pas la signature : une release signée par une mauvaise clé serait refusée par toutes les copies jusqu'à ce que tu re-signes le manifeste. | Commande `Mira.Release verify` contre `UpdateKeys.Trusted` avant `gh release create` ; message « restaure-la avec import-backup, ne lance pas keygen ». | S |
| DEP-04 | P2 | Sécurité | `src/Mira.Desktop/Playback/MpvEngine.cs:46`, `src/Mira.Desktop/TorLink/TorLinkInstallation.cs:17`, `src/Mira.Core/MpvPackage.cs:27` | libmpv, Node.js et TorLink ne sont jamais mis à jour après leur première installation : les versions figées du code ne servent qu'au premier coup. | Comparer `version.txt` aux versions figées au démarrage (mpv) ou à l'ouverture de TorLink, télécharger/vérifier à côté, activer au démarrage suivant ; ne pas toucher un moteur choisi par l'utilisateur. | M |
| SEC-09 | P2 | Sécurité | `installer/Mira.iss:16`, `tools/package.ps1:35`, `:53` | Exécutables non signés Authenticode (SmartScreen, UAC « éditeur inconnu », faux positifs antivirus). | Voir le tableau des options (section 3) : SignPath Foundation, ou Store MSIX. | M |
| DEP-07 | P2 | Licences | `tools/mac/package.sh:53`, `:68`, `THIRD_PARTY_NOTICES.md:31` | Le `.dmg` embarque libmpv et FFmpeg (GPL) sans licence ni notice ; les notices disent « libmpv is not included » (vrai sous Windows seulement). | Copier `LICENSE`, notices et `licenses/` dans `Contents/Resources`, lister les formules Homebrew embarquées et l'offre de sources. | S |
| SEC-03 | P2 | Sécurité | `src/Mira.Core/JellyfinStartup.cs:62`, `:63`, `src/Mira.Desktop/Services/JellyfinAutostart.cs:53` | Les deux règles « Mira - Jellyfin » sont en `profile=any` : sur un Wi-Fi public, `localsubnet` = le sous-réseau du hotspot, et Jellyfin y traite 10/8, 172.16/12, 192.168/16 comme locaux. La règle est jugée « prête » sur son seul nom, même sur un autre port. | `localsubnet` en `profile=private,domain` ; règle Tailscale limitée à l'interface Tailscale ; guider vers « réseau Privé » ; vérifier le contenu de la règle (port, profils). | S |
| SEC-05 | P2 | Sécurité | `src/Mira.Core/ServerAddress.cs:43`, `:63`, `:82` | Adresse sans schéma : HTTPS et HTTP sont sondés ensemble ; si HTTPS échoue (certificat), HTTP est retenu **sans avertir** : mot de passe et jeton en clair, même pour un hôte public. | Pas de repli HTTP après un échec de certificat sur le même hôte ; confirmation explicite pour `http://` hors boucle locale, réseau privé, Tailscale et `.local`. | S |
| SEC-06 | P2 | Sécurité | `src/Mira.Desktop/MainWindow.xaml.cs:416`, `src/Mira.Mac/Views/MainWindow.cs:175` | « Déconnecter » (Windows, Mac) efface le jeton local sans `Sessions/Logout` : il reste valide sur Jellyfin sans expiration ; les caches `library-*.db` et `images\` restent sur le disque. | `JellyfinClient.LogoutAsync()` (délai court, erreurs ignorées) avant `ClearConnection` ; option « Effacer aussi les données de ce compte sur ce PC ». | S |
| SEC-07 | P2 | Vie privée | `src/Mira.Web/js/api.js:131`, `src/Mira.Web/js/views/settings.js:111`, `src/Mira.Web/js/views/home.js:38` | La déconnexion de Mira web laisse `mira.home.*`, `mira.library.*`, recherches récentes, pistes par série et reprises masquées du compte. | Effacer ces clés dans `signOut()` et sur 401 ; garder `mira.device` et les réglages d'appareil ; point de contrôle dans `check.mjs`. | S |
| SEC-01 | P2 | Sécurité | `src/Mira.Web/js/app.js:496`, `:497`, `:346`, `src/Mira.Web/js/api.js:35`, `:125` | Mira web ne vérifie jamais que le serveur qui répond à `http://192.168.x.y:8096` est le tien : sur un réseau hostile, la première requête lui donne le jeton (compte souvent **administrateur**), et la page relue en `cache: 'reload'` remplace la copie gardée un mois. `serverId` est stocké mais jamais comparé. | Au démarrage, lire `System/Info/Public` **sans jeton**, comparer `Id` à `serverId` avant toute requête authentifiée et avant `checkForUpdate()` ; sinon « Ce n'est pas ton serveur ». `publicInfo()`/`publicUsers()` sans jeton. | M |
| SEC-04 | P2 | Sécurité | `src/Mira.Desktop/Services/JellyfinAutostart.cs:61`, `src/Mira.Desktop/Services/JellyfinInstaller.cs:57`, `:60`, `installer/Mira.iss:26` | Mira élève `Mira.exe` (et ses DLL, le runtime) dans `%LOCALAPPDATA%\Programs\Mira`, modifiable sans droits ; l'installateur de Jellyfin téléchargé est lancé en administrateur après un écart entre vérification et exécution. Exige un programme déjà présent sous ton compte. | Élever un binaire de System32 avec une commande fixe (`powershell.exe -NoProfile -WindowStyle Hidden -EncodedCommand`, construite de constantes et de valeurs validées ; `runas` interdit `CREATE_NO_WINDOW`, un bref flash de console est à vérifier) ; garder l'installateur ouvert en `FileShare.Read` de l'empreinte au lancement. | M |
| ROB-01 | P2 | Robustesse | `src/Mira.Desktop/App.xaml.cs:58`, `:78`, `src/Mira.Desktop/MainWindow.Player.cs:386`, `:390`, `src/Mira.Desktop/Services/LocalProfile.cs:41` | Une exception au démarrage (`data` non inscriptible au 1er lancement du portable) ou à la fermeture (disque plein, `SaveSettings`, `RememberPlayback`) passe par le gestionnaire global qui la marque traitée **sans arrêter** : fenêtre cachée ou jamais montrée, icône retirée, mutex gardé → processus invisible et « Mira est déjà ouvert » à chaque relance. | Attraper sur place dans `Window_Closing` (chaque étape au mieux, `Close` garanti en `finally`), `SaveSettings` tolérant, libération de mpv en `finally` ; `try/catch` autour de `new MainWindow` avec message clair + `Shutdown(1)`. | S |
| ROB-02 | P2 | Robustesse | `src/Mira.Core/LibraryStore.cs:20`, `:23`, `:24`, `src/Mira.Desktop/MainWindow.xaml.cs:182` | Seul `CreateSchema()` est protégé ; une page de données abîmée (en-tête intact) fait échouer `Prune()` à **chaque** ouverture : reproduit, la base n'est jamais mise de côté et Mira reste sur le voile (Mac : se ferme). La doc promet l'inverse. | `CreateSchema`, `Prune` et `PRAGMA quick_check(1)` (résultat ≠ « ok » = abîmée) dans le même `try` ; `try` côté appelants avec avis clair ; test qui écrase une page. **Pas** de repli `:memory:` (une connexion par opération). | S |
| ROB-03 | P2 | Robustesse | `src/Mira.Desktop/MainWindow.xaml.cs:323`, `:397` | Le délai de 12 s lève `TaskCanceledException`, avalée par `catch (OperationCanceledException) { }` : squelette tant que le serveur reste muet, sans avis sur cette actualisation (au démarrage, un avis finit par venir d'un autre appel après ~24 s ; le secours de 45 s répare l'écran quand Jellyfin répond). | `catch (OperationCanceledException) when (ct.IsCancellationRequested)` ; idem `Refresh_Click` ; test avec un serveur muet. | S |
| ROB-04 | P2 | Robustesse | `src/Mira.Desktop/MainWindow.xaml.cs:396`, `src/Mira.Desktop/MainWindow.Recency.cs:21`, `src/Mira.Mac/Views/PlayerPage.cs:269` | Hors de `SyncService`, une `SqliteException` (disque plein, base verrouillée) n'est pas « attendue » : boîte d'erreur à chaque actualisation sur Windows, fermeture de l'app sur Mac. | Traiter le cache comme un bonus (`TryCache`) ; ajouter `SqliteException` à `IsExpected` / `Errors.Expected` avec un texte dédié ; gestionnaire global sur Mac. | S |
| ROB-05 | P2 | Robustesse | `src/Mira.Desktop/MainWindow.xaml.cs:396`, `src/Mira.Mac/Services/Session.cs:73` | `IsExpected` range `ArgumentException` (donc `ArgumentNull`, `ArgumentOutOfRange` = bugs) parmi les erreurs réseau attendues ; 46 sites, dont 14 `catch` vides : des bugs disparaissent sans trace. | `UserInputException` pour les erreurs de saisie ; retirer `ArgumentException` d'`IsExpected` ; `Diagnostics.Note(ex)` qui écrit type + méthode dans `errors.log`. | M |
| ROB-06 | P2 | Robustesse | `src/Mira.Web/js/app.js:143`, `:493`, `src/Mira.Web/js/views/login.js:34` | Stockage du site bloqué (« Bloquer tous les cookies ») : un `sessionStorage` non protégé lève dans `route()` et Mira web reste sur son logo, sans message. | Aides `temp.get/set/remove` protégées comme `session.js` ; message « Mira a besoin du stockage du site » ; cas ajouté à `check.mjs`. | S |
| WIN-01 | P2 | Windows | `src/Mira.Desktop/App.xaml.cs:87`, `src/Mira.Desktop/Services/WindowsIdentity.cs:24`, `:30` | Chaque copie lancée (portable, zip, `dist\Mira` de `publish.ps1`) réécrit le raccourci du menu Démarrer et l'identité Windows : le menu Démarrer ouvre ensuite une autre copie avec un autre profil. | N'enregistrer automatiquement que depuis la copie installée ; ailleurs seulement si le raccourci est absent ou cassé ; jamais sous `%TEMP%`. | S |
| WIN-02 | P2 | Windows | `src/Mira.Desktop/MainWindow.xaml.cs:94`, `:103`, `:110` | Taille de fenêtre calculée sur l'écran principal (`SystemParameters.WorkArea`) mais fenêtre centrée sur l'écran de la souris : débordement sur un écran secondaire plus petit ; position jamais mémorisée. | Mémoriser le placement et le valider contre les moniteurs présents ; `ScreenFit` sur la zone de travail et le DPI du moniteur cible ; test. | M |
| PERF-01 | P2 | Performance | `src/Mira.Desktop/Services/ImageCache.cs:14`, `:43`, `src/Mira.Desktop/Views/MediaCard.cs:114` | Affiches décodées à 500 px, vignettes à 720 px, fonds à 2560 px quel que soit l'affichage ; budget mémoire de 512 Mo ; grille `WrapPanel` non virtualisée qui charge toutes les images. RAM **estimée** 250-330 Mo sur l'accueil, > 1 Go après beaucoup de « Afficher plus ». | Décoder à la taille affichée × DPI ; budget ~128 Mo vidé à la réduction ; images chargées à l'entrée dans la zone visible (ou panneau virtualisé). | M |
| PERF-03 | P2 | Performance | `src/Mira.Web/js/player/player.js:25`, `:36`, `:477`, `src/Mira.Web/js/api.js:186` | Hors réseau local, chaque ouverture de flux télécharge 1 Mo de test, sans délai maximal ni contrôle du code HTTP (un 503 donne la qualité la plus basse) ; Tailscale (100.64/10, `*.ts.net`) n'est pas reconnu comme local. | Mémoriser la mesure une heure ; `AbortSignal.timeout(5000)` ; ignorer une réponse non `ok` ; reconnaître Tailscale. | S |
| PERF-04 | P2 | Performance | `src/Mira.Web/js/api.js:100`, `src/Mira.Web/js/views/library.js:140`, `:185` | Films/Séries : grille sans limite, rafraîchie en redemandant d'un coup tous les titres affichés avec `Overview` inutile (3 000 films = 2,98 Mo, 22 586 nœuds). | Retirer `Overview`/`PrimaryImageAspectRatio` des cartes (garder `Genres` pour le bandeau) ; rafraîchir la première page seulement ; `content-visibility: auto` sur les cartes. | M |
| QUA-01 | P2 | Qualité | `src/Mira.Desktop/MainWindow.xaml.cs:13`, `src/Mira.Desktop/MainWindow.Player.cs:32` | `MainWindow` est un objet-dieu : 27 partiels, 5 215 lignes, ~170 champs (57 bool), ~400 méthodes ; la machine à états du lecteur est faite de champs de la fenêtre. | Extraire d'abord un `PlaybackSession` sans WPF (état de lecture, passages, épisode suivant, rapports) derrière une interface `IMpv`, réutilisable sur Mac ; puis catalogue et TorLink. | L |
| QUA-02 | P2 | Qualité | `src/Mira.Desktop/App.xaml.cs:48`, `src/Mira.Desktop/MainWindow.xaml.cs:118`, `src/Mira.Desktop/MainWindow.TorLinkCheck.cs:267` | 10 harnais de contrôle (1 183 lignes, 23 % de `MainWindow`) livrés dans `Mira.exe` et activables par la ligne de commande, dont un qui envoie de vraies frappes (`SendInput`). | Configuration `Validation` (`<Compile Remove>` + `#if MIRA_VALIDATION`) construite par la CI, non livrée par `package.ps1`. | M |
| QUA-03 | P2 | Tests | `tests/Mira.Tests/Program.cs:78`, `:79`, `tests/Mira.Tests/Mira.Tests.csproj:2` | Tous les tests dans un `Program.cs` de 176 Ko sans framework : le premier échec arrête tout, impossible de lancer un seul test, Windows seulement (donc rien sous Linux/Mac). | `tests/Mira.Core.Tests` (net8.0, xUnit) avec les tests de Core tels quels ; garder un projet Windows pour Desktop ; sortir les outils (`--update-server`…). | M |
| QUA-04 | P2 | Tests | `src/Mira.Desktop/Services/UpdateApplier.cs:17`, `src/Mira.Jellyfin/WebFiles.cs:41`, `src/Mira.Desktop/MainWindow.Player.cs:32` | ~2/3 du code produit sans test unitaire (estimation statique) : `UpdateApplier.Run`, `WebController`/`WebFiles`, `PlayAsync`, toute l'app Mac. | Par ordre : tests d'`UpdateApplier.Run`, test « WebFiles sert chaque fichier de Mira.Web », `PlaybackSession` (QUA-01), `node --test` du JS pur ; mesurer la couverture en CI. | L |
| QUA-05 | P2 | Qualité | `src/Mira.Web/js/player/player.js:64`, `:479` | Tout le lecteur web dans une fermeture de 1 032 lignes à 48 variables mutables ; la cascade de la 0.7.3 n'est testable que par Playwright. | Sortir une machine à états pure (`cascade.js`, `next.js`) et la tester avec `node --test` (`tracks.js` s'importe déjà tel quel : 3 tests réussis ici). | L |
| UX-01 | P2 | UX | `src/Mira.Desktop/MainWindow.Settings.cs:16`, `:31`, `src/Mira.Desktop/MainWindow.Player.cs:390` | La page dit « Enregistré automatiquement en quittant les réglages » mais fermer Mira ou recliquer la roue dentée perd les changements. | Appeler `AutoSaveSettings()` dans `Window_Closing` et `Settings_Click` ; mieux, appliquer chaque réglage immédiatement. | S |
| UX-02 | P2 | Accessibilité | `src/Mira.Desktop/MainWindow.xaml:18`, `:330`, `src/Mira.Desktop/MainWindow.Details.cs:203` | 7 boutons principaux (Regarder, Plus d'infos, Favori, Vu, Se connecter…) et 6 champs de réglages sans nom pour le Narrateur ; statuts et erreurs jamais annoncés. | `AutomationProperties.Name` / `LabeledBy` ; helper `Announce()` (`LiveRegionChanged`) pour connexion, installation, toasts. | S |
| UX-03 | P2 | Accessibilité | `src/Mira.Core/Models.cs:160`, `src/Mira.Desktop/Views/Motion.cs:11`, `src/Mira.Desktop/Themes/Cinema.xaml:2` | Les réglages Windows « Effets d'animation », « Taille du texte » et contraste élevé sont sans effet (Mira web, lui, respecte `prefers-reduced-motion`). | Suivre `SystemParameters.ClientAreaAnimation` par défaut ; tailles en ressources × `UISettings.TextScaleFactor` ; respecter `HighContrast`. | L |
| UX-04 | P2 | UX | `src/Mira.Desktop/MainWindow.xaml.cs:380`, `:395`, `src/Mira.Desktop/MainWindow.Player.cs:204` | Le toast est le seul retour pour des erreurs importantes (lecture impossible, progression perdue, mise à jour prête) et disparaît en 8 s même avec une action ou le focus clavier. | Durée selon le texte, pas de fermeture auto avec une action, pause au focus ; vrai état d'erreur dans le lecteur avec Réessayer/Retour comme Mira web. | M |
| UX-05 | P2 | Accessibilité | `src/Mira.Web/app.css:54`, `:55`, `:60` | Mira web bloque le pincement, fige `text-size-adjust` et fixe toutes les tailles en px : dans l'app de l'écran d'accueil, aucun moyen d'agrandir le texte. | `touch-action: manipulation` (blocage seulement dans le lecteur) ; base `-apple-system-body` et tailles en `rem`, ou un réglage « Taille du texte ». | M |
| SEC-10 | P3 | Sécurité | `src/Mira.Desktop/Services/Updater.cs:184`, `:202`, `SECURITY.md:10` | Mise à jour d'une copie « dossier » : l'arbre décompressé n'est contrôlé que par la taille, contrairement à SECURITY.md (le zip, lui, est vérifié par SHA-256). | Redécompresser depuis le zip vérifié juste avant d'appliquer, ou comparer les CRC32. | S |
| SEC-11 | P3 | Vie privée | `src/Mira.Mac/Services/Profile.cs:43`, `src/Mira.Mac/Views/MainWindow.cs:148`, `src/Mira.Mac/Services/Updates.cs:47` | Mac : jeton en clair (0600), requête GitHub à **chaque lancement** sans réglage, page de mise à jour ouverte sans validation, `.dmg` sans empreinte. | Trousseau macOS ; réglage « Rechercher les nouvelles versions » ; valider l'URL comme sur Windows et lancer `/usr/bin/open` ; publier le SHA-256 du `.dmg`. | M |
| SEC-12 | P3 | Sécurité | `src/Mira.Desktop/TorLink/TorLinkInstallation.cs:35`, `:71`, `src/Mira.Desktop/MainWindow.TorLink.cs:247` | Un TorLink trouvé par heuristique (raccourcis, npm global, **cache npx**) est lancé sans confirmation. | Confirmation avec chemin et version, mémorisée dans `TorLinkPath` ; retirer le cache npx des candidats. | S |
| SEC-13 | P3 | Sécurité | `src/Mira.Core/JellyfinSetup.cs:45`, `:54`, `:78` | Lecture accordée à Network Service sur toute la racine choisie, jamais retirée. | Limiter à Films/Séries/Animes créés par Mira ; refuser Documents, Bureau, OneDrive ; proposer le retrait. | S |
| SEC-14 | P3 | Sécurité | `src/Mira.Core/TorLink/TorLinkState.cs:176`, `src/Mira.Core/TorLink/Bencode.cs:101` | Un `.torrent` piégé de 64 Mo coûte ~1,2 Go et 10 s à analyser (mesuré), jusqu'à 6 fois. | Limite à ~10 Mo, plafond de nœuds, sauter `pieces` sans copier. | S |
| SEC-15 | P3 | Sécurité | `src/Mira.Jellyfin/WebController.cs:17`, `src/Mira.Web/index.html:24` | CSP `style-src 'unsafe-inline'` pour une seule balise `<style>`. | Empreinte `sha256-…` calculée au chargement ; point « aucune violation CSP » dans `check.mjs`. | S |
| SEC-16 | P3 | Sécurité | `src/Mira.Web/js/views/login.js:41`, `src/Mira.Web/js/api.js:123` | Connexion de Mira web en HTTP clair sans avertissement hors réseau privé ou Tailscale. | Avertissement sur l'écran de connexion selon `location.protocol` et l'hôte. | S |
| SEC-17 | P3 | Sécurité | `tools/web/check.mjs:15` | Identifiants par défaut `sasou` / `premier` dans le script de contrôle, peut-être réels. | Exiger les arguments ou reprendre ceux de la CI ; si `premier` a servi, change ce mot de passe. | S |
| DEP-09 | P3 | Build/CI | `tools/release.ps1:103`, `:114`, `.github/workflows/mac.yml:87` | Publication non atomique (extension, manifeste et `.dmg` ajoutés après), CI du commit non vérifiée, `main` et tags non protégés, releases modifiables. | Release en brouillon, workflows lancés sur le brouillon, vérification des 11 fichiers puis publication ; releases immuables et rulesets. | M |
| DEP-10 | P3 | Build/CI | `tools/release.ps1:50`, `tools/package.ps1:42` | La release est construite sur le PC depuis l'arbre de travail : les fichiers non suivis sont compilés ou embarqués. | Refuser les non suivis ou construire depuis un `git worktree` propre ; à terme, binaires construits en CI avec attestation de provenance. | S |
| DEP-11 | P3 | Build/CI | `.github/workflows/ci.yml:23`, `.github/workflows/mac.yml:38`, `.github/workflows/web.yml:143` | Actions v4 en Node 20 forcées en Node 24 ; `${{ … tag_name }}` interpolé dans `run:` ; jeton `contents: write` laissé dans `.git/config` pendant le build. | Mettre à jour les SHA ; passer par `env:` ; `persist-credentials: false`. | S |
| ROB-07 | P3 | Robustesse | `src/Mira.Desktop/MainWindow.xaml.cs:338`, `:377`, `src/Mira.Core/SyncService.cs:94` | « Hors connexion » est remplacé par « Connecté » 3 s après une erreur réseau (la boucle de synchro réécrit la ligne d'état). | État de connexion distinct, prioritaire dans `SyncChanged`. | S |
| ROB-08 | P3 | Robustesse | `src/Mira.Desktop/Services/ImageCache.cs:107`, `:122`, `src/Mira.Mac/Services/Images.cs:95` | Disque plein : l'image téléchargée est jetée parce que l'écriture du cache échoue avant le décodage. | Écriture du cache au mieux, décodage depuis la mémoire. | S |
| ROB-09 | P3 | Robustesse | `src/Mira.Desktop/Services/LocalProfile.cs:31`, `:39` | `settings.json` illisible → réglages remis à zéro sans le dire, puis écrasés ; `UnauthorizedAccessException` non attrapée. | Copie `.bad-<date>` + avis ; attraper aussi `UnauthorizedAccessException` (et `IOException` pour la session). | S |
| ROB-10 | P3 | Robustesse | `src/Mira.Desktop/MainWindow.Player.cs:381`, `:243`, `installer/Mira.iss:41` | Arrêt de Windows ou Restart Manager de l'installateur : la fermeture asynchrone est coupée ; volume, réglages et lectures récentes perdus. | Faire avant le premier `await` ce qui doit survivre ; `SessionEnding` synchrone et borné. | S |
| ROB-11 | P3 | Robustesse | `src/Mira.Desktop/Services/UpdateApplier.cs:124`, `:136`, `:147` | Mise à jour d'un dossier zip coupée net (courant) : la copie de retour arrière est effacée au prochain essai. | Marqueur « en cours » dans `rollback`, restauration automatique au démarrage. | M |
| ROB-12 | P3 | Windows | `src/Mira.Desktop/Playback/MpvEngine.cs:164`, `src/Mira.Desktop/MainWindow.Player.cs:192` | Veille/reprise non gérées : un flux mort pendant la veille peut finir en erreur, voire être compté comme **vu** si mpv le termine normalement (non vérifié). | `PowerModeChanged` : à la reprise, ping puis rechargement à la position ; `stop-screensaver=yes` explicite. | M |
| ROB-13 | P3 | Robustesse | `src/Mira.Web/js/api.js:93`, `:108`, `src/Mira.Web/js/components.js:153` | Réponse non JSON (portail captif) affichée telle quelle en anglais ; `ping()` croit le serveur revenu sur tout 2xx. | `try` autour de `JSON.parse` avec message français ; `ping()` exige « Jellyfin ». | S |
| ROB-14 | P3 | Robustesse | `src/Mira.Jellyfin/HlsController.cs:89`, `:102` | Proxy HLS : délai de 100 s, `TaskCanceledException` non interceptée (500 sans essayer l'adresse suivante). | Délai de 8 s par adresse, annulation traitée comme injoignable. | S |
| ROB-15 | P3 | Robustesse | `src/Mira.Jellyfin/WebFiles.cs:41` | Fichier d'extension inconnue ignoré en silence (piège déjà rencontré avec `.mjs`). | Erreur ou journal ; test « chaque fichier de Mira.Web est servi ». | S |
| WIN-03 | P3 | Windows | `src/Mira.Desktop/MainWindow.Windows.cs:23`, `src/Mira.Desktop/Services/InstanceActivation.cs:36`, `:42` | Relance pendant la fermeture : sans effet ; relance pendant le démarrage : faux « Mira est déjà ouvert ». | Réponse « closing » puis attente du mutex ; activation mise en attente jusqu'à la fenêtre. | S |
| WIN-04 | P3 | Performance | `src/Mira.Desktop/MainWindow.xaml.cs:182`, `src/Mira.Desktop/MainWindow.Catalog.cs:94`, `src/Mira.Desktop/MainWindow.Player.cs:77`, `src/Mira.Desktop/Services/UpdateLock.cs:24` | E/S synchrones sur le thread de l'interface : ouverture SQLite, relecture du cache à chaque navigation, `File.Exists` sur un chemin réseau possible, `Thread.Sleep` de `WaitIdle`. | `await Task.Run(...)` ; fenêtre d'attente pendant `WaitIdle`. | S |
| WIN-05 | P3 | Windows | `src/Mira.Core/JellyfinStartup.cs:58`, `:59`, `src/Mira.Desktop/MainWindow.Guide.cs:179` | Service Jellyfin forcé en démarrage immédiat (le différé est présenté comme un défaut) et relancé chaque minute sans limite. | Accepter le différé ; arrêter après trois échecs. | S |
| WIN-06 | P3 | Windows | `installer/Mira.iss:112`, `:129`, `src/Mira.Core/JellyfinStartup.cs:58` | La désinstallation laisse sans le dire les règles de pare-feu, le réglage du service, Jellyfin, `%TEMP%\.net\Mira\*` ; la question sur `data` ne cite ni Node, ni TorLink, ni la taille. | Lister ce qui reste et comment le retirer ; bouton « Ne plus ouvrir Jellyfin au réseau » ; nettoyage des extractions du portable. | M |
| WIN-07 | P3 | UX | `src/Mira.Desktop/MainWindow.MiniPlayer.cs:49`, `:61` | Coins du mini-lecteur figés au DPI d'entrée après un passage entre écrans. | `OnDpiChanged` → `SetCornerRadius` et repositionnement de la couche. | S |
| WIN-08 | P3 | Windows | `src/Mira.Desktop/Playback/MpvEngine.cs:50`, `:54` | Le mpv de Jellyfin MPV Shim (Program Files (x86)) est accepté sans contrôle d'architecture. | Lire l'en-tête PE et ignorer tout ce qui n'est pas x64. | S |
| WIN-09 | P3 | Windows | `src/Mira.Desktop/MainWindow.xaml:3`, `:386` | Barre de titre maison : pas de dispositions d'ancrage (Snap Layouts) de Windows 11, pas de survol rouge sur Fermer. | `WM_NCHITTEST` → `HTMAXBUTTON` ; survol `#C42B1C`. | M |
| PERF-02 | P3 | Performance | `src/Mira.Desktop/MainWindow.Carousel.cs:109`, `:113`, `:116` | Le carrousel reste abonné à `CompositionTarget.Rendering` (un rappel par image, 60-144 Hz) quand il est en pause (souris dessus, page défilée), et sans plafond de fréquence le reste du temps. CPU **estimé** faible ; « Défilement automatique » désactivé coupe tout. | Détacher le rappel en pause ; `Timeline.DesiredFrameRate = 30` ou `DispatcherTimer` pour la barre. | S |
| PERF-05 | P3 | Performance | `src/Mira.Desktop/MainWindow.xaml.cs:27`, `:78` | Actualisation de secours toutes les 45 s même WebSocket connecté, qui réécrit tout le cache même sans changement. | Seulement WebSocket déconnecté (ou 5 min) ; ne pas réécrire un cache identique. | S |
| PERF-06 | P3 | Performance | `src/Mira.Core/SyncService.cs:94`, `src/Mira.Desktop/MainWindow.Player.cs:169` | Boucle de synchro toutes les 3 s en permanence (requête SQLite et un pinceau neuf sur le thread de l'interface à chaque tour) ; progression envoyée toutes les 3 s même en pause. | Synchro sur signal ; en pause, un rapport au changement d'état ; 10 s en lecture comme jellyfin-web. | S |
| QUA-06 | P3 | Qualité | `src/Mira.Desktop/Playback/MpvEngine.cs:93`, `src/Mira.Mac/Playback/MpvPlayer.cs:112`, `src/Mira.Desktop/Views/Icon.cs:17` | Client libmpv, tracés d'icônes, passages/épisode suivant et constantes dupliqués entre Windows et Mac. | `MpvClient`, `SkipPlanner`, `IconPaths` dans Mira.Core, testés. | M |
| QUA-07 | P3 | Build/CI | `.editorconfig:3`, `Directory.Build.props:1`, `.github/workflows/ci.yml:54` | `dotnet format` échoue (le style réel contredit `.editorconfig`) et rien ne le vérifie ; seuls les avertissements de l'extension sont bloquants. | Aligner `.editorconfig` sur le style voulu (pas de reformatage massif) ; `dotnet format --verify-no-changes` en CI ; `TreatWarningsAsErrors` global. | S |
| QUA-08 | P3 | Qualité | `src/Mira.Desktop/MainWindow.Carousel.cs:21`, `:138` | Code mort : `_heroRequestedId` jamais lu, `LoadHeroImageAsync` ignore son paramètre. | Supprimer. | S |
| UX-06 | P3 | UX | `src/Mira.Desktop/MainWindow.xaml.cs:78`, `:338` | Hors connexion : aucun indicateur persistant sur Windows, et le même toast revient toutes les 45 s. | Pastille ou bandeau persistant comme Mira web ; toast au premier passage seulement, puis « Reconnecté ». | S |
| UX-07 | P3 | UX | `src/Mira.Desktop/Themes/Cinema.xaml:2`, `src/Mira.Desktop/MainWindow.xaml.cs:40` | Design system Windows quasi absent : 4 tokens (2 inutilisés), 167 couleurs en dur, 25 tailles, palettes Windows/Mac/web divergentes. | Reprendre les tokens de Mira web comme source unique dans `Cinema.xaml` et `App.axaml` ; pinceaux gelés en cache. | L |
| UX-08 | P3 | Accessibilité | `src/Mira.Desktop/MainWindow.xaml:195`, `:202`, `:27`, `src/Mira.Mac/App.axaml:10` | 4 petits textes sous 4,5:1 (GENRES 10 px 4,36, version 3,71, aide de recherche 4,25, Mac « Faint » 3,98). | « faint » = `#8A8A90` (5,9:1) partout ; intitulés en 11-12 px. | S |
| UX-09 | P3 | Accessibilité | `src/Mira.Desktop/MainWindow.Details.cs:274`, `src/Mira.Desktop/MainWindow.xaml:214`, `src/Mira.Desktop/Themes/Cinema.xaml:82` | Focus clavier invisible sur les liens de personnes, le curseur des sous-titres et l'Expander (pointillé noir sur fond noir). | `FocusVisualStyle` sur Slider/Expander, gabarit d'Expander thémé, style de focus pour Hyperlink. | S |
| UX-10 | P3 | Accessibilité | `src/Mira.Desktop/MainWindow.xaml:6`, `:301`, `src/Mira.Desktop/MainWindow.PlayerControls.cs:180` | Rail après les 60 affiches dans l'ordre de tabulation, pas de flèches dans la grille, raccourcis du lecteur différents du Mac (J/L/N absents). | Rail avant la bibliothèque dans le XAML ; navigation directionnelle ; Ctrl+1..5 ; aligner les deux lecteurs. | M |
| UX-11 | P3 | UX | `src/Mira.Desktop/App.xaml.cs:71`, `:77` | Erreur imprévue : boîte système blanche, icône « Information », même texte partout, sans limite de répétition. | Toast ou boîte dans la charte avec « Ouvrir le journal », une boîte par minute au plus. | S |
| UX-12 | P3 | UX | `src/Mira.Desktop/MainWindow.Guide.cs:275`, `:277`, `src/Mira.Desktop/MainWindow.Player.cs:117`, `src/Mira.Core/JellyfinClient.cs:399` | Quelques messages faux ou bruts : « seul un administrateur voit ces dossiers » pour un serveur injoignable, message .NET en anglais, « code 503 » au lieu de « Jellyfin démarre ». | Distinguer réseau / droits ; traduire 503 et 5xx ; `ex.Message` seulement pour les exceptions de Mira. | S |
| UX-13 | P3 | Accessibilité | `src/Mira.Desktop/MainWindow.Carousel.cs:82` | Favori du bandeau signalé seulement par une teinte proche du blanc. | Cœur plein pour l'état favori. | S |
| UX-14 | P3 | Accessibilité | `src/Mira.Web/js/views/search.js:13`, `src/Mira.Web/js/components.js:221`, `src/Mira.Web/js/views/home.js:93` | Mira web : résultats entiers en région live, rôles ARIA incomplets des feuilles et saisons, bandeau sans pause au focus. | Annonce « 12 résultats » ; rôles corrects ; arrêt du bandeau au focus ; focus sur le titre après `swap`. | S |
| DOC-01 | P3 | Documentation | `SECURITY.md:9`, `:10`, `:24`, `:25`, `docs/ARCHITECTURE.md:109` | SECURITY.md ne correspond plus au code : rien sur le service, le pare-feu, l'ACL Network Service, l'extension non signée, npm ; ARCHITECTURE.md:109 dit « aucun service ajouté ». | Réécrire SECURITY.md à partir de l'inventaire réseau de ce rapport, par composant. | S |
| DOC-02 | P3 | Documentation | `docs/ARCHITECTURE.md:7`, `docs/testing/latest-results.txt:1`, `src/Mira.Desktop/MainWindow.VisualCheck.cs:82`, `README.md:18` | Arbre de `MainWindow` incomplet, résultats de tests de la 0.5.2 ; captures transparentes (illisibles en thème clair sur GitHub) et périmées (0.4.9 / 0.5.2). | Compléter l'arbre ; résultats régénérés à chaque release ; fond `#070708` dans `CaptureAsync` puis captures 0.7.3. | S |

## Plan de correction

Aucun P0 : je propose de commencer par les échéances, puis par ce qui peut bloquer ou exposer. Chaque lot est
indépendant, tient dans une PR et se vérifie seul.

| Lot | Contenu | Vérification | Effort |
|---|---|---|---|
| **1. Dépendances urgentes et CI** | DEP-02 (SQLite 8.0.31), DEP-05 (audit + Dependabot), DEP-06 (`macos-15`), DEP-11, QUA-07 (`.editorconfig` aligné, format et warnings bloquants en CI), SEC-17 | `dotnet list package --vulnerable --include-transitive` vide ; 3 workflows verts ; `dotnet format --verify-no-changes` OK | S-M |
| **2. .NET 10** | DEP-01 (extension gardée en `net8.0`) | CI Windows (77 tests, captures, `update-check`), self-check Mac, Safari ; extension chargée par Jellyfin 10.9 et 12.1 | M |
| **3. Mira ne reste jamais bloqué** | ROB-01, ROB-02, ROB-03, ROB-04, ROB-09, UX-01 | Nouveaux tests : page SQLite écrasée, serveur muet, `settings.json` illisible ; sur ton PC : commandes ROB-01 de la section 7 | S-M |
| **4. Exposition réseau et jetons** | SEC-03, SEC-05, SEC-06, SEC-07, SEC-01, SEC-16, SEC-15, SEC-11 | Test administrateur de la CI sur les règles (profils, port) ; points `check.mjs` (déconnexion, serveur étranger, CSP) | M |
| **5. Chaîne d'approvisionnement** | DEP-03, SEC-02, DEP-08, SEC-08, DEP-04, SEC-04, SEC-10, SEC-12, SEC-14, ROB-11, DEP-09, DEP-10 | `release.ps1 -DryRun` (signature vérifiée) ; test « verrou npm présent et intègre » ; mise à jour d'essai (`update-check`) | L |
| **6. Intégration Windows** | WIN-01, WIN-02, WIN-03, WIN-05, WIN-06, WIN-07, WIN-08, ROB-10, ROB-12, SEC-13, DEP-07, SEC-09 (signature, selon ton choix) | Tests `ScreenFit` multi-écran ; sur ton PC : commandes de la section 7 (multi-écran, veille, désinstallation) | L |
| **7. Mira web : robustesse et performance** | ROB-06, ROB-13, ROB-14, ROB-15, PERF-03, PERF-04, UX-05, UX-14 | `check.mjs` (stockage bloqué, réponse HTML, 3 000 films) ; Safari 34/34 ; essai sur ton iPhone | M |
| **8. Performance Windows** | PERF-01, PERF-02, PERF-05, PERF-06, WIN-04, ROB-07, ROB-08 | Mesures avant/après sur ton PC (RAM, CPU, écritures : section 7) | M |
| **9. Accessibilité et UX Windows** | UX-02, UX-03, UX-04, UX-06, UX-08, UX-09, UX-10, UX-11, UX-12, UX-13, WIN-09 | UIAutomation (noms), Narrateur, réglages Windows d'animation et de texte, captures | L |
| **10. Structure et tests** | QUA-03, QUA-04, QUA-02, QUA-01, QUA-05, QUA-06, ROB-05, QUA-08, UX-07 | Tests portables lancés aussi sous Linux en CI ; couverture mesurée ; comportement inchangé (captures identiques) | L |
| **11. Documentation** | DOC-01, DOC-02 | Relecture | S |

## 7. Mesures à faire sur ton PC (PowerShell 5.1)

```powershell
# Architecture de l'exe (8664 = x64) et absence de démarrage automatique de Mira
$b=[IO.File]::ReadAllBytes("$env:LOCALAPPDATA\Programs\Mira\Mira.exe"); $o=[BitConverter]::ToInt32($b,0x3C); '{0:X4}' -f [BitConverter]::ToUInt16($b,$o+4)
Get-CimInstance Win32_StartupCommand | Where-Object Command -match 'Mira'; Get-ScheduledTask | Where-Object { $_.Actions.Execute -match 'Mira' }

# Runtime embarqué (DEP-01), composants installés (DEP-04), signature (SEC-09)
(Get-Item "$env:LOCALAPPDATA\Programs\Mira\System.Private.CoreLib.dll").VersionInfo.ProductVersion
Get-Content "$env:LOCALAPPDATA\Programs\Mira\data\mpv\version.txt","$env:LOCALAPPDATA\Programs\Mira\data\node\version.txt" -ErrorAction SilentlyContinue
Get-AuthenticodeSignature "$env:LOCALAPPDATA\Programs\Mira\Mira.exe" | Format-List Status,StatusMessage

# Temps jusqu'à la fenêtre (répéter 5 fois)
$p=Start-Process "$env:LOCALAPPDATA\Programs\Mira\Mira.exe" -PassThru; while ($p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 50; $p.Refresh() }; (Get-Date) - $p.StartTime

# RAM et CPU au repos (Mira ouvert depuis 2 min, sans lecture) ; refaire réduit, après 5 « Afficher plus », vidéo en pause
$p=Get-Process Mira; $t0=$p.TotalProcessorTime; Start-Sleep 60; $p.Refresh(); 'CPU {0:N2} % d''un cœur ; privé {1:N0} Mo ; working set {2:N0} Mo' -f (($p.TotalProcessorTime-$t0).TotalMilliseconds/600), ($p.PrivateMemorySize64/1MB), ($p.WorkingSet64/1MB)
Get-Counter "\Process(Mira)\IO Write Bytes/sec" -SampleInterval 5 -MaxSamples 24

# Pare-feu et profil réseau (SEC-03)
Get-NetFirewallRule -DisplayName 'Mira - Jellyfin' | ForEach-Object { [pscustomobject]@{ Profil=$_.Profile; Distant=($_ | Get-NetFirewallAddressFilter).RemoteAddress -join ','; Port=($_ | Get-NetFirewallPortFilter).LocalPort } }
Get-NetConnectionProfile | Select-Object InterfaceAlias,NetworkCategory

# Service Jellyfin (WIN-05) et extension mise à jour seule (SEC-02)
sc.exe qc JellyfinServer; sc.exe qfailure JellyfinServer
Get-ChildItem "$env:ProgramData\Jellyfin\Server\plugins" -Filter meta.json -Recurse | ForEach-Object { Get-Content $_.FullName | ConvertFrom-Json } | Where-Object name -eq 'Mira' | Select-Object version,autoUpdate,status

# Connexions sortantes réelles de Mira (et de node pendant TorLink)
Get-NetTCPConnection -State Established -OwningProcess (Get-Process Mira).Id | Select-Object RemoteAddress,RemotePort -Unique

# Veille (ROB-12) : en administrateur, vidéo en cours → Mira.exe doit apparaître sous DISPLAY et SYSTEM
powercfg /requests

# Processus fantôme (ROB-01) : après une fermeture, un Mira avec MainWindowHandle = 0 confirme le défaut
Get-Process Mira -ErrorAction SilentlyContinue | Select-Object Id,MainWindowHandle,StartTime

# Restes après désinstallation (WIN-06)
Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Mira.lnk","$env:LOCALAPPDATA\Programs\Mira","HKCU:\Software\Classes\AppUserModelId\Mira.Desktop"; Get-NetFirewallRule -DisplayName 'Mira - Jellyfin' -ErrorAction SilentlyContinue; Get-ChildItem "$env:TEMP\.net\Mira" -ErrorAction SilentlyContinue
```

## Sources
- .NET 8 et 9 : fin de support le 10/11/2026 — <https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/>, <https://raw.githubusercontent.com/dotnet/core/main/release-notes/8.0/releases.json>
- Retrait de `macos-14` — <https://github.blog/changelog/2026-10-01-github-actions-macos-14-runner-image-retirement>
- SQLite : GHSA-2m69-gcr7-jv3q — <https://github.com/advisories/GHSA-2m69-gcr7-jv3q> ; `ip` : <https://github.com/advisories/GHSA-2p57-rm9w-gvfp>
- Options de signature, Artifact Signing (particuliers : États-Unis et Canada), EV sans effet SmartScreen — <https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options>, <https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation>, <https://learn.microsoft.com/azure/artifact-signing/quickstart>
- Compte développeur Store gratuit pour les particuliers — <https://learn.microsoft.com/windows/apps/publish/whats-new-individual-developer>
- SignPath Foundation — <https://signpath.org/terms>, <https://about.signpath.io/product/open-source>
- Releases immuables GitHub — <https://github.blog/changelog/2025-10-28-immutable-releases-are-now-generally-available/>
