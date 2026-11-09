---
title: Vérifier ton téléchargement
short: Vérifier
description: Pourquoi Windows et macOS préviennent pour Mira, et trois façons de vérifier que le fichier reçu est bien celui publié.
platforms: Windows, Mac, iPhone
---

Les fichiers de Mira ne sont pas encore signés par un certificat payant. Windows SmartScreen et Gatekeeper de macOS préviennent pour toute app qu’ils ne connaissent pas, signée ou non : un fichier non signé repart sans réputation à chaque nouvelle version. L’avertissement ne dit rien du fichier lui-même. Voici comment le vérifier, en une minute.

## 1. Comparer le SHA-256

Chaque fichier d’une version a son `.sha256` à côté de lui, publié au même moment, dans une version qui ne peut plus être modifiée (releases immuables de GitHub : une fois sortie, personne ne peut remplacer un fichier, même pas le mainteneur).

Windows, dans PowerShell :

```powershell
Get-FileHash .\Mira-__VERSION__-win-x64-setup.exe -Algorithm SHA256
```

Mac, dans le Terminal :

```sh
shasum -a 256 ~/Downloads/Mira-__VERSION__-mac-arm64.dmg
```

La valeur doit être celle du fichier `.sha256`. Pareil pour l’`.ipa` de l’iPhone.

## 2. Vérifier d’où il vient

Aucun fichier d’une version n’est compilé sur un PC : GitHub Actions construit chacun depuis le commit de la version, au grand jour ([les workflows](https://github.com/sasou-web/Mira/tree/main/.github/workflows)), et signe pour lui une attestation de construction. Avec [GitHub CLI](https://cli.github.com/) :

```sh
gh attestation verify Mira-__VERSION__-win-x64-setup.exe -R sasou-web/Mira
gh release verify v__VERSION__ -R sasou-web/Mira
```

La première ligne nomme le workflow et le commit qui ont produit ton fichier ; la seconde vérifie la version entière. Les attestations commencent avec la version qui suit la 0.9.5.

## 3. Le chercher sur VirusTotal

Le workflow de publication envoie l’installateur Windows, le portable et l’image disque Mac à [VirusTotal](https://www.virustotal.com/), où quelque soixante-dix antivirus les analysent. Ouvre `https://www.virustotal.com/gui/file/` suivi du SHA-256 de l’étape 1. Si le fichier y est inconnu, envoie-le toi-même : il est déjà public.

> [!NOTE]
> Un programme tout neuf et non signé reçoit parfois un verdict « apprentissage automatique » (un nom qui finit par `!ml`) d’un seul moteur. C’est un faux positif connu sur les fichiers neufs non signés ; regarde le score entier, pas une ligne.

## 4. Ou laisser un gestionnaire de paquets vérifier pour toi

- **Mac** : `brew install --cask sasou-web/mira/mira` depuis [le tap Homebrew de Mira](https://github.com/sasou-web/homebrew-mira) : Homebrew vérifie lui-même le SHA-256 de l’image. Voir [Installer sur Mac](mac.md#avec-homebrew).
- **Windows** : winget arrive ; d’ici là, l’installateur de la version.

## Ce que Mira vérifie elle-même

Une fois installée, Mira se met à jour seule : elle n’installe qu’une version dont le manifeste est signé par la clé de Mira et dont les fichiers ont la taille et le SHA-256 annoncés. Voir [Installer sur Windows](windows.md#mises-a-jour) et [Installer sur Mac](mac.md#mises-a-jour).

## Le code

Mira est open source sous licence MIT : [github.com/sasou-web/Mira](https://github.com/sasou-web/Mira). Ce qu’elle envoie et à qui : [Confidentialité](privacy.md). Un problème ? [Signale-le](privacy.md#signaler-un-probleme).
