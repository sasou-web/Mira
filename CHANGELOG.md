# Historique

## 0.5.5 — 2026-10-01

Un guide pour bien démarrer, les nouveautés résumées après chaque mise à jour, et Jellyfin plus fiable.

- **Bienvenue** : au tout premier lancement, Mira explique en trois cartes ce que font Jellyfin et Mira, puis propose trois façons de commencer : installer Jellyfin sur ce PC, se connecter à son serveur, ou découvrir la démo.
- **Guide de démarrage** : bouton **?** de la barre de gauche, touche **F1**, **Réglages → Jellyfin**, ou **Où mettre mes vidéos ?** quand la bibliothèque est vide. Il montre :
  - les dossiers de chaque bibliothèque Jellyfin, avec un bouton **Ouvrir**, et des exemples de noms que Jellyfin reconnaît ;
  - le bouton **Ouvrir Jellyfin dans le navigateur**, vers son tableau de bord (bibliothèques, comptes, accès depuis Internet) ;
  - l’adresse à entrer dans l’application Jellyfin d’un téléphone ou d’une TV (celle du PC sur le réseau de la maison), avec **Copier** ;
  - l’essentiel de Mira.

  Il s’ouvre une fois après la première connexion, et juste après l’installation de Jellyfin par Mira.
- **Nouveautés** : après une mise à jour, un écran plein résume les points forts de la version, sans le détail. Les pages de release sur GitHub reprennent ce même résumé court ; le détail reste ici.
- Bibliothèque vide : au lieu de « Rien à afficher ici », Mira dit où ranger les vidéos et ouvre le guide.

Installation de Jellyfin depuis Mira : corrections après le premier essai sur un vrai PC.

- Un Jellyfin déjà installé sur le PC, même arrêté, n’était pas reconnu : son installateur écrit sa clé dans la partie 32 bits du registre, que Mira ne lisait pas. Mira pouvait donc relancer l’installateur par-dessus. Il le reconnaît maintenant, par cette clé ou par son service, et ne réinstalle jamais par-dessus un Jellyfin existant.
- L’installateur de Jellyfin peut afficher « Could not start the Jellyfin Server service » alors que son service démarre bien. Mira s’arrêtait sur ce message, même quand Jellyfin tournait juste après. Il vérifie maintenant lui-même que Jellyfin répond et continue. Quand Jellyfin ne démarre vraiment pas, Mira affiche la cause écrite dans son journal, par exemple un port déjà pris, au lieu d’un simple « ne répond pas ».
- **Écoute des changements avec les Jellyfin récents** : avec la 12.1 installée par Mira, et les autres versions récentes, Jellyfin refusait l’écoute en temps réel de Mira. Son journal affichait « Token is required » toutes les 30 secondes. Les ajouts et les « vu » faits ailleurs n’apparaissaient donc qu’à l’actualisation. Mira envoie maintenant sa session dans l’en-tête de la connexion, comme pour ses autres requêtes, au lieu de l’adresse.
- Un Jellyfin 12 en plein démarrage n’est plus pris pour absent. Sa page de démarrage annonce « assistant non terminé » même quand il l’est : Mira attend que le serveur lui-même réponde. Un autre programme sur le port 8096 est signalé avant toute installation.

## 0.5.4 — 2026-10-01

Jellyfin et le moteur vidéo s’installent depuis Mira, et des pannes rares qui pouvaient bloquer Mira sont maintenant contenues.

- **Jellyfin installé et configuré depuis Mira** : sous le formulaire de connexion, **Pas encore de serveur ? Installer Jellyfin sur ce PC**. Mira télécharge l’installateur officiel de Jellyfin 12.1 et vérifie son empreinte SHA-256, la même que celle du manifeste winget de Jellyfin. Il le lance (Windows demande une autorisation), crée `Films`, `Séries` et `Animes` dans `Vidéos\Jellyfin` ou dans un autre dossier choisi, remplit l’assistant de premier démarrage de Jellyfin et se connecte. L’assistant reçoit : interface et métadonnées en français, ton compte administrateur, trois bibliothèques, accès depuis Internet désactivé. TorLink retrouve ces trois bibliothèques sans réglage.
- Jellyfin tourne comme service Windows, sous le compte Network Service : Mira lui donne le droit de lire le dossier choisi. Un fichier que TorLink y range par lien physique ou par déplacement garde les droits de son dossier de téléchargement ; il reçoit maintenant ce même droit, sinon Jellyfin ne le verrait pas.
- **Moteur vidéo mpv installé par Mira** : une case « Télécharger le moteur vidéo mpv » dans l’installateur (cochée par défaut), un bouton **Installer le moteur mpv** dans **Réglages → Lecture**, et un bouton **Installer** proposé à la première lecture quand aucun moteur n’est trouvé. La lecture démarre une fois le moteur prêt.
- Mira télécharge une build précise des builds Windows de mpv (31 Mo, depuis SourceForge), vérifie son empreinte SHA-256, extrait `libmpv-2.dll`, vérifie aussi la sienne, et la range dans `data\mpv`, que les mises à jour ne touchent pas. Un fichier altéré n’est jamais installé. Un moteur déjà présent (mpv.net, Jellyfin MPV Shim, ou choisi dans les réglages) est gardé.

