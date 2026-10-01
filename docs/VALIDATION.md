# Validation — 1er octobre 2026

Les chemins `.artifacts/...` cités ci-dessous désignent les preuves de validation locales, exclues du dépôt. La galerie publique utilise uniquement le mode démonstration. Un récapitulatif sans données personnelles est conservé dans [testing/latest-results.txt](testing/latest-results.txt). Les tests de base et la construction de l’archive sont aussi exécutés par GitHub Actions.

## Accueil, guide et nouveautés 0.5.5

- Choix de l’écran à l’ouverture testé :
  - profil neuf → bienvenue ;
  - profil existant sans trace (mise à jour depuis 0.5.4 ou avant) → nouveautés de la version en cours ;
  - version vue → rien ; version plus récente vue → rien ;
  - versions sautées → les deux dernières, au plus.
- Tests : points forts courts pour chaque version, entrée présente pour la version en construction, icônes existantes dans Mira, texte de release (résumé, points, lien vers le CHANGELOG de la version, moins de 1 500 caractères), choix de l’adresse réseau (adaptateurs virtuels et 169.254 écartés, passerelle d’abord).
- Captures de la CI Windows (galerie de démonstration, hors ligne) : bienvenue, nouveautés (une version, puis deux dans une fenêtre de 1024 × 720), guide (haut et bas) et bibliothèque vide, relues avant la publication.
- Non vérifié : l’ouverture des dossiers dans l’Explorateur et de la page de Jellyfin dans le navigateur, la copie de l’adresse et la détection de l’adresse réseau sur un vrai PC.

## Installation de Jellyfin : corrections 0.5.5

- Premier essai réel, sur le PC de l’utilisateur (Windows 11, 10.0.26200) : l’installateur de Jellyfin a affiché « Could not start the Jellyfin Server service », et Mira s’est arrêté.
  - Le journal Application de Windows ne montre pourtant qu’un démarrage du service, réussi (nssm : « Démarrage réussi », 19 h 37). Le message de l’installateur était donc une fausse alerte : `nssm start` rend la main avant que le service tourne.
  - Au second essai, Mira a trouvé Jellyfin en marche et terminé la configuration : connexion du compte et analyse des bibliothèques visibles dans le journal de Jellyfin.
  - Le même journal montrait « Token is required » sur `/socket` toutes les 30 s : l’écoute en temps réel de Mira était refusée.
- Écoute en temps réel, reproduite sur un vrai Jellyfin 12.1 :
  - `/socket?api_key=…` reçoit 403 et le journal écrit « Token is required ». Le code de 12.1 n’accepte `api_key` qu’avec `EnableLegacyAuthorization`, désactivé par défaut.
  - Avec la session dans l’en-tête `Authorization`, la connexion est acceptée (« WS request » dans le journal).
  - Nouveau test : un serveur WebSocket local vérifie que l’adresse est `/socket` sans jeton, que l’en-tête porte la session et qu’un changement de bibliothèque arrive. Il échoue sur l’ancien code.
- Relu dans le script de l’installateur (`jellyfin.nsi`) :
  - pas de `SetRegView 64` : la clé `Software\Jellyfin\Server` est écrite sous `WOW6432Node`, que Mira ne lisait pas ;
  - en mise à jour silencieuse d’un Jellyfin existant, l’installateur continue sans demander (`/SD IDOK`) ;
  - ses messages d’erreur (`ShowError`) s’affichent même en silencieux.
- Observé sur un vrai Jellyfin 12.1 au démarrage, réponses de `System/Info/Public` dans l’ordre :
  - rien ;
  - 200 en camelCase avec `startupWizardCompleted: false`, de sa page de démarrage ;
  - 503 « Jellyfin Server is loading » ;
  - 200 du serveur, en PascalCase.
  L’ancienne détection voyait la deuxième réponse comme « autre programme ». La nouvelle passe par rien, en démarrage, prêt. Avec elle, l’essai complet lancé pendant le démarrage aboutit : attente, assistant, connexion, trois bibliothèques. Il a été fait avec un nom accentué et un chemin contenant une virgule.
- Nouveaux tests :
  - détection : rien, 503 avec ou sans texte, page de démarrage de Jellyfin 12, page web, 404, JSON sans `Id`, serveur prêt ;
  - port occupé ou libre ;
  - journal de Jellyfin : cause d’un échec de démarrage (exception de Kestrel) lue, erreurs ordinaires, échecs plus anciens et vieux fichiers ignorés.
- Non vérifié : la lecture du registre 32 bits et du service sur Windows (pas de Windows dans le conteneur) ; la cause réelle de l’échec chez l’utilisateur.

## Jellyfin installé par Mira 0.5.4

- Release v0.5.4 publiée (stable) : 8 fichiers en ligne. Le `mira-update.json` publié annonce la 0.5.4. La taille et le SHA-256 des trois paquets y sont ceux que donne GitHub. Sa signature, téléchargée depuis la release, est acceptée par `UpdateSignature.Verify` avec `UpdateKeys.Trusted`.
- Comportement de l’installateur relevé dans son script NSIS (`jellyfin-server-windows`) : en silencieux (`/S`), service `JellyfinServer` via nssm sous Network Service, démarré à la fin ; clé `HKLM\Software\Jellyfin\Server\InstallFolder` ; élévation requise. Adresse et SHA-256 de `jellyfin_12.1_windows-x64.exe` repris du manifeste winget `Jellyfin.Server` 12.1.
- Essai réel de `JellyfinSetup` contre un Jellyfin 12.1 neuf, compilé depuis ses sources sous Linux :
  - assistant terminé ; compte administrateur créé ; bibliothèques Films (movies), Séries et Animes (tvshows) sur les bons chemins ; langue fr/FR ; accès distant désactivé ;
  - `MediaLibraries.FromJellyfin` retrouve les trois dossiers pour TorLink ;
  - une reprise avec le même compte aboutit ; avec un autre mot de passe, elle est refusée proprement ;
  - pendant son démarrage, le serveur répond 503 : ce cas est géré.
