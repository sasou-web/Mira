---
title: Dépannage
short: Dépannage
description: Les messages que Mira affiche quand quelque chose ne va pas, ce qu’ils veulent dire et quoi faire.
platforms: Windows, Mac, iPhone
---

Mira dit ce qui ne va pas avec ses mots, avec un bouton qui aide quand il y en a un. Trouve le message ci-dessous, ou cherche un de ses mots dans ce wiki.

## Jellyfin injoignable

| Message ou symptôme | Quoi faire |
| --- | --- |
| Marche à la maison, pas ailleurs | L’adresse de la maison (`192.168…`) ne marche que sur ton Wi-Fi. Passe par [Tailscale](away.md) : sur le PC de Jellyfin, **Tout préparer** du guide ; sur ton appareil, Tailscale intégré ou l’app Tailscale. |
| « … n’est pas connecté à Tailscale : il est éteint, en veille ou hors ligne » | Le PC de Jellyfin a quitté Tailscale. Allume-le ou réveille-le. Mira recharge l’accueil dès qu’il revient. |
| Injoignable après un moment sans utiliser le PC de Jellyfin | Il s’est mis en veille, et Tailscale ne peut pas le réveiller. **Tout préparer** le garde éveillé sur secteur ; sur un portable, règle la fermeture du capot sur **Ne rien faire** sur secteur. |
| Injoignable après un redémarrage du PC de Jellyfin, jusqu’à ce que quelqu’un ouvre une session Windows | Tailscale attend une session : **Tout préparer** active **Run unattended**, ou menu de Tailscale → **Preferences** → **Run unattended**. |
| Tailscale redemande la connexion sur le PC de Jellyfin | Sa clé a expiré (tous les 180 jours par défaut). Reconnecte-toi, puis dans la [console](https://login.tailscale.com/admin/machines), **⋯ → Disable key expiry** sur ce PC. |
| « En attente d’approbation dans la console de Tailscale » | Ton réseau Tailscale fait approuver chaque nouvel appareil : **Ouvrir la console de Tailscale** et approuve l’appareil. |
| Tout affiche « Connexion… », sur tous les appareils | Tailscale lui-même est peut-être en panne : regarde [status.tailscale.com](https://status.tailscale.com). Voir [Quand Tailscale lui-même est en panne](away.md#quand-tailscale-lui-meme-est-en-panne). |
| « La Sécurité Windows bloque mira-tailscale.exe » | Un antivirus bloque Tailscale intégré. Installe plutôt l’app Tailscale sur ce PC : Mira passe alors par elle. |
| « macOS bloque mira-tailscale » | La marque « téléchargé depuis Internet » est restée sur Mira.app : **Copier la commande** dans les réglages de Tailscale intégré, colle-la dans le Terminal, relance Mira. |
| L’iPhone ne voit pas Jellyfin à la maison | L’accès au réseau local d’iOS est coupé : **Réglages → Apps → Mira → Réseau local**. |
| « Jellyfin est arrêté sur ce PC » | **Démarrer Jellyfin**, dans l’avis ou sur la carte du guide. S’il s’arrête encore, Mira montre la cause tirée du journal de Jellyfin (port 8096 pris, disque plein…). |

## Connexion impossible

| Message | Quoi faire |
| --- | --- |
| « Ce n’est pas un serveur Jellyfin » | Autre chose répond à cette adresse (box, NAS, Emby) : vérifie le port, 8096 par défaut. |
| « Mira a besoin de Jellyfin 10.9 » | Mets Jellyfin à jour. |
| « Certificat HTTPS pas reconnu » | Pour un serveur à la maison, tape son adresse `http://` complète (Mira la propose). Sinon, installe un certificat valide. |
| « Cette adresse passe par Internet, en http:// » | Ton mot de passe passerait sans chiffrement. Utilise l’adresse `https://` ou l’adresse Tailscale. |
| « Ce Jellyfin a déjà un compte administrateur » | Son installation a été commencée avec un autre compte : termine-la sur `http://127.0.0.1:8096`, puis connecte-toi. |
| « Un autre programme répond déjà sur le port 8096 » | Ce n’est pas Jellyfin : ferme ce programme, ou installe Jellyfin sur un autre PC. |

## Lecture impossible

| Symptôme | Quoi faire |
| --- | --- |
| La bibliothèque s’affiche, rien ne se lit (Windows) | **Réglages → Lecture** : installe le moteur mpv, ou choisis une DLL libmpv 64 bits. |
| « Le moteur vidéo n’a pas pu être téléchargé » | Vérifie la connexion à Internet, puis réessaie depuis **Réglages → Lecture**. Il vient de `downloads.sourceforge.net`. |
| Saccades hors de chez toi | Baisse la **Qualité** dans le menu **⋮** du lecteur, ou choisis **Auto**. |
| Image saccadée ou noire (Windows) | Coupe le **Décodage matériel** dans **Réglages → Lecture**. |
| Image grise ou noire (Mac) | Dans le lecteur, **⋮ → Image grise ou noire ? Réparer**. Chaque réparation va un cran plus loin ; Mira garde celle qui marche. |
| « Lecture impossible » | Mira a rouvert le flux et Jellyfin n’a toujours pas pu servir le fichier. L’administrateur de Jellyfin trouve la raison dans **Réglages → Entretien → Lectures impossibles signalées**. |
| Un fichier ajouté n’apparaît pas | Vérifie qu’il est dans **Films**, **Séries** ou **Animes**, et nommé comme Jellyfin l’attend (voir [Nommer tes vidéos](jellyfin.md#nommer-tes-videos)). Puis **Actualiser** dans la bibliothèque. |

## Mises à jour

| Symptôme | Quoi faire |
| --- | --- |
| Windows : une mise à jour ne s’installe pas | **Réglages → Mira sur ce PC** montre la dernière erreur ; `data\updates\update.log` donne les détails. Le dossier de Mira doit rester modifiable. |
| Mac : Mira demande le trousseau après une mise à jour | Seulement après une mise à jour par Homebrew ou à la main, une fois par version : choisis **Toujours autoriser**. Voir [Installer sur Mac](mac.md#mises-a-jour). |
| iPhone : Mira ne s’ouvre plus | Elle a expiré au bout de 7 jours. Ouvre AltStore à la maison et touche **Refresh All**. Rien n’est perdu. |

## Toujours bloqué ?

Demande sur [Discord](https://discord.gg/Bh3F6PDUvB), ou [ouvre une issue](https://github.com/sasou-web/Mira/issues/new/choose) avec les versions de Mira, de ton système et de Jellyfin. Ne joins jamais un mot de passe, un jeton, le dossier `data` de Mira ou des médias personnels.

Sur Mac, un journal du lecteur aide : dans le Terminal,

```sh
MIRA_MPV_LOG=$HOME/Desktop/mpv.log /Applications/Mira.app/Contents/MacOS/Mira
```

écrit `mpv.log` sur ton bureau.