- Un disque plein, une base locale verrouillée ou abîmée pendant une lecture n’ouvre plus une fenêtre d’erreur toutes les 3 secondes : la lecture continue et la ligne de synchronisation indique « Stockage local indisponible ».
- Dans ce cas, Mira ne pouvait plus se fermer : la fenêtre disparaissait mais le processus restait ouvert, et les envois suivants de la session étaient bloqués. Corrigé.
- Un cache local illisible (coupure de courant, disque défaillant) empêchait d’ouvrir le compte à chaque démarrage. Il est maintenant mis de côté (`.bad-…`) et recréé depuis Jellyfin, avec un message ; seuls les envois de progression qui n’étaient pas encore partis sont perdus.
- L’écoute des changements de Jellyfin ne s’arrête plus pour la session sur un message inattendu, et un serveur qui ferme la connexion aussitôt ouverte n’est plus recontacté toutes les 2 secondes.
- Un fichier `device-id` vide n’envoie plus un identifiant d’appareil vide à Jellyfin ; une commande envoyée au lecteur juste après son arrêt ne peut plus fermer Mira.

## 0.5.3 — 2026-10-01

Connexion plus simple, fiches plus riches et caches qui ne grossissent plus sans fin.

- **Connexion** : l’adresse du serveur peut s’écrire sans `http://` (`192.168.1.20`, `nas:8096`, `jellyfin.maison.lan`) ou se coller depuis la barre d’adresse de la page web de Jellyfin (`…:8096/web/#/home.html`). Mira essaie HTTPS, puis le port 8096 de Jellyfin, puis HTTP, et garde la première adresse où un serveur Jellyfin répond, avant d’envoyer le mot de passe. Une adresse passée en HTTPS par le serveur est retenue telle quelle.
- Messages de connexion explicites : adresse qui n’est pas un serveur Jellyfin (Emby compris), certificat HTTPS non reconnu, serveur muet, et version trop ancienne. Mira utilise les routes de **Jellyfin 10.9** : un serveur plus ancien est refusé avec sa version au lieu d’échouer ensuite sur des erreurs 404.
- **Fiches** : distribution (« Avec », six premiers rôles) et réalisation dans la colonne des genres ; rangée **Titres similaires** sous la fiche, d’après Jellyfin, aussi large que la fenêtre. Clic pour ouvrir, clic droit pour lire, marquer vu ou ajouter aux favoris.
- Nouveau tri **Dernière lecture** dans le catalogue.
- **Caches bornés** : les images sur disque (tous comptes confondus) sont ramenées à 768 Mo au-delà de 1 Go, les moins récemment vues d’abord ; les images décodées gardées en mémoire sont limitées à 512 Mo ; les pages de catalogue et positions locales déjà envoyées de plus de 30 jours sont oubliées. L’accueil, la reprise, l’historique et les envois en attente restent.
- `errors.log` est écrit dans le profil utilisé (`--data` compris), avec la méthode en cause et sans message d’erreur, et plafonné à 256 Ko.

## 0.5.2 — 2026-09-29

Mises à jour automatiques et nouvelle barre de lecture.

- **Mises à jour automatiques** : Mira cherche une nouvelle version sur GitHub à l’ouverture, puis toutes les 6 heures, télécharge le fichier qui correspond à sa copie (installateur, exécutable portable ou archive) et l’installe à la fermeture. **Redémarrer** l’installe tout de suite et rouvre Mira. Le dossier `data` n’est jamais modifié.
- Chaque version passe une vérification avant d’être installée : manifeste `mira-update.json` signé (ECDSA P-256) par une clé intégrée à Mira, puis taille et empreinte SHA-256 du fichier. Une release modifiée sur GitHub ou en chemin, non signée ou plus ancienne est refusée.
- Un téléchargement interrompu reprend où il s’était arrêté. Mira attend la fin d’une installation en cours avant de démarrer, annonce au démarrage suivant si la mise à jour a réussi, et ne relance plus à chaque fermeture une version qui a échoué deux fois.
- **Réglages → Mises à jour** : version, état, recherche manuelle, redémarrage pour installer, lien vers les nouveautés, et interrupteur des mises à jour automatiques.
- **Barre de lecture** redessinée : titre et épisode (« Épisode 3 / 12 · nom ») à gauche, chapitre en cours et temps à droite, au-dessus d’une barre découpée par chapitres ; lecture, épisode suivant et volume à gauche ; options, audio et sous-titres, mini-lecteur et plein écran à droite.
- Le curseur de volume sort du haut-parleur au survol, et un instant après ↑ / ↓. L’icône indique son niveau.
- Menus du lecteur ouverts au-dessus de leur bouton : pistes audio et sous-titres en deux colonnes, cochées, avec langue, codec et canaux ; vitesses, décalage des sous-titres et informations de lecture sous **⋮**.
- Les boutons ±10 secondes quittent la barre, comme sur la référence choisie ; ← et → restent les raccourcis.
- Outils de release : `tools/Mira.Release` (clé de signature protégée par Windows, hors du dépôt), manifeste signé par `tools/package.ps1`, essai de bout en bout `tools/update-check.ps1`.

