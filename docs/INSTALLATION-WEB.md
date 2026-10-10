# Mira sur iPhone, iPad et Android

Mira web est la version de Mira pour téléphone et tablette. Ton serveur Jellyfin la sert lui-même : il n’y a rien à installer sur le téléphone, ni App Store, ni compte Apple, ni application à rafraîchir. Sur iPhone, une fois ajoutée à l’écran d’accueil, elle s’ouvre comme une app, en plein écran, avec son icône.

Tu y retrouves l’interface de Mira : l’accueil avec Continuer à regarder, les films et les séries avec leurs filtres, la recherche, les fiches avec la distribution et les longues saisons par tranches de 100. Sur iPhone et iPad, les vidéos se lisent dans le lecteur d’Apple, en plein écran. Ailleurs, le lecteur de Mira gère la reprise, l’intro à passer, l’épisode suivant, les pistes audio et sous-titres, AirPlay et l’image dans l’image. Ta progression est partagée avec Mira sur Windows et sur Mac.

## Avec Mira pour Windows : tout en un clic

Sur le PC où tourne Jellyfin :

1. Ouvre le **Guide** (bouton **?** à gauche, ou F1).
2. La carte **Mira sur ton téléphone** coche ce qui est prêt et numérote ce qui reste :
   - **Mira web sur Jellyfin** : l’extension qui sert Mira à ton téléphone ;
   - **le pare-feu de Windows** : il laisse passer ton téléphone, sur le port de Jellyfin seulement, depuis ton Wi-Fi et Tailscale ;
   - **hors de chez toi (facultatif)** : Tailscale, gratuit, sur ce PC et sur ton téléphone, avec le même compte ;
   - **dès l’allumage du PC** : Jellyfin démarre avec Windows, avant ta session, et repart après une erreur (avec son service Windows).
3. Clique sur **Tout préparer**. Windows demande une seule autorisation, puis Jellyfin redémarre quelques secondes.
4. La page du QR code s’ouvre : scanne-le avec l’appareil photo du téléphone, puis connecte-toi avec ton compte Jellyfin.

Seul Tailscale est à installer toi-même : **Installer Tailscale** ouvre sa page de téléchargement. Une fois Tailscale connecté sur ce PC, le guide se met à jour tout seul et **Tout préparer** fait le reste.

Quand Tailscale est prêt, le QR code donne l’adresse Tailscale du PC (`http://100.x.y.z:8096/Mira`) : elle marche chez toi comme ailleurs, tant que Tailscale est activé sur le téléphone. Sans Tailscale sur le téléphone, choisis **Chez toi seulement** sur la page du QR code : l’adresse de la maison (`http://192.168.x.y:8096/Mira`) ne marche que sur ton Wi-Fi.

Il faut être connecté avec le compte administrateur de Jellyfin, ce qui est le cas si Mira a installé Jellyfin.

### Sur iPhone et iPad : l’avoir comme une app

Dans Safari, touche **Partager**, puis **Sur l’écran d’accueil**, et laisse **Ouvrir comme app web** activé. Mira apparaît sur l’écran d’accueil et s’ouvre en plein écran. La connexion est à faire une fois de plus dans cette app, qui garde ses propres données.

### Sur Android

Dans Chrome, menu **⋮ → Ajouter à l’écran d’accueil** (ou **Installer l’application**).

### Garder la même adresse

Si le PC n’est pas encore prêt quand tu ouvres Mira, l’app s’ouvre quand même (dès qu’elle a été ouverte une fois), indique que le serveur ne répond pas encore et reprend toute seule dès qu’il répond.

Avec l’adresse de la maison, réserve aussi son adresse IP au PC dans l’interface de ta box (bail DHCP fixe, ou « IP fixe » selon la box). Sinon, après un redémarrage, la box peut lui en donner une autre, et l’app de l’écran d’accueil ne le trouve plus. L’adresse Tailscale, elle, ne change pas.

## Sans Mira pour Windows

**Ajouter Mira web**, depuis la page web de Jellyfin :

1. **☰ → Tableau de bord → Extensions → Dépôts**, puis **+** :
   - nom : `Mira` ;
   - URL : `https://github.com/sasou-web/Mira/releases/latest/download/jellyfin-manifest.json`.
2. **Catalogue** : choisis **Mira**, puis **Installer**.
3. Redémarre Jellyfin (**Tableau de bord → Redémarrer**).

Jellyfin met ensuite l’extension à jour tout seul à chaque nouvelle version de Mira.

**L’ouvrir** : sur le téléphone, connecté au même Wi-Fi que le serveur, ouvre l’adresse du serveur suivie de `/Mira`, par exemple `http://192.168.1.20:8096/Mira`. Depuis Mira web déjà ouvert ailleurs, **Réglages → Ouvrir sur un autre appareil** affiche son QR code.

**Hors de chez toi** : installe Tailscale sur le serveur et sur le téléphone, avec le même compte. Dans Jellyfin, **Tableau de bord → Réseau → LAN networks**, ajoute `100.64.0.0/10` (avec, si la liste était vide, `127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12` et `192.168.0.0/16`), puis ouvre `http://100.x.y.z:8096/Mira` avec l’adresse Tailscale du serveur. Sous Windows, le pare-feu doit laisser entrer `jellyfin.exe` sur son port depuis `100.64.0.0/10`.

