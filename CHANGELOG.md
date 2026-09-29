# Historique

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