### À savoir

Les copies en 0.5.1 ou plus ancienne n’ont pas ces mises à jour : il faut installer la 0.5.2 une fois à la main. Une release publiée sans `mira-update.json` signé n’est pas proposée aux copies installées.

## 0.5.1 — 2026-09-29

Installation simplifiée.

- **Installateur Windows** `Mira-0.5.1-win-x64-setup.exe` : installation pour l’utilisateur courant, sans droits administrateur, dans `%LOCALAPPDATA%\Programs\Mira`, avec raccourci Démarrer, raccourci Bureau facultatif et désinstallation depuis les paramètres de Windows. Une mise à jour s’installe par-dessus et garde les données ; la désinstallation demande avant de les supprimer.
- **Exécutable portable** `Mira-0.5.1-win-x64-portable.exe` : Mira en un seul fichier, qui crée son dossier `data` à côté de lui.
- La page du terminal TorLink est servie depuis les ressources de Mira ; les liaisons clavier de mpv et l’icône Windows sont intégrées à l’exécutable portable.
- Contrôle TorLink : avec un autre TorLink ouvert sur le PC, il vérifie la page puis s’arrête, au lieu d’échouer.

## 0.5.0 — 2026-09-29

TorLink dans Mira, jusqu’à la bibliothèque Jellyfin.

- Page **TorLink** dans le rail : l’interface de TorLink, installé séparément et non modifié, s’affiche dans Mira (pseudo-console Windows, xterm.js dans WebView2). Une seule instance ; fermer Mira ferme TorLink proprement.
- Rangement automatique des téléchargements terminés dans les dossiers des bibliothèques Jellyfin : `Titre (Année)\Titre (Année).mkv`, `Série\Season 01\Série - S01E02.mkv`, sous-titres, bonus et packs de saison. Les dossiers existants sont réutilisés ; aucun fichier existant n’est remplacé, et un fichier que Mira n’a pas placé n’est jamais déplacé.
- Lien physique sur le même disque : TorLink continue de partager sans doublon. Sinon copie, ou déplacement au choix.
- Films, séries et animes distingués par la source TorLink, les dossiers existants et le nom de sortie ; « Classer comme… » corrige un classement. Jeux et téléchargements sans vidéo ignorés.
- Jellyfin est prévenu des nouveaux dossiers ; « Vers Jellyfin » suit chaque titre jusqu’à son apparition et propose « Voir », quand le serveur voit les fichiers au même chemin que Mira.
- **Réglages → TorLink** : rangement automatique, partage, emplacement de TorLink et dossiers de bibliothèque manuels.

### Limites connues

TorLink et ses sources restent des composants externes : Mira ne choisit ni ne vérifie ce qui est téléchargé. Comme tout client BitTorrent, TorLink échange avec des pairs, écoute un port et peut demander au routeur de l’ouvrir (UPnP, NAT-PMP) tant qu’il tourne. Les dossiers sont lus depuis Jellyfin avec un compte administrateur, s’ils existent sur ce PC ; sinon, ils se choisissent dans les réglages. Avec Jellyfin dans Docker ou sur un autre PC, les dossiers choisis doivent être ceux que le serveur partage, et l’ajout peut attendre sa prochaine analyse de la bibliothèque. Seuls les téléchargements terminés après l’activation sont rangés automatiquement ; les cinq plus récents d’avant restent proposés avec « Importer ».

## 0.4.9 — 2026-09-29

Première publication publique du projet et de la distribution Windows x64.

- Bibliothèque Jellyfin : accueil, films, séries, recherche, filtres, favoris, fiches et saisons.
- Lecture intégrée libmpv, plein écran, mini-lecteur déplaçable, pistes, sous-titres, chapitres et passage au suivant.
- Synchronisation persistante, progression locale, reprise après déconnexion et statut vu après fin de lecture.
- Identité Mira dans Windows, zone de notification, commandes dans l’aperçu de la barre des tâches et session multimédia système.
- Survol des cartes unifié et suivi pendant le défilement ; indicateurs de carrousel arrondis ; connexion redessinée.
- « Continuer à regarder » trié par dernière lecture, avec reprise locale après redémarrage.
- Galerie reproductible à partir des données fictives intégrées, documentation, notices tierces et script de création d’une archive propre.

### Limites connues

Windows x64 uniquement, interface française, libmpv à installer séparément. Distribution non signée et mise à jour manuelle. Transcodage, choix entre plusieurs versions d’un film, édition des collections et tests approfondis HDR/multicanal encore à réaliser. Les rendus de démonstration ne représentent pas des médias fournis avec l’application.