Hors de chez toi, la qualité **Automatique** mesure la connexion et demande à Jellyfin une vidéo adaptée. Tu peux la fixer dans **Réglages → Qualité** ou dans le lecteur.

## Ce qui change par rapport à Mira sur Windows et Mac

- **Sur iPhone et iPad, c’est le lecteur d’Apple.** La vidéo s’ouvre en plein écran, avec ses commandes : lecture, barre de progression, ±10 s, vitesse, AirPlay, image dans l’image, et le menu des sous-titres et de l’audio. L’épisode suivant démarre dans le même lecteur. **Terminé** (ou glisser vers le bas) ramène à la fiche, à l’endroit où tu t’es arrêté.
- **L’audio et les sous-titres se choisissent sur la fiche**, sous **Lecture**. Mira part des langues de **Réglages** (les mêmes que sur Windows et Mac) et retient celles choisies pour une série. Dans le lecteur d’Apple, le menu change les sous-titres texte. Il ne change l’audio que pour un fichier lu tel quel : pour un fichier converti par Jellyfin, choisis l’audio sur la fiche.
- **Le lecteur lit directement les fichiers MP4 et MOV** en H.264 ou HEVC. Les autres, par exemple les MKV, sont convertis à la volée par Jellyfin. Le plus souvent, c’est une simple remise en forme, sans réencodage, donc légère pour le PC. Les fichiers en H.264 10 bits ou avec des sous-titres en image (PGS, DVD) demandent un vrai réencodage : le PC doit être assez puissant.
- **Un titre que le lecteur refuse** est redemandé à Jellyfin, la vidéo convertie, puis tout converti, là où il en était. Si rien ne passe, Mira le dit, avec l’erreur du lecteur en petit : note-la avec les codecs du fichier (fiche du titre dans Jellyfin → Infos sur le média), c’est ce qu’il faut pour trouver pourquoi.
- **Le volume** se règle avec les boutons du téléphone. Le volume au-delà de 100 % n’existe que sur Windows et Mac.
- **Les sous-titres texte** (SRT, ASS, SSA) apparaissent dans le menu du lecteur d’Apple, même si tu as choisi « Aucun » sur la fiche ; leur aspect se règle dans **Réglages → Accessibilité → Sous-titres et sous-titres codés → Style** de l’iPhone. Pour un fichier converti par Jellyfin, ils font partie du flux : tous sont dans le menu, et en changer ne relance pas la vidéo. Les ASS des animés arrivent en texte simple : leurs couleurs et positions (panneaux, karaoké) sont perdues, mais la vidéo n’est plus réencodée pour eux. Les sous-titres en image (PGS, DVD) sont incrustés dans la vidéo par Jellyfin : le menu ne peut pas les éteindre, choisis-les sur la fiche.
- **La Dynamic Island** affiche le film pendant que tu le regardes : iOS le fait pour toute vidéo avec du son dans une app de l’écran d’accueil, et iOS 27 le demande même en plein écran. **Réglages → Masquer la Dynamic Island** l’évite, mais le film est alors muet quand l’iPhone est en mode silencieux (et il se mêle à la musique des autres apps) : une app web ne peut pas avoir les deux.

## Si quelque chose ne va pas

- **« Serveur injoignable »** : le PC est éteint ou en train de démarrer, Tailscale est éteint sur le téléphone (adresse `100.x`), ou le téléphone n’est pas sur le Wi-Fi de la maison (adresse `192.168.x`). Mira réessaie toute seule. Si ça dure, ouvre le **Guide** de Mira pour Windows : la carte **Mira sur ton téléphone** dit ce qui manque.
- **Page blanche, même à l’adresse sans `/Mira`** : le téléphone n’atteint pas Jellyfin. Le plus souvent, c’est le pare-feu de Windows (Jellyfin installé sans son service, ou l’autorisation de Windows refusée à son premier lancement) : **Guide → Mira sur ton téléphone → Tout préparer**. Avec l’adresse Tailscale, vérifie aussi que le PC apparaît dans l’app Tailscale du téléphone (même compte) et que **Allow incoming connections** est coché dans le menu de Tailscale sur le PC.
- **Page d’erreur de Safari à l’ouverture** : la toute première ouverture, ou après que l’iPhone a vidé sa mémoire de Safari, il faut que le serveur réponde. Ferme l’app (balaye-la vers le haut dans le sélecteur d’apps), puis rouvre-la une fois le PC démarré.
- **La page `/Mira` n’existe pas** : l’extension n’est pas installée, ou Jellyfin n’a pas redémarré depuis. Regarde dans **Tableau de bord → Extensions → Mes extensions** que **Mira** y figure, « Actif ».
- **La vidéo ne démarre pas, ou saccade hors de chez toi** : baisse la qualité dans **Réglages → Qualité** (ou dans le lecteur, hors iPhone et iPad).
- **« Touche pour lancer la lecture en plein écran »** : Safari veut un toucher avant d’ouvrir le lecteur d’Apple. Touche le bouton Lecture.
- **Mira web garde l’ancienne version après une mise à jour** : Jellyfin cherche les mises à jour de ses extensions au démarrage puis toutes les 24 h, et ne charge la nouvelle qu’au redémarrage suivant. Pour l’avoir tout de suite : **Tableau de bord → Tâches planifiées → Mettre à jour les extensions** (▶), puis **Tableau de bord → Redémarrer**.