- Nouveaux tests :
  - assistant rempli par un faux serveur aux réponses relevées sur Jellyfin 12.1 ;
  - reprise sans bibliothèque en double ; autre mot de passe refusé ; échec d’une bibliothèque signalé ;
  - compte invalide refusé sans requête ; chemin avec virgule ; serveur absent ou autre programme sur le port.
  - Sous Windows (CI) : droit de lecture du service sur la racine, puis sur un fichier lié et un fichier déplacé ; pas d’ajout de droit dans un dossier qui ne le donne pas.
- Non vérifié :
  - le téléchargement depuis `repo.jellyfin.org` (inaccessible depuis le conteneur) ;
  - l’installation silencieuse et l’autorisation Windows sur un vrai PC ;
  - la lecture des dossiers par le service ;
  - l’écran lui-même.

## Moteur mpv installé par Mira 0.5.4

- Build figée : `mpv-dev-x86_64-20260927-git-a1bf4b6559.7z` (31 487 676 octets). Téléchargée depuis `downloads.sourceforge.net` (redirection vers un miroir) : ses SHA-1 et MD5 sont ceux que publie SourceForge pour ce fichier. SHA-256 figés : archive `3a80c48d…74ac5c`, `libmpv-2.dll` `0a81c004…de6f6a` (120 812 544 octets, identique à l’extraction par 7-Zip).
- Essai réel sous Linux avec le code de Mira : téléchargement, vérifications et extraction en 10 s ; il ne reste dans `data\mpv` que la bibliothèque, `version.txt` et `SOURCE.txt` ; un second appel ne télécharge rien.
- Nouveaux tests : installation depuis une petite archive au format des builds mpv, aucun nouveau téléchargement une fois installé, archive altérée, trop longue, bibliothèque d’une autre empreinte ou fichier absent (404) jamais installés et sans fichier laissé ; ordre de recherche (chemin choisi, puis moteur de Mira).
- Non vérifié : l’installateur avec la case cochée sur un vrai Windows, le chargement de cette `libmpv-2.dll` par Mira et une lecture (pas de Windows dans le conteneur), l’interface (bouton, avis à la première lecture).

## Stabilité 0.5.4

- Revue du code à la recherche de pannes possibles. Corrigées : erreurs SQLite remontées jusqu’au lecteur (fenêtre d’erreur répétée pendant la lecture), verrou d’envoi jamais rendu après une erreur de stockage (fermeture bloquée), base locale abîmée qui empêchait d’ouvrir le compte, écoute WebSocket arrêtée par un message inattendu ou relancée toutes les 2 s, identifiant d’appareil vide, commande mpv sur un lecteur déjà détruit.
- Nouveaux tests : stockage en panne pendant la synchronisation (aucune exception vers le lecteur, erreur affichée, fermeture en moins de 10 s), base illisible mise de côté et recréée, identifiant d’appareil vide remplacé. Les deux premiers échouent sur le code précédent : exception SQLite, puis « file is not a database ». Un essai séparé sous Linux montre la fermeture **bloquée** avec l’ancien `SyncService` quand le stockage tombe en panne en cours de session, et terminée avec le nouveau.
- Tests de `Mira.Core` lancés sous Linux ; code WPF et programme de tests compilés sans avertissement contre les assemblages de référence de Windows Desktop. La CI Windows de la pull request donne le résultat de la suite complète.
- Non vérifié : l’écoute WebSocket (aucun serveur WebSocket de test), la garde de `MpvEngine.Command` (libmpv absent du conteneur) et l’interface elle-même.

## Connexion, fiches et caches 0.5.3

