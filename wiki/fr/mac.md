---
title: Installer sur Mac
short: Mac
description: Installer Mira sur un Mac avec l’image disque ou Homebrew, l’autoriser la première fois, et la laisser se mettre à jour.
platforms: macOS 15 ou plus récent, puce Apple
---

Mira pour Mac lit ta bibliothèque Jellyfin avec le même lecteur mpv que sous Windows. Il lui faut un Mac à puce Apple (M1 ou plus récent) sous macOS 15 Sequoia ou plus récent, et un serveur Jellyfin que tu as déjà : Mira pour Mac n’installe pas Jellyfin.

## Avec l’image disque

1. Télécharge [Mira-__VERSION__-mac-arm64.dmg](https://github.com/sasou-web/Mira/releases/download/v__VERSION__/Mira-__VERSION__-mac-arm64.dmg).
2. Ouvre-la et fais glisser **Mira** sur **Applications**.
3. Ouvre Mira depuis Applications. macOS l’arrête la première fois : voir plus bas.

## Avec Homebrew

Si tu utilises [Homebrew](https://brew.sh), une commande dans le Terminal remplace les étapes 1 et 2 :

```sh
brew install --cask sasou-web/mira/mira
```

Elle vient du tap de Mira, [sasou-web/homebrew-mira](https://github.com/sasou-web/homebrew-mira) : le catalogue principal de Homebrew n’accepte que les apps notarisées par Apple. Homebrew vérifie le SHA-256 de l’image et garde la protection de macOS : la première ouverture se fait comme ci-dessous. Mira se met ensuite à jour seule ; `brew uninstall --cask --zap mira` la retire avec ses réglages.

## Autoriser Mira la première fois

Mira n’est pas signée par un compte développeur Apple payant : macOS ne peut pas la vérifier et refuse de l’ouvrir la première fois. Une seule fois :

1. Ouvre Mira, puis ferme le message.
2. Va dans **Réglages Système → Confidentialité et sécurité**.
3. En bas, à côté de « Mira a été bloquée », clique sur **Ouvrir quand même**, puis confirme.

Si macOS dit que Mira est « endommagée », ou si le bouton n’apparaît pas, tape ceci dans le Terminal :

```sh
xattr -dr com.apple.quarantine /Applications/Mira.app
```

Elle retire seulement de Mira la marque « téléchargé depuis Internet ». Elle ne change aucun réglage de sécurité de ton Mac.

## Première connexion

Tape l’adresse de ton serveur (`192.168.1.20`, `nas:8096`…), ton nom d’utilisateur et ton mot de passe. Mira trouve seule le bon port et le bon protocole. Hors de chez toi, utilise l’adresse Tailscale du serveur, ou laisse [Tailscale intégré](away.md) le joindre sans l’app Tailscale.

Ton mot de passe n’est jamais gardé : Mira garde seulement la session ouverte par Jellyfin, dans le trousseau de ton Mac (élément « Mira »). Le reste est dans `~/Library/Application Support/Mira`, lisible par ton compte macOS seulement.

## Mises à jour

Mira se met à jour seule. À l’ouverture, puis toutes les 6 heures, elle demande à GitHub s’il existe une nouvelle version, télécharge l’image disque et ne la garde que si sa taille et son SHA-256 sont ceux du manifeste signé de la version. Elle l’installe quand tu quittes Mira, ou tout de suite avec **Redémarrer** dans l’avis.

> [!NOTE]
> Quand Mira se met à jour seule, l’ancienne version passe ta session à la nouvelle en quittant : macOS ne demande rien (depuis la version d’après la 0.9.4 : la mise à jour vers elle demande encore une fois). Après une mise à jour faite autrement (Homebrew, ou l’image disque glissée à la main), macOS demande une fois ton mot de passe de session pour laisser Mira lire son propre élément du trousseau : choisis **Toujours autoriser**. Ça arrive parce que Mira n’est pas signée par un compte développeur Apple. Si tu refuses, Mira te demande simplement de te reconnecter à Jellyfin.

Mira ne peut pas se remplacer quand elle tourne depuis l’image disque, depuis le dossier Téléchargements (macOS lance une copie protégée), ou depuis un dossier que ton compte ne peut pas modifier. Elle télécharge et vérifie quand même la nouvelle version, et **Ouvrir l’image disque** te laisse la glisser dans Applications.

Pour arrêter la recherche automatique, désactive **Réglages → Mira sur ce Mac → Mettre à jour automatiquement**.

## Fenêtre et menus

Mira se comporte comme les apps du Mac :

- La fenêtre revient où tu l’as laissée, sur le même écran, à la même taille (au centre si cet écran n’est plus là).
- <kbd>⌘</kbd> + <kbd>W</kbd> ou le bouton rouge ferme la fenêtre, Mira reste dans le Dock : un clic sur son icône la ramène. Ce qui jouait s’arrête, et où tu en étais est gardé. <kbd>⌘</kbd> + <kbd>Q</kbd> quitte Mira.
- **Aide** ouvre ce guide, **Raccourcis clavier** (<kbd>⌘</kbd> + <kbd>/</kbd>) et **Exporter un rapport…**, le fichier à joindre quand tu signales un problème.

## Trackpad

Mira suit les réglages de ton trackpad (défilement naturel, balayer entre les pages).

- Dans la bibliothèque : deux doigts sur le côté pour revenir et avancer (la page suit tes doigts, comme dans Safari ; lâche avant mi-chemin pour rester), pincer pour changer la taille des affiches, clic forcé sur une affiche pour son menu.
- Dans le lecteur : à gauche ou à droite pour avancer et reculer, en haut ou en bas pour le volume ; écarter deux doigts passe en plein écran, pincer en sort, puis réduit le lecteur en mini-lecteur.

## Repartir de zéro

Déconnecte-toi d’abord (**Réglages → Compte & serveur**), quitte Mira, puis supprime `~/Library/Application Support/Mira`.
