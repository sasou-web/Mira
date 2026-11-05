---
title: Hors de chez toi
short: Hors de chez toi
description: Joindre ton Jellyfin de partout avec Tailscale, intégré à Mira sous Windows, Mac et iPhone, sans rien ouvrir sur Internet.
platforms: Windows, Mac, iPhone
---

À la maison, Mira joint Jellyfin en direct. Ailleurs, il passe par **Tailscale** : un réseau privé gratuit entre tes propres appareils, chiffré de bout en bout. Jellyfin reste fermé au reste d’Internet.

> [!WARNING]
> N’ouvre pas de port sur ta box pour joindre Jellyfin : il serait exposé à tout Internet. Tailscale fait le même travail sans ce risque.

## Sur le PC de Jellyfin

Le PC où tourne Jellyfin a besoin de la vraie **app Tailscale**, connectée à ton compte Tailscale.

1. Installe Tailscale depuis [tailscale.com/download](https://tailscale.com/download) et connecte-toi. Un compte personnel gratuit suffit.
2. Dans Mira pour Windows, ouvre le guide (<kbd>F1</kbd>) et clique sur **Tout préparer** sur la carte **Mira sur ton iPhone**. Il ouvre Jellyfin à tes appareils Tailscale dans le pare-feu, active **Run unattended** de Tailscale (pour qu’il marche avant qu’une session Windows soit ouverte), et garde le PC éveillé sur secteur.
3. La carte donne ensuite l’adresse Tailscale à taper sur tes appareils. Elle marche aussi à la maison.

Si le PC se met en veille, Tailscale ne peut pas le réveiller. Sur un portable branché, règle la fermeture du capot sur **Ne rien faire**.

## Sur tes autres appareils

Chaque appareil peut soit avoir l’app Tailscale, soit utiliser **Tailscale intégré**, qui est dans Mira. Tailscale intégré fait de Mira un appareil de plus sur ton réseau Tailscale, seulement pendant que Mira est ouvert. Quand l’app Tailscale est connectée sur un appareil, Mira passe par elle et ne propose pas le sien.

### Sous Windows et Mac

1. À l’écran de connexion, tape l’adresse Tailscale du PC de Jellyfin (`100.x.y.z`) ou son nom. Mira propose **Se connecter à Tailscale** ; il le fait aussi quand il ne trouve aucun serveur.
2. Ton navigateur ouvre la page de connexion de Tailscale. Connecte-toi avec le compte du PC de Jellyfin.
3. Si ton réseau Tailscale fait approuver chaque nouvel appareil, approuve `mira-<nom de l’ordinateur>` dans sa console (**Ouvrir la console de Tailscale**).
4. Si le pare-feu demande d’autoriser le Tailscale de Mira, accepte : la connexion directe est plus rapide. Refuser marche aussi, par un relais de Tailscale.
5. Connecte-toi à Jellyfin comme d’habitude.

Mira rejoint ensuite ton réseau Tailscale à chaque ouverture et le quitte à la fermeture. **Réglages → Compte & serveur → Tailscale intégré** montre son état et le chemin que prend Mira : **À la maison (direct)**, **Par l’app Tailscale** ou **Par Tailscale intégré**.

### Sur iPhone et iPad

1. Mira → **Réglages → Ailleurs qu’à la maison** → active **Tailscale intégré**, puis **Se connecter à Tailscale**. Pas encore connecté à Jellyfin : à l’écran de connexion, **Ailleurs : Tailscale intégré**.
2. La page de Tailscale s’ouvre par-dessus Mira : connecte-toi avec le compte du PC de Jellyfin. Mira la referme dès que l’iPhone est connecté. Ton mot de passe Tailscale reste sur la page de Tailscale.
3. Si ton réseau fait approuver chaque nouvel appareil, approuve `mira-iphone` dans la [console](https://login.tailscale.com/admin/machines).
4. Depuis l’écran de connexion, Mira trouve Jellyfin sur ton réseau Tailscale. Déjà connecté : rien d’autre à faire. Quand le Wi-Fi ne mène pas à Jellyfin, Mira passe par Tailscale intégré.

**Réglages → Ailleurs qu’à la maison → Chemin vers Jellyfin** dit par où passe Mira, et si le PC de Jellyfin est en ligne sur Tailscale. Aucun profil VPN n’est nécessaire : ça marche avec AltStore Classic et un identifiant Apple gratuit. Par Tailscale intégré, une vidéo envoyée en AirPlay reste lue par l’iPhone : le téléviseur ne peut pas joindre Jellyfin par lui.

## Que ça continue de marcher

- **Expiration de la clé.** La connexion d’un appareil Tailscale expire par défaut au bout de 180 jours. Mira te prévient deux semaines avant. Dans la [console](https://login.tailscale.com/admin/machines), **⋯ → Disable key expiry** sur l’appareil l’évite, pour le PC de Jellyfin aussi.
- **Se déconnecter.** **Se déconnecter de Tailscale**, dans les réglages de Mira, prévient Tailscale puis efface les clés de Tailscale intégré de l’appareil. L’appareil reste dans ta console, « Logged out », jusqu’à ce que tu l’y retires.

### Quand Tailscale lui-même est en panne

Si les serveurs de coordination de Tailscale sont en panne, les appareils déjà prêts peuvent continuer à se joindre grâce à la liste d’appareils qu’ils gardent. Tailscale ne le permet que si la politique de ton réseau le dit. Mira le propose sous **Tailscale intégré** dans ses réglages, avec **Copier la ligne** ; dans la [politique de ton réseau](https://login.tailscale.com/admin/acls/file), colle-la juste sous `"nodeAttrs": [` (s’il n’y en a pas, ajoute tout le bloc après la première `{`) :

```json
"nodeAttrs": [
  { "target": ["autogroup:member", "autogroup:tagged"], "attr": ["cache-network-maps"] },
],
```

> [!PRIVACY]
> Rien ne part vers Tailscale avant que tu t’y connectes. Ensuite, comme avec l’app Tailscale, ses serveurs voient le compte choisi, le nom de l’appareil, son système et ses adresses IP. Tes vidéos restent chiffrées de bout en bout, en direct ou par un relais. Mira n’envoie aucun journal à Tailscale.

## Un ami sans Tailscale

Pour regarder avec quelqu’un qui n’est pas sur ton réseau Tailscale, envoie-lui ton [lien de party](together.md) : il n’ouvre à Internet que Mira, par Tailscale Funnel, jamais le reste de Jellyfin.
