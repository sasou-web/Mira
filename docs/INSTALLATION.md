# Installer Mira sur Windows

## Prérequis

- Windows 10 version 2004 ou plus récent, ou Windows 11, **x64**. Les coins natifs arrondis dépendent de Windows 11.
- Un serveur Jellyfin accessible, sur le même PC ou le réseau, et un compte sur ce serveur. Sans serveur, Mira peut [installer Jellyfin sur ce PC](#installer-jellyfin-depuis-mira).
- Le moteur vidéo **libmpv x64**. Mira l’installe à la demande (31 Mo) ; un moteur déjà présent est réutilisé. `mpv.exe` seul ne suffit pas.

L’installateur, l’exécutable portable et l’archive incluent le runtime .NET : il n’est pas nécessaire d’installer le SDK ou .NET. L’interface est actuellement en français. Mira est une préversion de développement non signée.

Facultatif : [TorLink](#torlink), que Mira installe quand tu l’actives, et le runtime Microsoft Edge WebView2 (présent sur Windows 11 et la plupart des Windows 10) pour afficher son terminal dans Mira.

## Installation

La [dernière release](https://github.com/sasou-web/Mira/releases) propose trois fichiers, où `<version>` est son numéro ; un seul suffit.

- **`Mira-<version>-win-x64-setup.exe`, recommandé.** L’installateur place Mira dans `%LOCALAPPDATA%\Programs\Mira`, pour ton compte uniquement et sans droits administrateur. Il ajoute le raccourci Démarrer, un raccourci Bureau si tu le coches, et une entrée de désinstallation dans **Paramètres → Applications**.
- **`Mira-<version>-win-x64-portable.exe`.** Mira en un seul fichier, sans installation. Range-le dans un dossier à lui où ton compte peut écrire, par exemple `Documents\Mira` : il y crée son dossier `data`. Au premier lancement, .NET extrait quelques bibliothèques natives dans `%TEMP%\.net`.
- **`Mira-<version>-win-x64.zip`.** Le dossier complet de l’application. Extraire **tout** le dossier `Mira` dans un emplacement accessible en écriture, sans lancer l’exécutable depuis l’archive ni copier uniquement `Mira.exe`.

Les fichiers ne sont pas encore signés : Windows SmartScreen peut afficher « Windows a protégé votre ordinateur ». Vérifier l’empreinte du fichier (plus bas), puis choisir **Informations complémentaires → Exécuter quand même**.

1. Ouvrir Mira. Un raccourci Mira est ajouté au menu Démarrer au premier lancement du profil principal, ou dès l’installation.
2. Entrer l’adresse de Jellyfin, le nom d’utilisateur et le mot de passe. Pour Jellyfin sur le même PC, l’adresse habituelle est `http://127.0.0.1:8096`. Sur le réseau, l’adresse IP ou le nom du PC suffit (`192.168.1.20`, `nas:8096`), comme l’adresse copiée depuis la page web de Jellyfin. Jellyfin 10.9 ou plus récent est nécessaire.
3. Le moteur vidéo : avec l’installateur, laisser cochée la case **Télécharger le moteur vidéo mpv**. Sinon, **Réglages → Lecture → Installer le moteur mpv**, proposé aussi à la première lecture.

Le bouton **Essayer sans se connecter** ouvre un catalogue fictif pour explorer l’interface. Sans serveur Jellyfin, **Pas encore de serveur ? Installer Jellyfin sur ce PC** l’installe et le prépare (section suivante).

## Installer Jellyfin depuis Mira

Sous le formulaire de connexion, choisir **Pas encore de serveur ? Installer Jellyfin sur ce PC**, puis :

1. **Dossier de tes vidéos** : `Vidéos\Jellyfin` par défaut, ou un autre avec **Parcourir** (un disque de stockage, par exemple). Mira y crée `Films`, `Séries` et `Animes`.
2. **Nom d’utilisateur** et **mot de passe** : ils deviennent le compte administrateur de Jellyfin.
3. **Installer et configurer**. Mira télécharge l’installateur officiel de Jellyfin 12.1 depuis `repo.jellyfin.org` et vérifie son empreinte SHA-256, celle du manifeste winget de Jellyfin. Un fichier différent n’est jamais lancé. Windows demande ensuite une autorisation : Jellyfin s’installe pour tout le PC, comme un service qui démarre avec Windows.

Mira remplit alors l’assistant de premier démarrage de Jellyfin :

- interface et métadonnées en français ;
- ton compte administrateur ;
- trois bibliothèques : **Films** (type films), **Séries** et **Animes** (type séries) ;
- accès depuis Internet désactivé ; les appareils du réseau local se connectent.

Puis il se connecte. Il reste à ranger les vidéos dans ces dossiers, sous les noms attendus par Jellyfin (`Titre (Année)\Titre (Année).mkv`, `Série\Season 01\Série - S01E02.mkv`), ou à laisser TorLink le faire : il retrouve les trois bibliothèques sans réglage.

Le service de Jellyfin tourne sous le compte Windows **Network Service**, qui ne lit pas les dossiers personnels. Mira lui donne le droit de lire, sans modifier, le dossier choisi et tout ce qui y est ajouté. Une vidéo copiée ou déplacée avec l’Explorateur prend ce droit. Un fichier rangé par TorLink, par lien physique ou par déplacement, le reçoit aussi.

Si Jellyfin est déjà installé et configuré sur ce PC, Mira le signale et propose de se connecter avec son compte. Une installation interrompue se relance avec le même compte : les étapes déjà faites sont gardées. Jellyfin se désinstalle depuis **Paramètres → Applications → Jellyfin Server** ; ses données sont dans `%ProgramData%\Jellyfin\Server`. Ses autres réglages, dont l’accès depuis Internet, se font sur sa page web, `http://127.0.0.1:8096`.

## Obtenir le moteur vidéo

**Automatiquement.** La case **Télécharger le moteur vidéo mpv** de l’installateur (cochée par défaut), le bouton **Installer le moteur mpv** de **Réglages → Lecture** ou le bouton **Installer** proposé à la première lecture font la même chose : Mira télécharge une build précise des [builds Windows de mpv](https://mpv.io/installation/) (shinchiro, publiée sur SourceForge, 31 Mo), vérifie son empreinte SHA-256, extrait `libmpv-2.dll` et vérifie aussi la sienne, puis la range dans `data\mpv`. Les mises à jour de Mira n’y touchent pas. Un fichier différent de celui attendu n’est jamais installé. Avec l’installateur, un échec du téléchargement (pas de connexion) n’empêche pas l’installation : Mira le propose de nouveau à la première lecture.

**Avec un moteur déjà présent.** Si mpv.net ou Jellyfin MPV Shim est installé à un emplacement connu de Mira, sa bibliothèque est réutilisée et rien n’est téléchargé. Une autre `libmpv-2.dll` ou `mpv-2.dll` x64 peut être choisie dans **Réglages → Lecture → Emplacement du moteur** ; elle passe avant celle de Mira.

Le moteur n’est pas inclus dans les fichiers de Mira : il est téléchargé depuis son distributeur, et ses licences (GPL pour mpv et FFmpeg) restent les siennes. `data\mpv\SOURCE.txt` indique sa provenance et ses empreintes.

## Utilisation

Le serveur Jellyfin reste lancé : il gère la bibliothèque, les affiches, les métadonnées et la progression. Mira fournit l’interface et la lecture. Un fichier local est lu directement si le serveur est local et le fichier accessible ; sinon Mira utilise le flux original du serveur. Cette version ne transcode pas.

| Action | Raccourci |
| --- | --- |
| Recherche | Ctrl + K / Ctrl + F |
| Lecture / pause | Espace / K |
| Reculer / avancer de 10 secondes | ← / → |
| Volume (jusqu’à 200 %) | ↑ / ↓, molette sur le haut-parleur |
| Couper le son | M |
| Plein écran | F / double-clic sur la vidéo |
| Mini-lecteur / agrandir | I |
| Passage disponible : opening, ending, suivant | S |
| Retour / fermer un panneau / sortir du plein écran | Échap |

La zone de notification permet de masquer puis de retrouver Mira sans arrêter la lecture. Le bouton × de la fenêtre quitte l’application. Les commandes de la barre des tâches et la fiche multimédia Windows apparaissent pendant la lecture ; leur présentation dépend de Windows.

## TorLink

TorLink est un client de téléchargement en terminal ([projet indépendant](https://github.com/baairon/torlink), licence MIT), publié sur npm (`npx torlnk`). Mira affiche son interface dans la page **Téléchargements** du rail.

1. Ouvrir **Téléchargements** (flèche du rail), puis activer **TorLink** dans la liste des téléchargeurs.
2. Si TorLink est déjà sur ce PC (copie de `npx torlnk`, installation npm globale, raccourci), Mira l’utilise tel quel. Sinon, il l’installe pour ce compte Windows, sans droits administrateur et sans modifier le PATH :
   - **Node.js 24.21.0** : le zip officiel de nodejs.org (38 Mo), vérifié par sa taille et son SHA-256, et rangé dans `data\node` ;
   - **TorLink 1.9.0**, installé par ce npm dans `data\torlink`.
   Compter une minute environ.
3. Dans **Réglages → TorLink**, vérifier les dossiers Films, Séries et Animes. Laissés vides, ils sont lus dans Jellyfin : il faut un compte administrateur et un serveur sur ce PC.

Désactiver TorLink le ferme ; il reste installé. Le TorLink installé par Mira est mis à jour avec Mira. Il tourne sans son module WebRTC facultatif, qui demande des outils de compilation : les pairs TCP, uTP et DHT suffisent.

Chaque téléchargement terminé est **déplacé** dans la bibliothèque, sous les noms attendus par Jellyfin, par exemple `Titre (Année)\Titre (Année).mkv` ou `Série\Season 01\Série - S01E02.mkv`. Il n’existe jamais en double :
- sur le même disque, le déplacement est instantané : le fichier change seulement de dossier ;
- sur un autre disque, chaque fichier est copié puis aussitôt supprimé du dossier de TorLink. Pour éviter ce cas, Mira fait télécharger TorLink sur le disque de la bibliothèque, dans « Téléchargements TorLink », quand TorLink n’a pas encore de dossier. Sinon, choisir ce dossier dans TorLink avec la touche **o**.

TorLink arrête de partager un téléchargement déplacé. Pour qu’il continue, activer **Réglages → TorLink → Continuer à partager** : sur le même disque, un lien physique met aussi le fichier dans la bibliothèque, sans prendre plus de place. Il apparaît alors dans les deux dossiers. Jellyfin est prévenu, puis le titre apparaît dans Mira.

Quitter Mira ferme aussi TorLink ; ses téléchargements reprennent à la prochaine ouverture. TorLink ne peut pas tourner en même temps dans Mira et dans une autre fenêtre. Avec Jellyfin dans Docker ou sur un autre PC, choisir les dossiers locaux que le serveur partage : les nouveaux titres apparaissent alors à sa prochaine analyse de la bibliothèque.

Mira ne vérifie pas ce qui est téléchargé. Les sources intégrées à TorLink indexent surtout des copies dont la diffusion n’est pas autorisée : ne télécharger que des contenus que tu as le droit d’obtenir.

## Mise à jour et données

Le dossier `data`, à côté de `Mira.exe`, contient la session protégée, les préférences, la progression en attente, le cache et le journal des rangements TorLink. Ne jamais le publier ou le joindre à une issue. La session protégée n’est pas portable vers un autre compte Windows.

**Mises à jour automatiques, à partir de la 0.5.2.** Quelques secondes après son ouverture, puis toutes les 6 heures, Mira cherche une nouvelle version sur GitHub. Il télécharge en arrière-plan le fichier de sa forme d’installation (installateur, exécutable portable ou archive), vérifie qu’il est signé par Mira, avec la taille et l’empreinte SHA-256 annoncées, et l’installe quand tu fermes Mira. **Redémarrer**, dans le message qui s’affiche ou dans **Réglages → Mises à jour**, l’installe tout de suite et rouvre Mira. Le dossier `data` reste en place ; les préversions sont proposées, comme toutes les versions publiées jusqu’ici. L’interrupteur de **Réglages → Mises à jour** arrête la recherche automatique ; **Rechercher** reste disponible. Le détail de chaque vérification est noté dans `data\updates\update.log`.

Une copie en 0.5.1 ou plus ancienne ne se met pas à jour seule : installer une fois à la main la 0.5.2 ou une version plus récente suffit. Pour mettre à jour à la main :

- **Installateur** : lancer celui de la nouvelle version ; il ferme Mira s’il est ouvert, remplace l’application et garde `data`. La désinstallation demande avant de supprimer ces données (non par défaut).
- **Exécutable portable** : fermer Mira et remplacer le fichier `.exe`, dans le même dossier que son `data`.
- **Archive** : fermer Mira, sauvegarder `data`, puis remplacer les fichiers de l’application par ceux de la nouvelle archive, **en conservant `data`**.

Chaque fichier de la release est accompagné de son empreinte SHA-256 (fichier `.sha256`). Pour vérifier un fichier téléchargé :

```powershell
Get-FileHash .\Mira-<version>-win-x64-setup.exe -Algorithm SHA256
```

Pour passer d’une copie portable à l’installateur en gardant sa session : installer, fermer toutes les fenêtres Mira, puis copier le dossier `data` de l’ancienne copie dans `%LOCALAPPDATA%\Programs\Mira`, en remplaçant celui qui s’y trouve.

## Si quelque chose ne fonctionne pas

| Symptôme | Vérification |
| --- | --- |
| Jellyfin injoignable hors de chez soi | L’adresse du guide (192.168…) ne marche que sur le réseau de la maison. Installer Tailscale sur ce PC et sur l’appareil, avec le même compte, puis **Guide** (**?** à gauche) **→ Mira sur ton téléphone → Tout préparer**, et scanner le QR code (ou entrer l’adresse Tailscale dans l’application Jellyfin). Ouvrir la box (redirection de port) exposerait Jellyfin à tout Internet : c’est déconseillé. |
| Serveur inaccessible | Jellyfin doit être lancé ; vérifier l’adresse, le port et l’accès réseau. Pour un serveur distant, utiliser HTTPS. |
| « Ce n’est pas un serveur Jellyfin » | L’adresse répond, mais pas Jellyfin (routeur, NAS, Emby…) : vérifier le port, 8096 par défaut. |
| « Mira a besoin de Jellyfin 10.9 » | Mettre Jellyfin à jour ; Mira utilise des routes introduites par cette version. |
| Fenêtre « Could not start the Jellyfin Server service » de l’installateur de Jellyfin | Choisir **Ignorer**. Ce message peut apparaître alors que le service démarre bien : il a été vu sur un vrai PC, où Jellyfin tournait juste après. Mira vérifie lui-même que Jellyfin répond, puis continue. S’il ne répond pas, Mira affiche la cause écrite dans le journal de Jellyfin (`%ProgramData%\Jellyfin\Server\log`), par exemple le port 8096 déjà pris. |
| « Jellyfin s’est installé mais ne démarre pas » ou « … déjà installé sur ce PC mais ne répond pas » | Lire la cause indiquée, la corriger, puis démarrer **Jellyfin Server** (menu Démarrer, ou **Services** de Windows) ou redémarrer le PC. **Installer et configurer** reprend ensuite où il s’était arrêté, sans réinstaller. |
| « Un autre programme répond déjà sur le port 8096 » | Ce n’est pas Jellyfin : fermer ce programme, ou installer Jellyfin sur un autre PC. |
| « Ce Jellyfin a déjà un compte administrateur » | Sa configuration a été commencée avec un autre compte : la terminer sur `http://127.0.0.1:8096`, puis se connecter. |
| Vidéo rangée mais absente de Jellyfin | Vérifier qu’elle est dans `Films`, `Séries` ou `Animes`. Un fichier déplacé par un autre programme peut garder les droits de son ancien dossier : le copier avec l’Explorateur à la place. |
| « Certificat HTTPS non reconnu » | Le certificat du serveur n’est pas approuvé par Windows : utiliser son adresse `http://` sur le réseau local, ou installer un certificat valide. |
| Bibliothèque visible mais lecture impossible | **Réglages → Lecture** : installer le moteur mpv, ou choisir une DLL libmpv **64 bits** avec ses dépendances. |
| « Le moteur vidéo n’a pas pu être téléchargé » | Vérifier la connexion Internet puis réessayer depuis **Réglages → Lecture**. Le téléchargement passe par `downloads.sourceforge.net` et ses miroirs. |
| Progression qui tarde à remonter | Consulter **Réglages → Jellyfin & synchronisation** ; les envois restent en attente quand le serveur est indisponible. |
| Icône de notification absente | Regarder les icônes masquées près de l’horloge. |
| Mira est déjà ouvert | Relancer le raccourci doit restaurer la fenêtre existante. |
| TorLink introuvable | Dans la page **Téléchargements**, choisir **Réinstaller**. |
| « TorLink n’a pas pu s’installer » | Vérifier la connexion à Internet (nodejs.org et registry.npmjs.org), puis réactiver TorLink. |
| « TorLink est déjà ouvert » | Quitter l’autre fenêtre TorLink (q ou Ctrl + C), puis choisir **Réessayer**. |
| Terminal intégré indisponible | Installer le runtime Microsoft Edge WebView2. En attendant, **Ouvrir dans une fenêtre** lance TorLink à part ; ses téléchargements terminés sont quand même rangés, au plus tard à la prochaine ouverture de Mira. |
| Téléchargement non rangé | Lire son état dans **Vers Jellyfin**, sur la page Téléchargements. Choisir les dossiers manquants dans les réglages, puis **Réessayer**. |
| Mise à jour qui ne s’installe pas | **Réglages → Mises à jour** donne la dernière erreur, `data\updates\update.log` le détail. Le dossier de Mira doit rester accessible en écriture ; une copie lancée depuis un dossier de développement (`bin\Release`) ne se met jamais à jour. |

Pour un problème reproductible, [ouvrir une issue](https://github.com/sasou-web/Mira/issues/new/choose) avec les versions de Mira, Windows, Jellyfin et du moteur. Ne joindre aucun identifiant ni média personnel.
