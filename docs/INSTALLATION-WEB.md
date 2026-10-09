# Mira sur iPhone, iPad et Android

Mira web est la version de Mira pour téléphone et tablette. Ton serveur Jellyfin la sert lui-même : il n’y a rien à installer sur le téléphone, ni App Store, ni compte Apple, ni application à rafraîchir. Sur iPhone, une fois ajoutée à l’écran d’accueil, elle s’ouvre comme une app, en plein écran, avec son icône.

Tu y retrouves l’interface de Mira : l’accueil avec Continuer à regarder, les films et les séries avec leurs filtres, la recherche, les fiches avec la distribution et les longues saisons par tranches de 100. Le lecteur gère la reprise, l’intro à passer, l’épisode suivant, les pistes audio et sous-titres, AirPlay et l’image dans l’image. Ta progression est partagée avec Mira sur Windows et sur Mac.

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

### Sur iPhone et iPad : l’avoir comme une app

Dans Safari, touche **Partager**, puis **Sur l’écran d’accueil**, et laisse **Ouvrir comme app web** activé. Mira apparaît sur l’écran d’accueil et s’ouvre en plein écran. La connexion est à faire une fois de plus dans cette app, qui garde ses propres données.

### Sur Android

Dans Chrome, menu **⋮ → Ajouter à l’écran d’accueil** (ou **Installer l’application**).

## Hors de chez toi

L’adresse ci-dessus ne marche que sur le réseau de la maison. Ailleurs, utilise **Tailscale** (gratuit), comme pour Mira sur Mac :
1. installe-le sur le PC et sur le téléphone, avec le même compte ;
2. dans Mira pour Windows, **Guide → Hors de chez toi → Autoriser Tailscale** ;
3. ouvre `http://100.x.y.z:8096/Mira`, avec l’adresse Tailscale du PC, et ajoute aussi cette page à l’écran d’accueil.

Hors de chez toi, la qualité **Automatique** mesure la connexion et demande à Jellyfin une vidéo adaptée. Tu peux la fixer dans **Réglages → Qualité** ou dans le lecteur.

## Ce qui change par rapport à Mira sur Windows et Mac

- **Le lecteur est celui de Safari.** Il lit directement les fichiers MP4 et MOV en H.264 ou HEVC. Les autres, par exemple les MKV, sont convertis à la volée par Jellyfin. Le plus souvent, c’est une simple remise en forme, sans réencodage, donc légère pour le PC. Les fichiers en H.264 10 bits ou avec des sous-titres en image (PGS, DVD) demandent un vrai réencodage : le PC doit être assez puissant.
- **Le volume** se règle avec les boutons du téléphone. Le volume au-delà de 100 % n’existe que sur Windows et Mac.
- **Les sous-titres texte** (SRT, ASS) sont affichés par Safari, aussi dans le plein écran d’iOS. Les sous-titres en image sont incrustés dans la vidéo par Jellyfin.

## Si quelque chose ne va pas

- **« Serveur injoignable »** : le téléphone n’est pas sur le même Wi-Fi, ou Tailscale est éteint. Vérifie aussi que le pare-feu de Windows autorise Jellyfin.
- **La page `/Mira` n’existe pas** : l’extension n’est pas installée, ou Jellyfin n’a pas redémarré depuis. Regarde dans **Tableau de bord → Extensions → Mes extensions** que **Mira** y figure, « Actif ».
- **La vidéo ne démarre pas, ou saccade hors de chez toi** : baisse la qualité dans le lecteur (bouton des réglages, en haut à droite).
- **Pas d’image mais le son** : touche le bouton de plein écran. Le lecteur d’iOS lit alors la vidéo directement. Signale-le dans une [issue](https://github.com/sasou-web/Mira/issues).
