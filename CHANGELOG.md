# Historique

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
