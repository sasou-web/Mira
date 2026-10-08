# Installer Mira sur Mac

Mira pour Mac lit ta bibliothèque Jellyfin avec le même lecteur mpv que la version Windows : reprise, épisode suivant, pistes audio et sous-titres, opening à passer, volume jusqu’à 200 %.

## Prérequis

- Un Mac à puce Apple (M1, M2, M3, M4…) sous **macOS 14 Sonoma** ou plus récent.
- Un serveur **Jellyfin 10.9** ou plus récent, sur ton réseau ou joignable avec Tailscale. Mira pour Mac ne l’installe pas : il se connecte à celui que tu as déjà, par exemple celui du PC Windows où tourne Mira.

## Installation

1. Télécharge **Mira-x.y.z-mac-arm64.dmg** sur la [page des versions](https://github.com/sasou-web/Mira/releases/latest).
2. Ouvre l’image disque et fais glisser **Mira** sur **Applications**.
3. Ouvre Mira depuis Applications. macOS l’arrête la première fois (voir ci-dessous) ; c’est normal.

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

Saisis l’adresse de ton serveur Jellyfin (par exemple `192.168.1.20` ou `nas:8096`), ton nom d’utilisateur et ton mot de passe. Mira trouve seule le bon port et le bon protocole. Hors de chez toi, utilise l’adresse Tailscale du serveur (`100.x.y.z`) ; le guide de Mira sur Windows explique comment l’obtenir.

Ton mot de passe n’est jamais enregistré : Mira garde seulement la session ouverte par Jellyfin, dans `~/Library/Application Support/Mira`, lisible par ton compte macOS uniquement.

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

Mira vérifie à chaque lancement si une nouvelle version existe pour Mac et propose de la télécharger. Remplace alors l’ancienne Mira dans Applications par la nouvelle ; tes réglages et ta session restent dans `~/Library/Application Support/Mira`. L’autorisation de la première fois est à refaire pour chaque nouvelle version.

## Ce qui reste propre à Windows

TorLink, l’installation de Jellyfin depuis Mira, le mini-lecteur et l’intégration à la barre des tâches n’existent que sur Windows.

## Si quelque chose ne fonctionne pas

- **Pas d’image, mais le son** : dans le Terminal, `MIRA_SOFTWARE_VIDEO=1 /Applications/Mira.app/Contents/MacOS/Mira` lance Mira avec l’affichage vidéo logiciel. S’il fonctionne, signale-le dans une [issue](https://github.com/sasou-web/Mira/issues).
- **Pour un rapport détaillé** : `MIRA_MPV_LOG=$HOME/Desktop/mpv.log /Applications/Mira.app/Contents/MacOS/Mira` écrit le journal du lecteur sur le bureau ; joins-le à l’issue.
- **Repartir de zéro** : quitte Mira et supprime le dossier `~/Library/Application Support/Mira`.
