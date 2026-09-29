# Installer Mira sur Windows

## Prérequis

- Windows 10 version 2004 ou plus récent, ou Windows 11, **x64**. Les coins natifs arrondis dépendent de Windows 11.
- Un serveur Jellyfin accessible, sur le même PC ou le réseau, et un compte sur ce serveur.
- Une bibliothèque **libmpv x64** pour lire les vidéos. `mpv.exe` seul ne suffit pas.

L’archive Windows inclut le runtime .NET : il n’est pas nécessaire d’installer le SDK ou .NET pour l’utiliser. L’interface est actuellement en français. La version 0.5.0 est une préversion de développement non signée.

Facultatif : [TorLink](#torlink), installé séparément, et le runtime Microsoft Edge WebView2 (présent sur Windows 11 et la plupart des Windows 10) pour afficher son terminal dans Mira.

## Installation

1. Télécharger `Mira-0.5.0-win-x64.zip` dans les [releases](https://github.com/sasou-web/Mira/releases).
2. Extraire **tout** le dossier `Mira` dans un emplacement où ton compte peut écrire, par exemple `Documents/Applications/Mira`. Ne pas lancer l’exécutable depuis l’archive et ne pas copier uniquement `Mira.exe`.
3. Ouvrir `Mira.exe`. Un raccourci Mira est ajouté au menu Démarrer au premier lancement du profil principal.
4. Entrer l’adresse de Jellyfin, le nom d’utilisateur et le mot de passe. Pour Jellyfin sur le même PC, l’adresse habituelle est `http://127.0.0.1:8096`.
5. Dans **Réglages → Lecture**, vérifier que le moteur mpv est détecté. Si besoin, déplier son emplacement et sélectionner `libmpv-2.dll` ou `mpv-2.dll` x64.

Le bouton **Essayer sans se connecter** ouvre un catalogue fictif pour explorer l’interface.

## Obtenir le moteur vidéo

Si mpv.net ou Jellyfin MPV Shim est déjà installé à un emplacement détecté par Mira, sa bibliothèque peut être réutilisée. Une version de [mpv.net](https://github.com/mpvnet-player/mpv.net/releases) contenant `libmpv-2.dll` est une possibilité. La [page d’installation officielle de mpv](https://mpv.io/installation/) référence également des distributions Windows : choisir une build **libmpv x64**, puis conserver ensemble la DLL et ses éventuelles dépendances.

Mira ne télécharge ni n’installe automatiquement le moteur. Les licences du moteur et de ses dépendances sont celles de leur distributeur.

## Utilisation

Le serveur Jellyfin reste lancé : il gère la bibliothèque, les affiches, les métadonnées et la progression. Mira fournit l’interface et la lecture. Un fichier local est lu directement si le serveur est local et le fichier accessible ; sinon Mira utilise le flux original du serveur. Cette version ne transcode pas.

| Action | Raccourci |
| --- | --- |
| Recherche | Ctrl + K / Ctrl + F |
| Lecture / pause | Espace / K |
| Reculer / avancer de 10 secondes | ← / → |
| Volume | ↑ / ↓ |
| Couper le son | M |
| Plein écran | F / double-clic sur la vidéo |
| Mini-lecteur / agrandir | I |
| Passage disponible : opening, ending, suivant | S |
| Retour / fermer un panneau / sortir du plein écran | Échap |

La zone de notification permet de masquer puis de retrouver Mira sans arrêter la lecture. Le bouton × de la fenêtre quitte l’application. Les commandes de la barre des tâches et la fiche multimédia Windows apparaissent pendant la lecture ; leur présentation dépend de Windows.

## TorLink

TorLink est un client de téléchargement en terminal, installé séparément. Mira ne le modifie pas : il le lance depuis son dossier, avec le Node.js qu’il contient ou celui du PC (22 ou plus récent), et affiche son interface dans la page **TorLink** du rail.

1. Installer TorLink et vérifier qu’il démarre depuis son raccourci.
2. Ouvrir **TorLink** dans le rail de Mira. S’il n’est pas retrouvé, choisir son dossier, celui qui contient `torlink.bat`.
3. Dans **Réglages → TorLink**, vérifier les dossiers Films, Séries et Animes. Laissés vides, ils sont lus dans Jellyfin : il faut un compte administrateur et un serveur sur ce PC.

Chaque téléchargement terminé rejoint la bibliothèque sous les noms attendus par Jellyfin, par exemple `Titre (Année)\Titre (Année).mkv` ou `Série\Season 01\Série - S01E02.mkv`. Sur le même disque, un lien physique évite toute copie et TorLink continue de partager le fichier ; sinon il est copié. Jellyfin est prévenu, puis le titre apparaît dans Mira.

Quitter Mira ferme aussi TorLink ; ses téléchargements reprennent à la prochaine ouverture. TorLink ne peut pas tourner en même temps dans Mira et dans une autre fenêtre. Avec Jellyfin dans Docker ou sur un autre PC, choisir les dossiers locaux que le serveur partage : les nouveaux titres apparaissent alors à sa prochaine analyse de la bibliothèque.

Mira ne vérifie pas ce qui est téléchargé. Les sources intégrées à TorLink indexent surtout des copies dont la diffusion n’est pas autorisée : ne télécharger que des contenus que tu as le droit d’obtenir.

## Mise à jour et données

Fermer Mira, sauvegarder son dossier `data`, puis remplacer les fichiers de l’application par ceux de la nouvelle archive, **en conservant `data`**. Ce dossier contient la session protégée, les préférences, la progression en attente, le cache et le journal des rangements TorLink. Ne jamais le publier ou le joindre à une issue. La session protégée n’est pas portable vers un autre compte Windows.

L’empreinte du ZIP est fournie dans le fichier `.sha256`. Pour vérifier le fichier téléchargé :

```powershell
Get-FileHash .\Mira-0.5.0-win-x64.zip -Algorithm SHA256
```

## Si quelque chose ne fonctionne pas

| Symptôme | Vérification |
| --- | --- |
| Serveur inaccessible | Jellyfin doit être lancé ; vérifier l’adresse, le port et l’accès réseau. Pour un serveur distant, utiliser HTTPS. |
| Bibliothèque visible mais lecture impossible | Choisir une DLL libmpv **64 bits**, avec ses dépendances, dans les réglages. |
| Progression qui tarde à remonter | Consulter **Réglages → Jellyfin & synchronisation** ; les envois restent en attente quand le serveur est indisponible. |
| Icône de notification absente | Regarder les icônes masquées près de l’horloge. |
| Mira est déjà ouvert | Relancer le raccourci doit restaurer la fenêtre existante. |
| TorLink introuvable | Indiquer son dossier, celui qui contient `torlink.bat`, dans **Réglages → TorLink**. |
| « TorLink est déjà ouvert » | Quitter l’autre fenêtre TorLink (q ou Ctrl + C), puis choisir **Réessayer**. |
| Terminal intégré indisponible | Installer le runtime Microsoft Edge WebView2. En attendant, **Ouvrir dans une fenêtre** lance TorLink à part ; ses téléchargements terminés sont quand même rangés, au plus tard à la prochaine ouverture de Mira. |
| Téléchargement non rangé | Lire son état dans **Vers Jellyfin**, sur la page TorLink. Choisir les dossiers manquants dans les réglages, puis **Réessayer**. |

Pour un problème reproductible, [ouvrir une issue](https://github.com/sasou-web/Mira/issues/new/choose) avec les versions de Mira, Windows, Jellyfin et du moteur. Ne joindre aucun identifiant ni média personnel.