- GitHub Actions sur Windows (exécution manuelle de la branche, [run 36787107476](https://github.com/sasou-web/Mira/actions/runs/36787107476)) : compilation Release sans erreur ni avertissement, **51 tests** réussis, archive, exécutable portable et installateur produits. Nouveaux tests : adresses saisies (sans schéma, avec port, IPv6, copiée depuis la page web, serveur nommé « web ») ; recherche du serveur (ordre HTTPS → 8096 → HTTP, redirection vers HTTPS conservée, HTTPS muet abandonné après le délai de grâce, Emby, 404, Jellyfin 10.8, certificat refusé, serveur injoignable, seule `System/Info/Public` demandée) ; distribution, réalisation, titres similaires et tri par dernière lecture ; cache disque (budget, fichiers les moins récemment vus, `.tmp` abandonnés, fichier relu « touché ») ; élagage SQLite (pages anciennes oubliées ; accueil, reprise, historique et envoi en attente gardés) ; images en mémoire (budget respecté, les plus anciennes libérées puis relues sur disque sans nouveau téléchargement).
- Les tests de `Mira.Core` concernés ont aussi été exécutés sous Linux, et le code WPF compilé contre les assemblages de référence de Windows Desktop pendant le développement.
- Release v0.5.3 publiée (stable) : 8 fichiers en ligne. Le `mira-update.json` publié annonce la 0.5.3, avec la taille et le SHA-256 des trois paquets tels que GitHub les donne ; sa signature, téléchargée depuis la release, est acceptée par `UpdateSignature.Verify` avec `UpdateKeys.Trusted`. La mise à jour réelle d’une copie installée en 0.5.2 n’a pas encore été observée.
- Non vérifié : l’interface elle-même n’a pas été lancée ni capturée (rangée « Titres similaires », colonne « Avec » / « Réalisation », libellés de connexion), ni essayée avec un vrai serveur Jellyfin, un NAS, un proxy HTTPS ou un certificat auto-signé. Les contrôles du lecteur, de mouvement, Windows et TorLink n’ont pas été relancés ; le code du lecteur n’a pas changé. Les captures de la galerie ne montrent pas encore les nouveautés des fiches.

## Mises à jour automatiques et barre de lecture 0.5.2
- Compilation Release sans erreur ni avertissement ; **45 tests** réussis. Nouveaux : libellés du lecteur (« Épisode 3 / 12 », pistes nommées en français, vitesse, décalage) ; manifeste signé accepté, refusé s’il est modifié, mal formé ou signé par une autre clé ; seule une version signée plus récente est proposée ; chaque redirection vérifiée, reprise d’un téléchargement interrompu, réponse inattendue refusée ; copie en deux temps avec retour arrière, dossier intact si la préparation échoue, résultat annoncé une fois ; type d’installation reconnu et dossier remplacé sans toucher à `data`. Le test du vrai TorLink (`--torlink-integration`) n’a pas été relancé : TorLink n’a pas changé.
- Contrôle de bout en bout `tools/update-check.ps1` : **13 vérifications** réussies, sans GitHub ni la vraie clé. `tools/package.ps1` produit les paquets 0.5.2 et 0.5.3 ; le manifeste 0.5.3 est signé par une clé de test jetable et servi par un faux GitHub local (127.0.0.1) ; chaque Mira a un profil isolé. Archive : 0.5.3 trouvée, téléchargée, vérifiée (signature, taille, SHA-256) et installée à la fermeture ; la nouvelle version se trouve à jour et efface son téléchargement. Exécutable portable : remplacé en place et rouvert en 0.5.3 par « Redémarrer ». Installateur : 0.5.2 installé en silence dans un dossier de test et mis à jour par son installateur à la fermeture ; réinstallé, puis mis à jour par « Redémarrer », avec la progression d’Inno Setup et la réouverture de Mira. `data` reste intact à chaque fois ; désinstallations silencieuses ; raccourci Démarrer et identité Windows de `dist/Mira` rétablis. Résultats : `.artifacts/update-check/result.txt`.
- Deux problèmes trouvés par ces passages, corrigés. Inno Setup complète le nom de produit de l’installateur par des espaces, que la vérification de version refusait. Dans le contrôle lui-même, le désinstalleur d’Inno Setup efface `unins000.exe` en dernier, depuis une copie temporaire : une réinstallation immédiate dans le même dossier perdait son désinstalleur, et Mira s’y mettait à jour (avec succès) comme un simple dossier. Le contrôle attend maintenant la fin du désinstalleur et vérifie que chaque étape passe par son propre type de mise à jour.
- La clé publique intégrée correspond à la vraie clé de signature, conservée hors du dépôt et protégée par DPAPI.
- Release v0.5.2 publiée (préversion) : les 8 fichiers en ligne ont la taille et le SHA-256 des fichiers préparés (empreintes données par GitHub), manifeste signé par la vraie clé. Une copie du même code numérotée 0.5.1, lancée normalement dans un dossier de test, a trouvé cette release sur GitHub, téléchargé l’archive, vérifié signature, taille et SHA-256, puis est passée en 0.5.2 à sa fermeture. Dossier de test supprimé et raccourci Démarrer rétabli ensuite.
- Revue indépendante du diff : **21 remarques** corrigées avant ces passages. Parmi elles : vérification de chaque redirection, reprise des téléchargements, copie en deux temps avec retour arrière, verrous pendant une mise à jour (`SetupMutex` compris), suivi du résultat dans `data/updates/pending.json`, arrêt des essais automatiques après deux échecs, réouverture par Inno Setup même après un échec, menus du lecteur en boutons radio et annonces aux lecteurs d’écran.
- Barre de lecture : **67 contrôles du lecteur** réussis sur la compilation Release, puis sur `dist/Mira` installé (média synthétique chapitré, piste `.srt` externe). Nouveaux : titre, deuxième ligne et temps au-dessus de la barre, boutons en dessous, alignés sur le titre et le temps ; temps « 0:02 / 0:18 » ; barre coupée en 3 segments de chapitres séparés de 3 px ; chapitre en cours nommé au-dessus du temps (« Opening », « Partie A », « Ending ») ; curseur de volume déployé au survol du haut-parleur, puis replié ; niveau affiché après un réglage au clavier ; menu des sous-titres (piste chargée cochée, « Désactivés » transmis à mpv sans fermer le menu, réactivation, fermeture par le même bouton) ; menu ⋮ (vitesses, décalage des sous-titres, informations de lecture). Captures : `.artifacts/player-v052-check`.
- Distribution `dist/Mira` passée en 0.5.2 depuis l’archive 0.5.2 du contrôle, sauvegarde de 0.5.1 dans `.artifacts/previous-mira-0.5.1`. Les 468 fichiers du profil sont inchangés après la copie et après le contrôle du lecteur (SHA-256) ; les 500 fichiers de l’archive sont en place. Raccourcis du projet et du menu Démarrer réenregistrés. Cette copie est reconnue comme un dossier : elle se mettra à jour depuis l’archive des prochaines versions.
- Captures 07 à 10 de la galerie (réglages) refaites en mode démonstration.
- Non vérifié : l’avertissement SmartScreen, l’installation sur un autre PC, un arrêt de Windows pendant une installation, l’assistant interactif et le passage direct d’un menu du lecteur à l’autre à la souris. Les copies 0.5.1 et antérieures n’ont pas de mise à jour automatique : 0.5.2 doit être installée une fois à la main. Contrôles Windows (0.4.9), de mouvement (0.5.0), TorLink de bout en bout et installateur (0.5.1) non relancés.
## Installateur et exécutable portable 0.5.1

- `tools/package.ps1` produit l’archive (75,0 Mo), l’exécutable portable (76,4 Mo) et l’installateur Inno Setup 6.7.3 (53,7 Mo), chacun avec son `.sha256`. Compilation sans avertissement ; **40 tests** réussis. SHA-256 : installateur `472d4c9cf1912114e541c52a856791d1f8d56970ac60cbf095dbee4c38a5c6a6`, portable `3ecd66ad3b37eb8cb5e106981a211d5ad3dd810997bb04e29b247192f20262b4`, archive `326b25364e946c2fb5912695bbde7f5eae0943cd3a0dcb1b089b330e29b959d8`.
- L’exécutable portable publié est identique, octet pour octet, à celui qui a passé les contrôles ci-dessous ; les 478 binaires de l’archive et de l’installateur sont identiques à ceux de la version installée localement.
- Exécutable portable, copié seul dans un dossier vide : les 14 vues de démonstration s’affichent. Le contrôle TorLink y réussit **23 vérifications au clavier réel** : page servie depuis les ressources, saisie, Alt + ←, rangements, reclassement et arrêt. Les **50 contrôles du lecteur** réussissent aussi, avec les liaisons clavier de mpv écrites dans `data/app`. Aucun fichier n’apparaît à côté de l’exécutable en dehors du profil.
- Installateur, en mode silencieux dans un dossier de test : **12 vérifications** réussies. Installation pour l’utilisateur courant sans droits administrateur, entrée dans les applications installées, raccourci Démarrer et identité Windows, démarrage de Mira installé ; réinstallation par-dessus qui garde `data` ; désinstallation qui retire l’application, son entrée, son raccourci et son identité, et garde `data`. Le raccourci Démarrer du `dist/Mira` local est ensuite rétabli. Résultats : `.artifacts/installer-v051-test/result.txt`.
- Avec un autre TorLink ouvert sur le PC, le contrôle vérifie la page (servie depuis les ressources, avec xterm.js et sa feuille de style) et le refus d’une seconde instance, puis s’arrête.
- Non vérifié : les pages de l’assistant et la question de suppression des données en mode interactif, l’avertissement SmartScreen, et l’installation sur un autre PC ou un autre compte Windows.

## TorLink 0.5.0

- Compilation Release complète sans erreur ni avertissement. **40 tests** réussis avec `-- --torlink-integration`, 39 sans le test du vrai TorLink. Nouveaux : noms de sortie réels (films, épisodes, packs, fansubs), sous-titres, bonus et noms Windows, plan de rangement dans des dossiers existants, lien physique et copie entre disques sans remplacement ni écriture hors de la bibliothèque, racine imbriquée jamais supprimée, journal (activation, attente des fichiers, dossier manquant, rechargement, aucun lien magnet), fichier déjà présent jamais déplacé par « Classer », type choisi conservé par « Réessayer », reprise d’un déplacement partiel, journal illisible mis de côté, fichiers exacts d’un `.torrent`, API Jellyfin simulée (une requête pour tous les titres suivis, bibliothèque d’animes reconnue par mot entier), pseudo-console, et TorLink 1.1.1 réel dans la pseudo-console avec un état isolé (`TORLINK_STATE_DIR`).
- Une revue indépendante du diff a relevé, puis vu corriger : fichiers présents adoptés puis déplacés par « Classer », élagage d’une racine imbriquée, reclassement oublié par « Réessayer », déplacement partiel impossible à reprendre, journal écrasé sur erreur de lecture (tous couverts par les tests ci-dessus), ainsi qu’une seconde instance possible par « Ouvrir dans une fenêtre » et des lectures de l’état de TorLink qui pouvaient faire échouer son remplacement atomique (corrigés, sans test dédié).
- Contrôle de bout en bout `tools/torlink-check.ps1` : **23 vérifications** réussies au clavier réel sur la première version installée, puis **22** sur la version finale installée (`dist/Mira`) et sur la compilation Release, avec des événements clavier du navigateur (fenêtre de contrôle sans premier plan). Vrai TorLink dans la page, profil, état TorLink et bibliothèque isolés, mode démonstration sans Jellyfin. Couverts : ouverture depuis le rail, une seule instance, Échap laissé à TorLink, saisie reçue par TorLink et fin affichée, relance, film et sous-titre, fichiers annexes laissés dans TorLink, lien physique (deux liens sur le même fichier), épisode, anime, jeu ignoré, liste « Vers Jellyfin », reclassement en anime avec suppression des dossiers vidés, seconde passe sans doublon, réglages, Alt + ← depuis le terminal, Échap hors du terminal, arrêt de TorLink et aucun processus WebView2 restant après Mira. Résultats et captures : `.artifacts/torlink-check`.
- WebView2 transmet Alt + ← à WPF comme une flèche gauche avec Alt enfoncé, et non comme une touche système : Mira reconnaît les deux formes. Si WPF ne reçoit pas la touche, la page demande elle-même le retour ; ce chemin est vérifié par des événements clavier du navigateur, utilisés quand la fenêtre de contrôle n’a pas le premier plan (22 contrôles réussis sur la compilation Release).
- Lecture seule sur le serveur personnel (Jellyfin 12.1.0) avec le code de Mira (`--jellyfin-live`) : trois bibliothèques lues et associées à Films, Séries et Animes, dossiers présents sur ce PC ; la vidéo de film la plus récente est retrouvée par son chemin exact, comme pour « Voir ». Aucun signalement ni modification n’a été envoyé au serveur.
- Non-régression : **50 contrôles du lecteur** réussis (média synthétique chapitré), rendus et **9 contrôles de mouvement** réussis sur la bibliothèque réelle en lecture seule. En mode démonstration, le contrôle de mouvement échoue au transfert du survol, de la même façon avec la version 0.4.9 installée : le catalogue fictif est trop court pour défiler d’une rangée. Les 24 contrôles Windows n’ont pas été relancés.
- Distribution autonome installée dans `dist/Mira`, sauvegarde de 0.4.9 dans `.artifacts/previous-mira-0.4.9`, puis version finale réinstallée après la revue. Les 468 fichiers du profil sont inchangés après la copie et après le contrôle TorLink de la version installée (SHA-256) ; les 494 fichiers distribués correspondent à la version préparée, page du terminal et `WebView2Loader.dll` compris. Raccourcis du projet et du menu Démarrer réenregistrés.
- Un passage du contrôle a échoué au démarrage du terminal, probablement parce qu’un autre TorLink tournait alors sur ce PC : Mira refuse bien une seconde instance. Le contrôle le signale désormais dès le départ, et la page propose « Réessayer » si le terminal ne s’affiche pas en 20 secondes.
- Archive `Mira-0.5.0-win-x64.zip` (SHA-256 `d3dc735e6d4c1393b3ece40f40c31c614d1f9b995daa5a552b5a41bafe3068ca`) : 510 entrées, aucun fichier de profil. Ses 493 fichiers d’application sont identiques à ceux de la version installée vérifiée ; démarrage et export des 14 vues de démonstration réussis depuis l’archive extraite. Le contrôle TorLink n’y a pas été relancé : un TorLink tournait alors dans le Mira de ce PC, et le contrôle refuse à raison d’en démarrer un second.
- État TorLink réel (`history.json`, `queue.json`, `seeds.json`, `config.json`) et dossiers `MEDIAS` inchangés par les essais.
- Non vérifié : un vrai téléchargement rangé de bout en bout, `Library/Media/Updated` sur le serveur personnel (la surveillance en temps réel de Jellyfin y est activée), le bouton « précédent » de la souris au-dessus du terminal, copier-coller, l’absence de WebView2 (« Ouvrir dans une fenêtre »), un serveur Docker ou distant, et une copie vers un autre disque physique ou un partage réseau (le chemin de copie est testé sur le même volume NTFS).

## Fluidité et reprise 0.4.9

- Distribution autonome installée dans `dist/Mira`, sauvegarde de 0.4.7 dans `.artifacts/previous-mira-0.4.7`. Les 184 fichiers du profil sont inchangés après copie (SHA-256), les 478 fichiers distribués correspondent à la version préparée. Raccourcis du projet et du menu Démarrer vérifiés : exécutable installé, logo dédié et AppUserModelID `Mira.Desktop`.

- Compilation Release sans erreur ni avertissement ; **30 tests** de bibliothèque et synchronisation réussis. Ajouts : ordre chronologique, priorité d’une reprise sur NextUp, activité d’une série après la fin d’un épisode, persistance et fusion sans écraser une date serveur plus récente.
- **50 contrôles du lecteur** réussis sur média synthétique muet : les nouveaux contrôles passent par lecture → arrêt → rangée rendue, puis lecture → mini-lecteur. Ils vérifient que le dernier film regardé passe à gauche et que le défilement horizontal revient au début. Résultats : `.artifacts/player-v049-check/result.txt`.
- **9 contrôles de mouvement** réussis : même valeur animée pour contour et zoom, interpolation, inversion en cours sans saut, transfert du survol sous un pointeur fixe pendant l’inertie, dimensions des cartes stables. Résultats : `.artifacts/visual-v049-final/motion-checks.txt`.
- Rendus de la bibliothèque à 1024, 1440 et 1920 unités WPF ; écrans de connexion et de compte inspectés à 1440 × 900, connexion à 1024 × 700. Indicateurs du carrousel arrondis et progression intermédiaire vérifiés. Rendus : `.artifacts/visual-v049-final`.
- Essai à la souris dans la fenêtre de validation : survol d’une carte « Continuer à regarder », molette depuis cette carte, transfert à une affiche puis à la rangée suivante sans déplacement du pointeur. Aucun média personnel lu ou modifié.
- **24 contrôles Windows** réussis à nouveau, avec le courtier multimédia réel et la vidéo synthétique. Résultats : `.artifacts/windows-v049-check/result.txt`. Les limites de vérification visuelle d’Explorer décrites ci-dessous restent applicables.
- Le classement utilise les dates Jellyfin disponibles et conserve les nouvelles lectures localement. Il ne reconstitue pas les dates manquantes des épisodes terminés avant cette version. Les contrôles vérifient la continuité des animations ; ils ne constituent pas une mesure de cadence sur toutes les configurations.

## Intégration Windows 0.4.8

- Compilation Release sans erreur ni avertissement ; **27 tests** de bibliothèque, synchronisation et comportement réussis.
- **24 contrôles Windows** réussis via le véritable courtier multimédia Windows et libmpv sur vidéo synthétique muette : publication du titre, épisode et affiche lisible, pause/reprise répétées idempotentes, seek système aller/retour, commandes de dix secondes, disponibilité du suivant, lecture de l’épisode suivant, réduction en zone de notification, commande pendant que la fenêtre est masquée, restauration par un second processus et nettoyage à l’arrêt/fermeture. Résultats : `.artifacts/windows-v048-final/result.txt`.
- **48 contrôles du lecteur** réussis, dont plein écran, mini-lecteur, raccourcis, volume et marquage vu lors du passage au suivant. Résultats : `.artifacts/player-v048-check/result.txt`.
- Les tests Windows n’enregistrent pas de raccourci Démarrer et publient une identité de validation distincte. Aucun épisode personnel lu ou modifié par ces essais.
- Limite du contrôle automatisé : la fiche Windows et les événements sont vérifiés via l’API système ; l’apparence de l’aperçu de la barre des tâches, les menus Explorer, la touche d’un clavier physique et les périphériques Bluetooth ne sont pas couverts par ce contrôle. Les couleurs des glyphes suivent le thème système, mais le changement de thème n’a pas été provoqué pendant les essais.

## Correctif 0.4.7

- Compilation Release sans erreur ni avertissement ; **28 tests** réussis, dont lecture libmpv + HTTP authentifié sur le serveur fictif. Nouveaux tests : fin de générique avant 90 %, persistance après réouverture, reprise du marquage vu sans renvoyer l’arrêt déjà accepté.
- **48 contrôles du lecteur** réussis avec une vidéo synthétique muette : ajout des vrais chemins de commande « épisode suivant » en milieu d’épisode, raccourci du générique, échéance du compte à rebours et fin naturelle avec ancienne position native. Les premiers conservent la reprise, les trois derniers marquent vu et effacent la reprise.
- **9 contrôles de mouvement** et rendus à 1024, 1440 et 1920 unités WPF réussis. Le mouvement réel du pointeur lève immédiatement la garde de défilement. Survol des cartes de reprise et des affiches, puis molette depuis une carte, vérifiés à la souris sur un profil séparé.
- Nouveau logo et 36 icônes vectorielles Mira : planche de contrôle dans `.artifacts/visual-v047/mira-icon-family.png`, PNG et ICO multi-tailles issus du même dessin. Centrage des boutons latéraux, marge droite des affiches et filtre de genre compact corrigés.
- Voile de démarrage déclaré dès le XAML, levé après préparation des premières images avec attente bornée ; les cascades de cartes sont supprimées à l’ouverture. La libération du voile est vérifiée par le contrôle visuel. La durée perçue du premier lancement sans cache reste dépendante du serveur.
- Aucun épisode personnel marqué vu pendant les essais. Les anciennes progressions ne sont pas converties en bloc : une progression seule ne permet pas de déterminer si l’épisode a réellement été terminé.

## Correctif 0.4.6

- Animations Windows d’ouverture, de fermeture et de réduction : la fenêtre garde le style de légende natif (`WS_CAPTION`), recouvert entièrement par le cadre personnalisé. Le contrôle du lecteur vérifie ce style, l’absence de barre de titre native (zone client égale à la fenêtre), puis son retrait en plein écran et son retour à la sortie : 43 vérifications réussies. Contrôle visuel et 8 contrôles de mouvement réussis.
- L’animation elle-même est jouée par Windows et n’a pas été observée automatiquement ; elle dépend du réglage « Effets d’animation », actif sur ce PC.

## Correctif 0.4.5

- Cause du survol « en deux temps » des boutons du lecteur mesurée avec la sonde `--perf-probe` du contrôle du lecteur (fenêtre 1480 × 930 unités WPF, DPI 200 %, écran 240 Hz). Chaque mouvement de souris recréait la découpe de la couche de commandes et redéplaçait sa fenêtre transparente : pendant un survol, l’animation tombait de 231 à **107 images/s** (27 % d’un cœur). Après correction : **231 images/s**, 9 % d’un cœur. La fenêtre transparente elle-même n’est pas limitante (231 images/s, 240 en mini-lecteur) ; une lecture de propriété mpv coûte 0,01 ms.
- Autres causes d’à-coups retirées : icône lecture / pause changée au clic (et non au relevé suivant), une seule courbe pour le fond et l’échelle du survol, écriture SQLite de la progression hors du fil de l’interface, calcul de la couleur du bandeau en arrière-plan, redessin de la barre de progression par pas de 0,4 pixel.
- Compilation Release sans erreur ni avertissement. **26 tests** réussis, dont l’intégration libmpv + HTTP. Nouveaux : titre arrêté au-delà de 90 % compté comme vu, rapport refusé (404) qui ne bloque pas la file, rapport d’arrêt envoyé avant la fermeture.
- Contrôle du lecteur : 40 vérifications réussies, dont l’indicateur de chargement et le curseur occupé levés à la première image, le compte à rebours « Épisode suivant dans 10 s » pendant le générique et son annulation.
- Contrôle visuel et 8 contrôles de mouvement réussis ; état vide de recherche, fiche avec bouton « Marquer comme vu » vérifiés sur les rendus.
- Non vérifié automatiquement : le menu contextuel ouvert à la souris, les raccourcis Alt + ← et bouton « précédent » de la souris, l’enregistrement automatique des réglages, le retour sur la fiche après une vraie lecture Jellyfin, l’épisode proposé par `Shows/NextUp` sur la bibliothèque personnelle et le marquage vu / non vu côté serveur (aucune mutation n’a été faite sur la bibliothèque personnelle).

## Correctif 0.4.4

- Compilation Release sans erreur ni avertissement. **23 tests** réussis, dont l'intégration libmpv + HTTP authentifié avec le serveur fictif. Nouveaux tests : requête `/MediaSegments` typée et filtrage, classement des titres de chapitres (opening, ending, récap, aperçu, générique selon sa position, avant-générique exclu), priorité des segments serveur, passage actif et bascule vers l'épisode suivant.
- Contrôle du lecteur (`--player-check`) : 37 vérifications réussies sur la version compilée et sur `dist/Mira`, avec `.artifacts/lecture-chapitres.mkv` (18 s, chapitres « Opening », « Partie A », « Ending » ajoutés par ffmpeg). Nouveautés couvertes : icônes non rognées et centrées dans chaque bouton, barre qui avance entre deux relevés mpv, Espace qui met en pause même quand l'événement vise le bouton +10 s, flèche droite qui avance de 10 s depuis n'importe quel bouton, masquage des commandes et du curseur en pause, exclusion de la couche de commandes des captures, points de chapitres, bouton « Passer l'intro » et saut à la fin du passage, mini-lecteur arrondi dans son coin, aimantation au coin le plus proche, rangement derrière la languette avec la vidéo conservée, retour par la languette.
- Contrôle visuel (`--visual-check --demo`) et 8 contrôles de mouvement réussis ; le survol des affiches n'a plus de légende et peut revenir sur l'affiche sous le pointeur après le défilement. Rendus de la couche de commandes dans `.artifacts/player-v044-check-result`.
- Non vérifié automatiquement : le geste réel de glisser à la souris (le contrôle appelle la fin de glisser directement), les coins arrondis de la vidéo native et l'ombre tels que composés par Windows, le rendu réel d'une capture Win + Maj + S, et les segments Jellyfin sur la bibliothèque personnelle (aucune lecture ni mutation n'y a été faite).

## Correctif 0.4.3

Cadre arrondi et contour natifs vérifiés sur Windows 11 (DPI 200 %), dans la bibliothèque et avec une vidéo synthétique muette. Agrandissement, plein écran et retour en fenêtre testés à la souris et au clavier. Les marges invisibles de redimensionnement Windows sont compensées en mode agrandi pour conserver les boutons dans la zone visible. Les 23 contrôles WPF/libmpv passent avec le nouveau cadre.

## Correctif 0.4.2

- Compilation Release sans erreur ni avertissement ; 23 assertions WPF/libmpv réussies. Alignement vertical volume/curseur/bouton pause, marges inférieures, interruption du fondu et conservation de la surface native compris.
- Rendus de la bibliothèque réelle à 1024, 1440 et 1920 unités WPF réussis ; 8 contrôles de mouvement/navigation réussis. Résultats dans `.artifacts/player-v042-check-result` et `.artifacts/visual-v042`.

- Essais réels à la souris sur une vidéo synthétique muette, profil séparé : les commandes cachées reviennent sans Échap en fenêtre et en plein écran. `GetCursorInfo` confirme le curseur système caché pendant l'inactivité, puis visible après mouvement.
- Même surface de commandes observée avant/après passage du fenêtré au plein écran 3840 × 2160 (DPI 200 %), puis au mini-lecteur 380 × 214 unités WPF. La vidéo conserve son instance et sa position.
- Clic pause, panneau de réglages, fermeture du panneau par Échap, réduction par son bouton, défilement avec mini-lecteur et survol intégré vérifiés dans la fenêtre de test.
- Survol constaté sur une affiche puis supprimé dès la molette ; aucun panneau ne recouvre les autres affiches. La barre supérieure reste transparente pendant le défilement.
- Le journal opt-in `player-ui-probe.jsonl` consigne uniquement les états d'interface pour `--qa-window --demo --test-media`, dans le profil de test. Il n'est pas activé en utilisation normale.

## Correctif 0.4.1

Bandeau épuré : suppression des raccourcis redondants, de la mention au-dessus du titre et du bouton pause du carrousel. Les réglages couvrent toute la fenêtre, y compris sous le rail, avec animation du contenu indépendante du fond.

## Résultats

- Compilation Release : aucune erreur ni avertissement au dernier contrôle.
- Tests automatisés : **20 réussis**, dont un avec la bibliothèque mpv.net réellement installée (`libmpv-2.dll`).
- Version 0.4 inspectée sur la bibliothèque réelle : accueil, catalogue, fiches, aperçus au survol et réglages, à 1024, 1440 et 1920 pixels de largeur. Rendus réalisés par l’application hors du bureau avec un profil de validation séparé.
- Molette synthétique routée depuis une carte de « Continuer à regarder » : événement traité par la page, passage observé d’environ 67 à 108 pixels au cours de l’interpolation.
- Rotation du bandeau, segment de durée à 50 %, conservation des instances de cartes après actualisation, aperçu au bord inférieur, mode de mouvement réduit et changements rapides de page vérifiés dans WPF.
- Titre de fiche présenté avant toute attente réseau ; 1 ms observée dans le profil préchargé. Cette mesure concerne la présentation initiale, pas le chargement complet ni le débit d’affichage du PC.
- Menus, raccourci Ctrl+K et recherche « Samurai » vérifiés dans la fenêtre Windows en 0.2 (un résultat : Samurai Champloo).
- Facettes réelles : 21 genres et 16 années récupérés. Les menus incluent une option « Tous / Toutes » supplémentaire.
- Lecture d’une vidéo synthétique 1280 × 720 à 24 images/s dans la surface mpv intégrée : image affichée et progression active.
- Pause vérifiée, puis déplacement de 13,25 s à 3,25 s avec la commande −10 s.
- Bibliothèque chargée dans l’application depuis un serveur HTTP de test isolé.
- Flux HTTP exigeant l’authentification : chargement dans libmpv, reprise à 2 s, pause, déplacement à 8 s, détection de piste audio et arrêt.
- Réception des rapports `Sessions/Playing`, `Sessions/Playing/Progress`, `Sessions/Playing/Stopped` dans cet ordre, avec les positions attendues.
- La bibliothèque personnelle a été consultée en lecture seule pour la validation visuelle. Les essais de lecture et de synchronisation utilisent un serveur fictif séparé.
- Lecteur 0.4 : 18 assertions WPF/libmpv réussies sur un média synthétique : seek, mini-lecteur, navigation, agrandissement, dimensions physiques du plein écran et restauration, mute/unmute (y compris volume zéro), vitesse, sous-titres, placement des menus à 200 %, raccourcis natifs et continuité des modes entre vidéos.
- Vérification à la souris : plein écran sur le moniteur 3840 × 2160 à 200 %, retour en fenêtre, menu de vitesse et sélection 1,25×, panneau dans les limites du lecteur, réduction dans la bibliothèque et navigation en conservant la vidéo.
- Contrôles vidéo superposés observés en fenêtre et en mode mini ; disparition lors de la lecture inactive et réapparition à la pause. Média synthétique muet uniquement.
- Police Nunito Sans embarquée résolue par WPF ; rendus exportés au DPI réel, pas rééchantillonnés à 96 DPI.
- Aperçus : zoom réduit à 2,5 %, panneau compact, respect des limites inférieures. Palette blanche et accent calculé par illustration.

## Tests couverts

1. Validation et normalisation d’adresse avec préservation d’un sous-chemin de proxy.
2. Authentification JSON, en-tête de session et encodage de recherche.
3. Persistance, ordre et coalescence de la file de synchronisation.
4. Réponse ancienne concurrente avec une progression plus récente.
5. Reprise locale et isolation entre comptes.
6. Sauvegarde réactive malgré un serveur lent.
7. Reconnexion et livraison ordonnée après une coupure.
8. Stockage de session protégé par DPAPI.
9. Bornes de progression et numérotation des épisodes.
10. Intégration libmpv + HTTP authentifié + synchronisation.
11. Paramètres de filtres, encodage, pagination et séparation des caches filtrés.
12. Chargement récursif des genres et extraction des années.
13. Migration des anciens réglages et persistance des préférences d’apparence/reprise.
14. Interpolation équivalente avec des pas de temps simulant 30, 60 et 144 images/s.
15. Inversion du défilement et bornes après réduction de la taille du contenu.
16. Durée du carrousel, pause, reprise et remise à zéro après sélection.
17. Partage des requêtes de préchargement et invalidation des métadonnées.
18. Nouvelle tentative après une erreur temporaire de préchargement.
19. Extraction de teintes différentes et lisibles pour des images rouges et bleues.
20. Repli blanc pour les images neutres et transparentes.
21. Segments Jellyfin : types demandés, passages inconnus ou trop courts ignorés.
22. Titres de chapitres transformés en passages à sauter.
23. Fusion des passages, passage actif et « Épisode suivant » en fin de lecture.
24. Arrêt local au-delà de 90 % : titre vu, reprise à zéro.
25. Rapport refusé définitivement écarté sans bloquer la file.
26. Rapport d’arrêt livré pendant la fermeture du service de synchronisation.

## Reproduire

```powershell
dotnet restore tests/Mira.Tests/Mira.Tests.csproj --configfile NuGet.Config
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-restore
```

Le test d’intégration demande un fichier `.artifacts/lecture-test.mp4` de plus de 10 secondes, libmpv, et le serveur fictif :

```powershell
node tools/mock-jellyfin.mjs
# Dans un autre terminal :
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release -- --mpv-integration
```

Les données et journaux de test restent sous `.artifacts/`, ignoré par Git. `--prepare-ui` crée exclusivement une session fictive dans `.artifacts/mock-profile` pour les essais de l’interface.

Un contrôle de rendu intégré peut exporter les vues de l’application sans interagir avec le bureau : passer `--data <profil de test séparé> --visual-check <dossier de sortie>`. Le profil doit déjà contenir une connexion protégée valide, ou ajouter `--demo`. Les fichiers `result.txt`, `layout.txt` et `motion-checks.txt` consignent le résultat. Les contrôles du lecteur natif ne sont pas capturés par ce rendu WPF.

Le contrôle du lecteur se lance sur un profil isolé :

```powershell
src/Mira.Desktop/bin/Release/net8.0-windows10.0.19041.0/Mira.exe --data .artifacts/player-check/profile --demo --test-media .artifacts/lecture-test.mp4 --player-check .artifacts/player-check
```

Le résultat est écrit dans `result.txt`. Les changements de modes utilisent le lecteur natif et un rectangle physique hors du bureau ; les vérifications à la souris complètent ce contrôle pour les fenêtres superposées.

TorLink, installé sur le PC, est vérifié sans toucher à son état réel ni à Jellyfin. Le contrôle de bout en bout ouvre une fenêtre visible ; il n’envoie de vraies touches que si elle a le premier plan :

```powershell
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build -- --torlink-integration
./tools/torlink-check.ps1                                   # build Release
./tools/torlink-check.ps1 -Executable dist/Mira/Mira.exe    # version publiée
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build -- --jellyfin-live <profil de validation>
```

La dernière commande lit seulement les bibliothèques et les derniers ajouts du serveur, avec la session protégée d’un profil de validation. Avec une vidéo chapitrée, le contrôle vérifie aussi les passages à sauter :

```powershell
ffmpeg -i .artifacts/lecture-test.mp4 -i .artifacts/chapitres.txt -map_metadata 1 -map_chapters 1 -c copy .artifacts/lecture-chapitres.mkv
```

## À vérifier avec l’utilisateur

Reprise entre Mira et ses autres clients, sous-titres utilisés habituellement, HDR éventuel, écran(s) et périphérique audio. Le plein écran a été vérifié sur le moniteur actuel. Les parcours d’épisodes réels et les configurations multi-écrans restent à éprouver avec les contenus et périphériques habituels.
