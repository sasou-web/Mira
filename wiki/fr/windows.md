---
title: Installer sur Windows
short: Windows
description: Télécharger Mira pour Windows, passer SmartScreen, ajouter le moteur vidéo et garder Mira à jour.
platforms: Windows 10, Windows 11
---

## Télécharger

La [dernière version](https://github.com/sasou-web/Mira/releases/latest) propose trois fichiers pour Windows. Un seul suffit.

| Fichier | Pour |
| --- | --- |
| [Installateur](https://github.com/sasou-web/Mira/releases/download/v__VERSION__/Mira-__VERSION__-win-x64-setup.exe) | La plupart des gens. Installe Mira pour ton compte Windows seulement, sans droits d’administrateur, avec un raccourci dans le menu Démarrer. |
| [Portable](https://github.com/sasou-web/Mira/releases/download/v__VERSION__/Mira-__VERSION__-win-x64-portable.exe) | Un seul fichier qui se lance tel quel. Range-le dans un dossier à lui, comme `Documents\Mira` : il crée son dossier `data` à côté de lui. |
| [Zip](https://github.com/sasou-web/Mira/releases/download/v__VERSION__/Mira-__VERSION__-win-x64.zip) | Le dossier complet de l’application. Extrais tout le dossier `Mira` là où tu peux écrire, jamais seulement `Mira.exe`. |

Le runtime .NET est inclus : il n’y a rien d’autre à installer.

## Passer SmartScreen

Les fichiers Windows de Mira ne sont pas encore signés : Windows SmartScreen peut dire « Windows a protégé votre ordinateur ». Clique sur **Informations complémentaires**, puis **Exécuter quand même**. Tu peux d’abord vérifier que ton fichier est bien celui publié : chaque fichier a son `.sha256` à côté de lui dans la version.

```powershell
Get-FileHash .\Mira-__VERSION__-win-x64-setup.exe -Algorithm SHA256
```

La valeur doit être celle du fichier `.sha256`. D’autres façons de vérifier, et pourquoi Windows prévient : [Vérifier ton téléchargement](verify.md).

## Premier lancement

1. Ouvre Mira. La première fois, un écran de bienvenue explique ce que font Jellyfin et Mira.
2. Mira cherche Jellyfin sur ce PC, sur le réseau de la maison et sur tes appareils Tailscale, et remplit l’adresse trouvée. Entre ton nom d’utilisateur et ton mot de passe Jellyfin, puis **Se connecter**. Plus de détails dans [Se connecter à Jellyfin](sign-in.md).
3. Pas encore de Jellyfin ? Choisis **Pas encore de serveur : Mira installe Jellyfin sur ce PC**. Voir [Préparer Jellyfin](jellyfin.md).

## Le moteur vidéo

Mira lit avec mpv. Son moteur (31 Mo) n’est pas dans les fichiers de Mira : Mira télécharge une version précise des builds Windows de mpv sur SourceForge et vérifie son SHA-256 avant de s’en servir.

- Avec l’installateur, laisse cochée **Télécharger le moteur vidéo mpv**.
- Sinon, Mira le propose à la première lecture, ou dans **Réglages → Lecture → Installer le moteur mpv**.
- Tu as déjà mpv.net ou Jellyfin MPV Shim ? Mira réutilise leur moteur et ne télécharge rien. Tu peux aussi choisir une autre `libmpv-2.dll` 64 bits dans **Réglages → Lecture**.

## Mises à jour

Mira se met à jour seul. Quelques secondes après son ouverture, puis toutes les 6 heures, il demande à GitHub s’il existe une nouvelle version, la télécharge en arrière-plan, vérifie qu’elle est signée par Mira avec la taille et le SHA-256 annoncés, et l’installe quand tu fermes Mira. **Redémarrer**, dans l’avis, l’installe tout de suite.

Pour arrêter la recherche automatique, désactive **Réglages → Mira sur ce PC → Mettre à jour automatiquement**. Le bouton **Rechercher** reste là.

Tes réglages et ta session sont dans le dossier `data`, qu’une mise à jour ne touche jamais.

## Désinstaller

**Paramètres → Applications → Mira**. Le désinstallateur demande avant d’effacer tes données (non par défaut), puis liste ce qui reste sur le PC : Jellyfin si Mira l’a installé, la règle de pare-feu « Mira - Jellyfin », et le droit de lecture de Jellyfin sur tes dossiers de vidéos.
