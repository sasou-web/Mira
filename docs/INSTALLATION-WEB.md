# Mira sur iPhone, iPad et Android

Mira web est la version de Mira pour téléphone et tablette. Ton serveur Jellyfin la sert lui-même : il n’y a rien à installer sur le téléphone, ni App Store, ni compte Apple, ni application à rafraîchir. Sur iPhone, une fois ajoutée à l’écran d’accueil, elle s’ouvre comme une app, en plein écran, avec son icône.

Tu y retrouves l’interface de Mira : l’accueil avec Continuer à regarder, les films et les séries avec leurs filtres, la recherche, les fiches avec la distribution et les longues saisons par tranches de 100. Sur iPhone et iPad, les vidéos se lisent dans le lecteur d’Apple, en plein écran. Ailleurs, le lecteur de Mira gère la reprise, l’intro à passer, l’épisode suivant, les pistes audio et sous-titres, AirPlay et l’image dans l’image. Ta progression est partagée avec Mira sur Windows et sur Mac.

## 1. Ajouter Mira web à Jellyfin (une fois)

**Avec Mira pour Windows**, sur le PC où tourne Jellyfin :

1. Ouvre le **Guide** (bouton **?** à gauche, ou F1).
2. Dans **Sur tes autres appareils**, clique sur **Installer Mira web**.

Mira ajoute l’extension à Jellyfin, qui redémarre quelques secondes, puis ouvre la page avec le QR code. Il faut être connecté avec un compte administrateur de Jellyfin, ce qui est le cas si Mira a installé Jellyfin.

**À la main**, depuis la page web de Jellyfin :

1. **☰ → Tableau de bord → Extensions → Dépôts**, puis **+** :
   - nom : `Mira` ;
   - URL : `https://github.com/sasou-web/Mira/releases/latest/download/jellyfin-manifest.json`.
2. **Catalogue** : choisis **Mira**, puis **Installer**.
3. Redémarre Jellyfin (**Tableau de bord → Redémarrer**).

Jellyfin met ensuite l’extension à jour tout seul à chaque nouvelle version de Mira.

## 2. Ouvrir Mira sur le téléphone

Le téléphone doit être sur le même Wi-Fi que le serveur. Ouvre l’adresse du serveur suivie de `/Mira`, par exemple `http://192.168.1.20:8096/Mira`. Le plus simple est de scanner le QR code avec l’appareil photo :
- sur le PC, Mira pour Windows l’affiche après l’installation (**Guide → Sur tes autres appareils → Afficher le QR code**) ;
- depuis Mira web déjà ouvert ailleurs : **Réglages → Ouvrir sur un autre appareil**.

Connecte-toi ensuite avec ton compte Jellyfin.

### Dès l’allumage du PC

Pour que le téléphone trouve Mira web dès que le PC est allumé, même avant l’ouverture de ta session, ouvre le **Guide** de Mira pour Windows : la carte **Mira sur iPhone et Android** dit si c’est le cas. Sinon, **Disponible dès l’allumage du PC** règle tout en une fois, avec l’autorisation de Windows :
- le service **Jellyfin Server** démarre avec Windows, sans le délai de deux minutes de certaines installations ;
- Windows le relance s’il s’arrête sur une erreur (un arrêt demandé depuis le tableau de bord de Jellyfin est respecté) ;
- le pare-feu laisse entrer, sur le port de Jellyfin et pour Jellyfin seulement, les appareils de ton réseau local et de Tailscale. L’installateur de Jellyfin n’ajoute aucune règle pour son service.

Si le PC n’est pas encore prêt quand tu ouvres Mira, l’app s’ouvre quand même (dès qu’elle a été ouverte une fois), indique que le serveur ne répond pas encore et reprend toute seule dès qu’il répond.

Garde aussi la même adresse au PC : dans l’interface de ta box, réserve-lui son adresse IP (bail DHCP fixe, ou « IP fixe » selon la box). Sinon, après un redémarrage, la box peut lui en donner une autre, et l’app de l’écran d’accueil ne le trouve plus.

### Sur iPhone et iPad : l’avoir comme une app

Dans Safari, touche **Partager**, puis **Sur l’écran d’accueil**, et laisse **Ouvrir comme app web** activé. Mira apparaît sur l’écran d’accueil et s’ouvre en plein écran. La connexion est à faire une fois de plus dans cette app, qui garde ses propres données.

### Sur Android

Dans Chrome, menu **⋮ → Ajouter à l’écran d’accueil** (ou **Installer l’application**).

## Hors de chez toi

L’adresse ci-dessus ne marche que sur le réseau de la maison. Ailleurs, utilise **Tailscale** (gratuit), comme pour Mira sur Mac :
1. installe-le sur le PC et sur le téléphone, avec le même compte ;
2. dans Mira pour Windows, **Guide → Hors de chez toi → Autoriser Tailscale**, puis **Ouvrir le pare-feu** si le guide le propose (le pare-feu de Windows bloque souvent Tailscale quand Jellyfin a été installé sans son service) ;
3. ouvre `http://100.x.y.z:8096/Mira`, avec l’adresse Tailscale du PC, et ajoute aussi cette page à l’écran d’accueil.

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

- **« Serveur injoignable »** : le PC est éteint ou en train de démarrer, le téléphone n’est pas sur le même Wi-Fi, ou Tailscale est éteint. Mira réessaie toute seule. Si ça dure, vérifie dans le **Guide** de Mira pour Windows que Mira web est **disponible dès l’allumage du PC** (service et pare-feu).
- **Rien ne s’affiche à l’adresse Tailscale, même sans `/Mira`** : le téléphone n’atteint pas Jellyfin. Ouvre le **Guide** de Mira pour Windows : **Hors de chez toi** dit si le pare-feu de Windows bloque Tailscale, et **Ouvrir le pare-feu** le règle. Sinon, vérifie que le PC apparaît dans l’app Tailscale du téléphone (même compte) et que **Allow incoming connections** est coché dans le menu de Tailscale sur le PC.
- **Page d’erreur de Safari à l’ouverture** : la toute première ouverture, ou après que l’iPhone a vidé sa mémoire de Safari, il faut que le serveur réponde. Ferme l’app (balaye-la vers le haut dans le sélecteur d’apps), puis rouvre-la une fois le PC démarré.
- **La page `/Mira` n’existe pas** : l’extension n’est pas installée, ou Jellyfin n’a pas redémarré depuis. Regarde dans **Tableau de bord → Extensions → Mes extensions** que **Mira** y figure, « Actif ».
- **La vidéo ne démarre pas, ou saccade hors de chez toi** : baisse la qualité dans **Réglages → Qualité** (ou dans le lecteur, hors iPhone et iPad).
- **« Touche pour lancer la lecture en plein écran »** : Safari veut un toucher avant d’ouvrir le lecteur d’Apple. Touche le bouton Lecture.
- **Mira web garde l’ancienne version après une mise à jour** : Jellyfin cherche les mises à jour de ses extensions au démarrage puis toutes les 24 h, et ne charge la nouvelle qu’au redémarrage suivant. Pour l’avoir tout de suite : **Tableau de bord → Tâches planifiées → Mettre à jour les extensions** (▶), puis **Tableau de bord → Redémarrer**.
