---
title: Regarder ensemble
short: Regarder ensemble
description: Regarder le même film à la même seconde que tes amis, chacun chez soi, avec ton lien de party, même sans compte ni Tailscale.
platforms: Windows, Mac, iPhone
---

Regarder ensemble garde plusieurs appareils sur le même film, à la même seconde : quand quelqu’un met en pause, avance ou passe à l’épisode suivant, tout le monde suit. Il s’appuie sur SyncPlay, celui de Jellyfin, donc il marche entre Mira pour Windows, Mac et iPhone.

## Avec vos propres comptes

Pour ceux qui ont déjà un compte sur ton Jellyfin (ta maison, un ami que tu as ajouté) :

1. Sur la fiche d’un titre, **Regarder ensemble → Créer un groupe et inviter…**.
2. Les autres voient le groupe sur leur accueil, sous le bandeau, et **Rejoindre**. Ou envoie-leur le lien que donne Mira : ouvert là où ils ont Mira, il les fait entrer dans le groupe. Sans l’extension de Mira sur ton Jellyfin, c’est un lien `mira://`, qui ne s’ouvre que sur un appareil qui a Mira ; avec elle, c’est le lien de ta party.
3. **Lecture** devient **Lancer pour le groupe** : il démarre pour tout le monde, depuis ta progression. Choisis un autre titre pendant que tu es dans le groupe et Mira demande avant de remplacer celui du groupe.

Dans un groupe, le lecteur dit ce que fait le groupe (« On attend quelqu’un du groupe… », « En pause pour le groupe »), et une icône montre combien vous êtes. **Quitter le groupe** quand c’est fini.

## Ton lien de party

Ta party est un lien, toujours le même, qui amène un ami droit dans ton groupe, **même sans compte sur ton Jellyfin et sans Tailscale**. Il est dans **Réglages → Amis & invités → Ta party** : **Copier**.

Envoie-le sur Discord, dans Messages ou ailleurs : il montre une carte avec qui invite et ce que vous regardez. Ton ami :

1. Ouvre le lien et touche **Ouvrir dans Mira** (Mira pour Windows, Mac ou iPhone ; sinon la page dit où le prendre).
2. Tape un prénom, celui que voit le groupe. Aucun compte à créer.
3. Est dans ton groupe, et suit.

Chaque invité a son propre appareil. Dans **Invités**, sous ton lien, **Retirer** en enlève un tout de suite. **Faire un nouveau lien** arrête l’ancien lien et tous ses invités.

Si ton ami a un compte sur ton Jellyfin, le même lien le fait entrer dans le groupe avec son propre compte. Et si le lien vient d’**un autre** Jellyfin (la party d’un ami sur son propre serveur), Mira la rejoint en invité, puis te rend ton propre compte quand tu fais **Quitter la party**.

### Comment un lien marche de partout

Le lien passe par **Tailscale Funnel**, qui n’ouvre à Internet que la page de Mira et sa passerelle d’invités, jamais le reste de Jellyfin : ni tableau de bord, ni page de connexion, ni aucune autre route. Sur le PC de Jellyfin, Mira pour Windows ouvre Funnel tout seul. La toute première fois, Tailscale te demande d’autoriser Funnel sur ton compte : **Autoriser Funnel sur Tailscale** ouvre cette page, une fois.

Sans Funnel, le lien marche quand même à la maison, et pour les amis sur ton réseau Tailscale.

> [!PRIVACY]
> Funnel ouvert, le nom de ton PC de Jellyfin sur Tailscale devient public : son certificat HTTPS est publié dans les journaux de transparence des certificats, comme toute adresse Funnel. Le trafic reste chiffré jusqu’à ton PC. Chaque invité reçoit un compte Jellyfin caché qui ne peut que regarder avec ton groupe, supprimé quand tu le retires, et qui expire après 7 jours sans servir.

## Bon à savoir

- Prends la dernière Mira sur chaque appareil. Un groupe entre vos propres comptes marche sur n’importe quel Jellyfin ; le lien de party et ses invités demandent l’extension de Mira sur le Jellyfin de l’hôte (voir [Préparer Jellyfin](jellyfin.md#preparer-le-serveur)).
- Chaque appareil lit depuis Jellyfin de son côté. Un ami loin doit avoir une connexion assez rapide pour le fichier, ou baisser la **Qualité** dans son lecteur (voir [Le lecteur](player.md#loin-de-la-maison-la-qualite)).
- Quelqu’un qui charge plus lentement retient le groupe un instant : Mira dit « Chargement pour le groupe… » plutôt que de le laisser derrière.
