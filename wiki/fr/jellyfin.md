---
title: Préparer Jellyfin
short: Préparer Jellyfin
description: Laisser Mira installer Jellyfin sur un PC Windows, nommer tes vidéos pour que Jellyfin les reconnaisse, et préparer le serveur pour tes autres appareils.
platforms: Windows (PC de Jellyfin)
---

Jellyfin est le serveur : il garde tes films et tes séries, leurs images et la progression de chacun. Mira est le lecteur. Jellyfin tourne sur un ordinateur qui reste allumé ; Mira tourne sur chaque appareil où tu regardes.

Tu as déjà Jellyfin 10.9 ou plus récent ? Passe à [Préparer le serveur](#preparer-le-serveur).

## Laisser Mira installer Jellyfin

Sur le PC Windows qui gardera tes vidéos, à l’écran de connexion de Mira, choisis **Pas encore de serveur : Mira installe Jellyfin sur ce PC**, puis :

1. **Dossier des vidéos** : `Vidéos\Jellyfin` par défaut, ou un autre avec **Parcourir** (un disque de stockage, par exemple). Mira y crée **Films**, **Séries** et **Animes**.
2. **Nom d’utilisateur** et **mot de passe** : ils deviennent le compte administrateur de Jellyfin.
3. **Installer**. Mira télécharge l’installateur officiel de Jellyfin sur `repo.jellyfin.org`, vérifie son SHA-256 avec celui que publie Jellyfin, et le lance. Windows demande une autorisation, une fois : Jellyfin s’installe pour tout le PC, comme un service qui démarre avec Windows.

Mira remplit ensuite l’assistant de premier démarrage de Jellyfin : ton compte administrateur, trois bibliothèques (Films, Séries, Animes), accès depuis Internet désactivé. Puis il te connecte.

> [!TIP]
> Si l’installateur de Jellyfin affiche « Could not start the Jellyfin Server service », choisis **Ignorer**. Le service démarre en général juste après, et Mira continue dès que Jellyfin répond.

Le service de Jellyfin tourne sous le compte **Network Service** de Windows, qui ne lit pas tes dossiers personnels. Mira lui donne le droit de lire, sans modifier, Films, Séries et Animes, et rien d’autre du dossier choisi.

## Nommer tes vidéos

Jellyfin trouve le titre, l’affiche et le résumé de chaque vidéo d’après son dossier et son nom. Mets un dossier par film, un dossier par série avec un dossier par saison :

```text
Films\Le Voyage de Chihiro (2001)\Le Voyage de Chihiro (2001).mkv
Séries\Arcane\Season 01\Arcane - S01E03.mkv
Animes\Frieren\Season 01\Frieren - S01E01.mkv
```

L’année entre parenthèses évite de confondre deux films du même nom. Les fichiers de sous-titres à côté d’une vidéo, avec le même nom et une langue (`Le Voyage de Chihiro (2001).fr.srt`), sont trouvés aussi.

Les nouveaux fichiers apparaissent à la prochaine analyse de Jellyfin. **Actualiser**, dans la bibliothèque de Mira, la lance tout de suite (compte administrateur).

> [!NOTE]
> Copie ou déplace tes vidéos dans ces dossiers avec l’Explorateur : elles prennent les droits du dossier. Un fichier déplacé par un autre programme peut garder les droits de son ancien dossier et rester invisible pour Jellyfin ; copie-le plutôt avec l’Explorateur.

## Préparer le serveur

Sur le PC où tourne Jellyfin, connecté avec le compte administrateur de Jellyfin, ouvre le guide (**?** à gauche, ou <kbd>F1</kbd>). La carte **Mira sur ton iPhone** coche ce qui est prêt et numérote ce qui reste :

- **L’extension Mira sur Jellyfin** : elle sert ton lien de party, les invités sans compte, les sous-titres automatiques et quelques autres fonctions.
- **Le pare-feu de Windows** : il laisse tes appareils joindre Jellyfin, sur son port seulement, depuis le réseau de la maison et Tailscale, jamais sur un réseau public. Si Windows compte le Wi-Fi de ta box comme public, **Passer en privé** le change (seulement chez toi : le réseau d’un café doit rester public).
- **Hors de chez toi (facultatif)** : Tailscale sur ce PC.
- **Dès l’allumage** : Jellyfin démarre avec Windows, avant toute session, et repart après une erreur.

**Tout préparer** fait tout ce que Mira peut faire, avec une seule autorisation de Windows, puis donne l’adresse à taper sur tes autres appareils. **Ne plus ouvrir Jellyfin au réseau**, sous les étapes, retire la règle de pare-feu de Mira ; **Tout préparer** la remet.

### Sans Mira pour Windows

Avec un Mac (Mira 0.9.6 ou plus), connecté avec le compte administrateur de Jellyfin : **Réglages → Compte et serveur → Installer**. Mira vérifie d’abord la signature de l’extension, puis Jellyfin redémarre quelques secondes, seulement quand personne ne regarde.

Sinon, pour ajouter l’extension de Mira depuis la page web de Jellyfin :

1. **Tableau de bord → Extensions → Dépôts → +**, nom `Mira`, URL :
   ```text
   https://github.com/sasou-web/Mira/releases/latest/download/jellyfin-manifest.json
   ```
2. **Catalogue** → **Mira** → **Installer**, puis redémarre Jellyfin (**Tableau de bord → Redémarrer**).

Jellyfin met ensuite l’extension à jour à chaque version de Mira. Avec un PC Windows, préfère **Tout préparer**, et avec un Mac son bouton **Installer** : Mira vérifie la signature de l’extension avant de l’installer.

## Garder Jellyfin à l’abri

Dans Mira pour Windows, sur le PC de Jellyfin : **Réglages → Entretien → Jellyfin sur ce PC** montre sa version et sa dernière sauvegarde (Jellyfin 10.11 ou plus récent).

- **Sauvegarder maintenant** fait la sauvegarde intégrée de Jellyfin : comptes, progression de chacun, bibliothèques, images et réglages, pas tes vidéos. Mira peut copier chaque sauvegarde sur un autre disque (**Copier aussi dans…**) et y garde les trois plus récentes. Il te la rappelle après 30 jours sans sauvegarde.
- Quand un Jellyfin plus récent sort, Mira le dit une fois. **Mettre à jour Jellyfin** sauvegarde d’abord, télécharge l’installateur officiel, le vérifie, puis Windows demande une autorisation. C’est refusé pendant que quelqu’un regarde.

> [!WARNING]
> Une sauvegarde contient les jetons de connexion de tes appareils. Garde-la dans un dossier à toi, jamais dans un dossier partagé.

Pour la remettre : installe Jellyfin (même version ou plus récente), copie `jellyfin-backup-<date>.zip` dans `C:\ProgramData\Jellyfin\Server\data\backups`, ouvre `http://127.0.0.1:8096` → **Tableau de bord → Sauvegardes**, et restaure-la.
