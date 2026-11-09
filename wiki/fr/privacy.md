---
title: Vie privée
short: Vie privée
description: Ce que Mira envoie, à qui et quand. Pas de compte, pas de statistiques, pas de télémétrie.
platforms: Windows, Mac, iPhone
---

Mira n’a ni compte, ni pub, ni statistiques, ni télémétrie. Il parle à ton serveur Jellyfin, et à quelques services seulement quand une fonction en a besoin, chacun listé ci-dessous. Le code est ouvert : [vois par toi-même](https://github.com/sasou-web/Mira).

> [!TIP]
> La même liste est dans les apps : **Réglages → Confidentialité** sur Windows, Mac et iPhone, chaque connexion avec ce qu’elle envoie et son interrupteur quand elle en a un (la recherche de mises à jour, Tailscale intégré, et sous Windows la recherche d’un Jellyfin plus récent).

## Toujours

| Qui | Vers | Quoi |
| --- | --- | --- |
| Mira, sur chaque appareil | **ton serveur Jellyfin** | ta bibliothèque, la lecture et la progression, avec le jeton de ta session dans un en-tête, jamais dans une adresse (sauf le flux du lecteur de l’iPhone, que le lecteur d’Apple ne sait pas envoyer autrement) |
| Mira pour Windows et Mac | **GitHub** (`api.github.com`) | la recherche de mises à jour, au démarrage puis toutes les 6 heures : la requête elle-même (ton adresse IP, la version de Mira), rien d’autre. Coupe-la dans **Réglages → Mira sur ce PC → Mettre à jour automatiquement** |

Avant ta connexion, Mira cherche Jellyfin sur cet ordinateur, sur le réseau de la maison (la question de découverte de Jellyfin, jamais au-delà de ton réseau) et sur tes appareils Tailscale. Rien d’identifiant n’est envoyé ; ton mot de passe ne va qu’à l’adresse où tu te connectes.

## Seulement quand tu utilises une fonction

| Fonction | Contacte | Quoi |
| --- | --- | --- |
| **Tailscale intégré**, une fois connecté | serveurs de coordination et relais de Tailscale ; ton routeur (UPnP, NAT-PMP, PCP), à qui il demande d’ouvrir un port pour une connexion directe plutôt que par un relais | comme l’app Tailscale : le compte choisi, le nom de l’appareil, son système et ses adresses IP, sa clé publique. Les vidéos restent chiffrées de bout en bout. Aucun journal envoyé |
| **Le moteur mpv** (Windows) | SourceForge | le téléchargement du moteur, SHA-256 vérifié |
| **Installer ou mettre à jour Jellyfin** (Windows) | `repo.jellyfin.org`, GitHub (catalogue de winget, une fois par jour sur le PC de Jellyfin, coupé dans **Réglages → Confidentialité**) | le téléchargement, et s’il existe une version plus récente |
| **Sous-titres automatiques** | OpenSubtitles, SubDL, depuis ton serveur | l’empreinte et la taille du fichier, les identifiants IMDb/TMDb, saison, épisode, titre, année, langue. Jamais le nom du fichier |
| **Chercher des sous-titres…** avec une autre extension de Jellyfin | le service de cette extension, depuis ton serveur | ce que l’extension envoie ; Mira te le dit avant |
| **Ton lien de party** par Tailscale Funnel | relais de Tailscale, puis tes invités | le trafic de tes invités vers la page de Mira, chiffré jusqu’à ton PC. Le nom Tailscale de ton PC devient public |
| **Une bande-annonce du web**, à ton clic | le site de la bande-annonce (souvent YouTube), dans ton navigateur | ce qu’envoie ton navigateur (ton adresse IP, les cookies du site). Mira n’en charge rien avant ton clic et n’a aucune vue web ; une bande-annonce gardée sur ton serveur ne passe que par lui |
| **AltStore** (iPhone) | Apple, `miracinema.app`, GitHub | ton identifiant Apple à Apple seulement, pour le certificat gratuit ; la source et l’app de Mira |

## Sur tes appareils

- Ton mot de passe Jellyfin n’est jamais gardé : seulement la session. Windows la protège pour ton compte ; Mac et iPhone la gardent dans le trousseau (sur iPhone, cet appareil seulement, jamais dans les sauvegardes).
- Les clés et l’état de Tailscale intégré sont chiffrés sur l’appareil. Son journal y reste et ne contient aucune page de connexion.
- Rien sur toi ni sur ta bibliothèque n’est jamais collecté.

Tous les détails, requête par requête : [SECURITY.md](https://github.com/sasou-web/Mira/blob/main/SECURITY.md).

## Signaler un problème

Tu as trouvé une faille ? Passe par **Security → Report a vulnerability** sur [le dépôt GitHub de Mira](https://github.com/sasou-web/Mira/security), jamais par une issue publique. Ne publie jamais un jeton, un fichier de session ou un mot de passe où que ce soit.
