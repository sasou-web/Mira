---
title: Se connecter à Jellyfin
short: Se connecter
description: Quelle adresse taper, comment Mira trouve ton serveur, et pourquoi il demande parfois avant d’envoyer ton mot de passe.
platforms: Windows, Mac, iPhone
---

## Mira cherche ton serveur

Tant qu’aucune adresse n’est connue, l’écran de connexion cherche Jellyfin pendant deux ou trois secondes : sur cet ordinateur, sur le réseau de la maison (la découverte de Jellyfin) et sur tes appareils en ligne sur Tailscale. Il remplit la meilleure adresse trouvée et dit où il l’a trouvée ; les autres serveurs sont proposés à côté. **Chercher à nouveau** relance la recherche. Une adresse que tu as tapée n’est jamais remplacée.

L’iPhone ne peut pas découvrir les serveurs du réseau de la maison (Apple ne le permet qu’aux comptes développeur payants) : tape l’adresse, ou passe par Tailscale.

## Quelle adresse taper

Mira accepte l’adresse sous presque toutes ses formes :

| Tu tapes | Mira essaie |
| --- | --- |
| `192.168.1.20` | le PC de la maison, port 8096 |
| `nas:8096` | un nom de ton réseau, avec son port |
| `100.101.102.103` ou `jellyfin-pc` | l’adresse ou le nom du PC sur Tailscale |
| `https://jellyfin.example.com` | un serveur sur Internet |
| une adresse copiée de la page web de Jellyfin | le même serveur |

Sans `http://` ni `https://`, Mira essaie HTTPS, puis le port 8096, puis HTTP, et montre l’adresse qu’il a gardée.

> [!TIP]
> Sur le PC de Jellyfin lui-même, l’adresse est `http://127.0.0.1:8096`. Pour tes autres appareils, la carte **Mira sur ton iPhone** du guide donne la bonne. Une adresse Tailscale marche à la maison comme ailleurs.

## Quand Mira demande avant de te connecter

Mira vérifie l’adresse avant que ton mot de passe parte :

- **Ce n’est pas un serveur Jellyfin** : quelque chose répond, mais pas Jellyfin (une box, la page d’un NAS, Emby). Vérifie le port, 8096 par défaut.
- **Mira a besoin de Jellyfin 10.9** : mets Jellyfin à jour.
- **Certificat HTTPS pas reconnu** : Mira ne repasse jamais tout seul en `http://`, ton mot de passe y passerait en clair. Pour un serveur à la maison, tape son adresse `http://` complète.
- **Cette adresse passe par Internet, en http://** : ton mot de passe traverserait Internet sans chiffrement. Utilise l’adresse `https://` du serveur ou son adresse Tailscale. **Se connecter quand même** l’envoie malgré tout.

À la maison, sur ce PC et par Tailscale, rien n’est demandé : Tailscale chiffre déjà tout.

## Se connecter avec un code

Mira pour iPhone peut se connecter sans taper de mot de passe : **Se connecter avec un code** affiche six chiffres. Un administrateur les valide dans Mira pour Windows ou Mac (**Réglages → Amis & invités → Autoriser un appareil**) en choisissant le compte, ou sur la page web de Jellyfin. **Quick Connect** doit être activé dans Jellyfin (Tableau de bord → Général).

## Ce que Mira garde

Ton mot de passe n’est jamais gardé. Mira garde seulement la session ouverte par Jellyfin : protégée par Windows pour ton compte, dans le trousseau sur Mac et iPhone. **Se déconnecter** ferme aussi la session sur Jellyfin. Sous Windows et Mac, **Effacer aussi les données de ce compte sur ce PC** retire le cache et les images de ce compte.

## Tes choix te suivent

Tes langues préférées, **Passer les intros et les récaps**, l’audio et les sous-titres choisis pour chaque série et les titres retirés de Continuer à regarder sont gardés sur ton compte Jellyfin, et te suivent sur tes autres appareils. Le choix le plus récent gagne. L’apparence et les rangées de l’accueil restent propres à chaque appareil.
