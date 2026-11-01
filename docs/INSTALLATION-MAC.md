# Installer Mira sur Mac

Mira pour Mac lit ta bibliothèque Jellyfin avec le même lecteur mpv que la version Windows : reprise, épisode suivant, pistes audio et sous-titres, opening à passer, volume jusqu’à 200 %.

## Prérequis

- Un Mac à puce Apple (M1, M2, M3, M4…) sous **macOS 15 Sequoia** ou plus récent (macOS 14 Sonoma : jusqu’à la 0.7.4).
- Un serveur **Jellyfin 10.9** ou plus récent, sur ton réseau ou joignable avec Tailscale. Mira pour Mac ne l’installe pas : il se connecte à celui que tu as déjà, par exemple celui du PC Windows où tourne Mira.

## Installation

1. Télécharge **Mira-x.y.z-mac-arm64.dmg** sur la [page des versions](https://github.com/sasou-web/Mira/releases/latest).
2. Ouvre l’image disque et fais glisser **Mira** sur **Applications**.
3. Ouvre Mira depuis Applications. macOS l’arrête la première fois (voir ci-dessous) ; c’est normal.

### Ou avec Homebrew

Si tu as [Homebrew](https://brew.sh), une commande dans le **Terminal** remplace les étapes 1 et 2 :

```
brew install --cask sasou-web/mira/mira
```

Elle vient du tap de Mira ([sasou-web/homebrew-mira](https://github.com/sasou-web/homebrew-mira)), pas du catalogue officiel de Homebrew, qui n’accepte que les apps notarisées par Apple. Homebrew vérifie l’empreinte de l’image (la même que son `.sha256`) et garde la protection de macOS : la première ouverture se fait comme ci-dessous. Mira se met ensuite à jour toute seule ; `brew uninstall --cask --zap mira` la retire avec ses réglages.

### Autoriser Mira la première fois

Mira n’est pas signée par un compte développeur Apple payant : macOS ne peut pas la vérifier et refuse de l’ouvrir au premier lancement. À faire une seule fois :

1. Ouvre Mira une première fois, puis ferme le message.
2. Va dans **Réglages Système → Confidentialité et sécurité**.
3. En bas, à côté de « Mira a été bloquée », clique sur **Ouvrir quand même**, puis confirme.

Si macOS dit que Mira est « endommagée » ou si le bouton n’apparaît pas, ouvre le **Terminal** et tape :

```
xattr -dr com.apple.quarantine /Applications/Mira.app
```

Cette commande retire seulement la marque « téléchargé depuis Internet » de Mira ; elle ne change aucun réglage de sécurité du Mac.

## Première connexion

Saisis l’adresse de ton serveur Jellyfin (par exemple `192.168.1.20` ou `nas:8096`), ton nom d’utilisateur et ton mot de passe. Mira trouve seule le bon port et le bon protocole. Hors de chez toi, utilise l’adresse Tailscale du serveur (`100.x.y.z`) ou son nom sur Tailscale ; sans l’app Tailscale sur ce Mac, **Tailscale intégré** (ci-dessous) le joint quand même.

Ton mot de passe n’est jamais enregistré : Mira garde seulement la session ouverte par Jellyfin. Son code d’accès va dans le trousseau de ton Mac (élément « Mira »), le reste dans `~/Library/Application Support/Mira`, lisible par ton compte macOS uniquement. **Se déconnecter** (Réglages → Compte) efface les deux et ferme aussi la session sur Jellyfin ; avec **Effacer aussi les données de ce compte sur ce Mac**, le cache et les affiches de ce compte quittent le Mac.

## Tailscale intégré : ton Jellyfin loin de chez toi, sans l’app Tailscale

Si ton Jellyfin est sur Tailscale (le PC où il tourne a l’app Tailscale) et que ce Mac ne l’a pas, Mira rejoint ton réseau Tailscale elle-même, comme un appareil de plus, seulement quand elle est ouverte. Rien à installer : le nœud Tailscale de Mira (`mira-tailscale`) est dans Mira.app.

1. Sur l’écran de connexion, tape l’adresse Tailscale du serveur (`100.x.y.z`) ou son nom, ou laisse Mira chercher : sous le champ, Mira propose **Se connecter à Tailscale** (« Ce serveur est sur Tailscale », ou « Pas chez toi ? » quand elle ne trouve rien).
2. Clique sur **Se connecter à Tailscale** : ton navigateur ouvre la page de connexion de Tailscale. Connecte-toi avec le compte de ton réseau Tailscale, celui du PC de Jellyfin.
3. De retour dans Mira, la ligne dit « Tailscale intégré · Connecté » : connecte-toi à Jellyfin comme d’habitude. Sans adresse tapée, Mira cherche à nouveau ton serveur parmi tes appareils Tailscale et le propose.

Ensuite, le nœud démarre avec Mira et s’arrête avec elle. **Réglages → Tailscale intégré** dit son état et par où Mira joint Jellyfin (en direct à la maison, par l’app Tailscale, ou par Tailscale intégré). Quand Jellyfin ne répond pas, Mira dit pourquoi : PC de Jellyfin éteint ou en veille (par son nom sur Tailscale), connexion à Tailscale expirée…, avec le bouton qui aide. Si l’app Tailscale est connectée sur ce Mac, Mira passe par elle et ne propose pas Tailscale intégré.

- **Dans ta console Tailscale**, l’appareil s’appelle « Mira-<nom de ce Mac> ». Sa connexion expire après 180 jours (réglage par défaut de Tailscale) : Mira te prévient deux semaines avant. Pour ne plus avoir à te reconnecter, choisis **Disable key expiry** dans le menu ⋯ de cet appareil, dans la [console](https://login.tailscale.com/admin/machines).
- **Pare-feu de macOS** : s’il est activé, macOS peut demander d’autoriser « mira-tailscale » à accepter des connexions entrantes. Accepte : la connexion directe est plus rapide. Refuser marche aussi, par un relais de Tailscale.
- **« macOS bloque mira-tailscale »** : la marque « téléchargé depuis Internet » est restée sur Mira.app. Dans **Réglages → Tailscale intégré**, clique sur **Copier la commande**, colle-la dans le Terminal, puis relance Mira : c’est la commande `xattr` de l’installation, avec l’emplacement de ton Mira. Mira ne retire jamais cette marque elle-même.
- **Ce que voit Tailscale** : rien avant **Se connecter à Tailscale**. Ensuite, comme pour l’app Tailscale, le compte choisi dans le navigateur, le nom de l’appareil, son système et ses adresses IP (publique et locales, pour la connexion directe) vont aux serveurs de Tailscale. Tes vidéos restent chiffrées de bout en bout, en direct ou par un relais de Tailscale. Mira n’envoie aucun journal à Tailscale.
- **Où c’est gardé** : l’identité du nœud est dans `~/Library/Application Support/Mira/tailscale`, chiffrée par une clé rangée dans le trousseau, avec la session (élément « Mira » ; « Mira (Tailscale) » avant la 0.8.8). **Se déconnecter de Tailscale** le dit à Tailscale (l’appareil reste dans ta console, « Logged out », jusqu’à ce que tu l’y supprimes), puis efface la clé et le dossier.

## Raccourcis

| Action | Raccourci |
| --- | --- |
| Rechercher | ⌘F |
| Réglages | ⌘, |
| Retour | Échap, ⌘[ ou bouton arrière de la souris |
| Lecture / pause | Espace / K, ou un clic sur la vidéo |
| Reculer / avancer de 10 secondes | ← / → |
| Volume (jusqu’à 200 %) | ↑ / ↓, ou la molette sur le haut-parleur |
| Couper le son | M |
| Plein écran | F, ou un double-clic sur la vidéo |
| Passer l’opening ou l’ending | S |
| Épisode suivant | N |

## Mises à jour

Depuis la 0.8.8, Mira se met à jour seule. À l’ouverture puis toutes les six heures, elle demande à GitHub s’il existe une nouvelle version, télécharge son image disque et la garde seulement si sa taille et son empreinte SHA-256 sont celles du manifeste signé de la release (la même vérification que sous Windows). Elle prépare la nouvelle Mira à côté d’elle, puis l’installe quand tu quittes Mira ; **Redémarrer**, dans l’avis « Mira x.y.z est prête », l’installe tout de suite et la rouvre. Tes réglages et ta session restent en place, et macOS ne redemande pas l’autorisation de la première fois : c’est Mira qui a téléchargé et vérifié la nouvelle version, pas le navigateur.

Mira ne peut pas se remplacer elle-même quand elle tourne depuis son image disque, depuis la copie protégée que macOS fait d’une app ouverte directement dans Téléchargements, ou depuis un dossier que ton compte ne peut pas modifier (Applications d’un compte administrateur, vu depuis un compte standard). Elle télécharge et vérifie quand même la nouvelle version, et **Ouvrir l’image disque** te laisse la glisser dans Applications, comme à l’installation. C’est aussi ce qu’elle propose si un remplacement a échoué. La 0.8.7 et les versions d’avant n’ont pas ce code : pour passer à la 0.8.8, remplace Mira à la main une dernière fois.

Au premier lancement de la nouvelle version, macOS demande aussi ton mot de passe de session (« Mira veut utiliser vos informations confidentielles gardées dans « Mira » de votre trousseau ») pour la laisser lire la session et la clé de Tailscale intégré rangées dans le trousseau : choisis **Toujours autoriser**, c’est l’élément de Mira elle-même. Mira n’étant pas signée par un compte Apple Developer, macOS ne reconnaît pas la nouvelle version comme la même app, d’où une question par mise à jour (une seule depuis la 0.8.8, la session et la clé étant dans le même élément). Si tu refuses, Mira te demande simplement de te reconnecter à Jellyfin, et Tailscale intégré attend : **Réessayer** dans ses Réglages repose la question.

Pour que Mira ne contacte plus GitHub d’elle-même, désactive **Réglages → Mises à jour → Rechercher les nouvelles versions** ; le bouton **Rechercher une mise à jour** reste disponible.

Chaque image disque est publiée avec son empreinte SHA-256 (fichier `.sha256`). Pour vérifier celle que tu as téléchargée, télécharge aussi son fichier `.sha256` dans le même dossier (ici Téléchargements), puis dans le Terminal :

```
cd ~/Downloads
shasum -a 256 -c Mira-x.y.z-mac-arm64.dmg.sha256
```

`OK` : le fichier est identique à celui de la release.

## Ce qui reste propre à Windows

TorLink tourne sur le PC Windows de Jellyfin : depuis le Mac, la page **Téléchargements** (compte administrateur de Jellyfin) lui envoie tes recherches et tes téléchargements, et montre leur progression. L’installation de Jellyfin depuis Mira, le mini-lecteur et l’intégration à la barre des tâches n’existent que sur Windows.

## Si quelque chose ne fonctionne pas

- **Pas d’image, mais le son** : dans le Terminal, `MIRA_SOFTWARE_VIDEO=1 /Applications/Mira.app/Contents/MacOS/Mira` lance Mira avec l’affichage vidéo logiciel. S’il fonctionne, signale-le dans une [issue](https://github.com/sasou-web/Mira/issues).
- **Pour un rapport détaillé** : `MIRA_MPV_LOG=$HOME/Desktop/mpv.log /Applications/Mira.app/Contents/MacOS/Mira` écrit le journal du lecteur sur le bureau ; joins-le à l’issue.
- **Repartir de zéro** : déconnecte-toi d’abord (Réglages → Compte), puis quitte Mira et supprime le dossier `~/Library/Application Support/Mira`. Sans déconnexion, supprime aussi l’élément « Mira » (et « Mira (Tailscale) » d’une version d’avant la 0.8.8) dans Trousseaux d’accès, et l’appareil « Mira-<nom de ce Mac> » dans la console de Tailscale.
