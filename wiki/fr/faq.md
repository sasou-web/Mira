---
title: Questions
short: Questions
description: Des réponses courtes à ce qu’on demande le plus sur Mira.
---

## Mira est gratuite ?

Oui, entièrement, sans compte, abonnement ni pub. Son code est ouvert, sous licence MIT.

## Mira fournit des films ?

Non. Mira lit ce qui est sur ton propre serveur Jellyfin. Elle ne fournit ni film, ni source, ni contenu.

## Il me faut Jellyfin ?

Oui : Jellyfin est le serveur qui garde tes vidéos et les sert. Si tu ne l’as pas, Mira pour Windows l’installe sur ton PC et le prépare pour toi. Voir [Préparer Jellyfin](jellyfin.md).

## Mira marche avec Plex ou Emby ?

Non, seulement avec Jellyfin (10.9 ou plus récent).

## Il y a Mira pour Android, une télé ou un navigateur ?

Pas pour l’instant. Mira web, qui se lisait dans le navigateur du téléphone, s’est arrêtée avec la 0.9.4 pour se concentrer sur les apps. Sur une télé ou une console, l’app de Jellyfin marche avec le même serveur : le guide de Mira donne l’adresse à taper.

## Pourquoi Mira n’est pas sur l’App Store ?

Y publier demande un compte développeur Apple payant. AltStore Classic installe Mira avec un identifiant Apple gratuit à la place : voir [Installer sur iPhone](iphone.md).

## Pourquoi Windows ou macOS m’avertit ?

Les fichiers de Mira ne sont pas encore signés par un certificat payant. Windows SmartScreen et Gatekeeper de macOS avertissent pour toute app non signée. Le SHA-256 de chaque fichier est publié à côté de lui, dans une version qui ne peut plus être modifiée. Voir [Installer sur Windows](windows.md#passer-smartscreen) et [Installer sur Mac](mac.md#autoriser-mira-la-premiere-fois).

## Mira convertit mes vidéos ?

À la maison, non : Mira lit chaque fichier tel quel, 4K et HDR compris. Hors de chez toi, si tu choisis une **Qualité** plus basse, Jellyfin convertit le fichier pendant que tu regardes.

## Mes amis peuvent regarder ma bibliothèque ?

De deux façons. Donne-leur un compte à eux (**Réglages → Amis & invités → Ajouter un ami**, en administrateur de Jellyfin) : ils choisissent quoi regarder, quand ils veulent, et il leur faut Tailscale pour joindre ton serveur depuis chez eux. Ou envoie ton [lien de party](together.md#ton-lien-de-party) : ils regardent avec toi, dans ton groupe, sans compte ni Tailscale.

## C’est sûr d’ouvrir mon serveur à des amis ?

Ton lien de party n’ouvre à Internet que la page de Mira et sa passerelle d’invités, jamais le tableau de bord ni la connexion de Jellyfin. Chaque invité a un compte caché limité, que tu peux retirer à tout moment. Les comptes que tu crées pour des amis ne voient que les bibliothèques choisies, sans téléchargement.

## Où sont mes données ?

Ta bibliothèque, ta progression et les comptes sont sur ton serveur Jellyfin. Mira garde un cache et tes réglages sur chaque appareil, et rien nulle part ailleurs. Voir [Vie privée](privacy.md).

## Comment aider ?

Parles-en autour de toi, signale les bugs sur [GitHub](https://github.com/sasou-web/Mira/issues/new/choose), partage tes idées sur [Discord](https://discord.gg/Bh3F6PDUvB). Chaque page de ce wiki a **Modifier cette page sur GitHub** en bas.
