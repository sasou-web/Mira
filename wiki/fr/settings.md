---
title: Où trouver quoi
short: Réglages
description: La carte des réglages de Mira sous Windows, Mac et iPhone, et à quoi sert chaque partie.
platforms: Windows, Mac, iPhone
---

Chaque changement s’applique et s’enregistre tout de suite : il n’y a pas de bouton Enregistrer.

## Windows et Mac

Les réglages s’ouvrent par la roue dentée de la barre de gauche (<kbd>⌘</kbd> + <kbd>,</kbd> sur Mac).

| Partie | Ce qu’on y trouve |
| --- | --- |
| **Lecture** | Passer les intros et les récaps, enchaîner les épisodes, bande-annonce avant le film, cacher les spoilers, « Tu regardes toujours ? », reprendre là où tu t’es arrêté, image, qualité hors de chez toi, décodage matériel, le moteur mpv (Windows) |
| **Audio & sous-titres** | Langues audio et de sous-titres préférées, taille et style des sous-titres, sortie audio et ampli |
| **Apparence** | Langue de Mira, mode canapé, rangées de l’accueil, densité de la bibliothèque, carrousel automatique, réduire les animations |
| **Compte & serveur** | Ton compte Jellyfin et ta photo, se déconnecter, Tailscale intégré, notifications de nouveaux épisodes, qui regarde sur Jellyfin en ce moment |
| **Mira sur ce PC / Mac** | Les mises à jour, et les titres gardés pour regarder sans connexion |
| **Confidentialité** | Tout ce que Mira envoie, à qui et quand, avec l’interrupteur de chaque connexion qui en a un |
| **Ton bilan** | Ce que tu as regardé ce mois-ci et cette année, d’après Jellyfin |

Sous **Ton serveur**, pour l’administrateur de Jellyfin :

| Partie | Ce qu’on y trouve |
| --- | --- |
| **Amis & invités** | Ajouter un ami (un compte à lui), ton lien de party et ses invités, autoriser un appareil par son code |
| **Entretien** | Titres à corriger, sous-titres automatiques, épisodes manquants, sagas de films, aperçus de la barre de lecture, et sur le PC de Jellyfin : sauvegarde et mise à jour de Jellyfin, conversions par la carte graphique |

Le guide (**?** dans la barre, <kbd>F1</kbd> sous Windows) est à part : où vont tes vidéos, et la carte **Mira sur ton iPhone** qui prépare le serveur.

## iPhone et iPad

L’onglet **Réglages** contient :

- **Compte** : ton serveur, ton compte, ta photo, **Se déconnecter**.
- **Lecture** : langues, intros et récaps, épisode suivant, reprendre un peu avant, cacher les spoilers et **Données mobiles** (plafond de qualité en 4G, 2 Mb/s par défaut).
- **Ailleurs qu’à la maison** : Tailscale intégré et le chemin que prend Mira vers Jellyfin.
- **Administration** (administrateur de Jellyfin) : qui regarde sur Jellyfin en ce moment.
- **Cet iPhone → Confidentialité** : tout ce que Mira envoie, à qui et quand, et l’interrupteur de Tailscale intégré.
- **À propos** : la version et le code source.

## Langue

Mira parle anglais et français. Par défaut, il suit la langue choisie à l’installation, sinon celle du système. Change-la dans **Réglages → Apparence → Langue de Mira** : elle s’applique à la prochaine ouverture de Mira. Les langues audio et de sous-titres par défaut la suivent.

## Mode canapé

Pour un PC ou un Mac branché à la télé, qu’on regarde depuis le canapé (Windows et Mac). Allume-le dans **Réglages → Apparence → Mode canapé** : il s’applique tout de suite, et reste allumé aux prochaines ouvertures de Mira.

- Mira prend tout l’écran, et chaque page est dessinée en plus grand, comme sur une fenêtre de 1280 × 720 : lisible depuis l’autre bout de la pièce. Le lecteur garde sa taille.
- Un anneau blanc montre ce qui est choisi. Les flèches vont d’un élément au suivant n’importe où sur l’écran, rail compris ; <kbd>Entrée</kbd> ouvre, <kbd>Échap</kbd> ou <kbd>Retour arrière</kbd> revient.
- Dans le lecteur, <kbd>Échap</kbd> ou <kbd>Retour arrière</kbd> arrête le film et revient à sa fiche, comme à la télé.
- Une manette (Xbox, ou une manette qui se présente comme elle ; PlayStation et autres manettes appairées à un Mac) : **A** ouvre, **B** revient, **Y** cherche, **LB** et **RB** changent de page. Dans le lecteur, **A** met en pause, **X** active ou coupe les sous-titres, **Y** passe l’intro, **LB** et **RB** sautent d’une minute. Branche une manette mode canapé éteint et Mira propose de l’allumer.
- Une télécommande de télé, par le HDMI-CEC de la télé avec un adaptateur qui tape des touches : ses flèches, OK et retour marchent comme celles du clavier, et ses touches lecture et pause arrivent à Mira comme les touches multimédias d’un clavier.

> [!NOTE]
> Le mode canapé ne lit la manette que quand Mira est au premier plan, et n’envoie rien nulle part.

Pour en sortir, de nouveau **Réglages → Apparence → Mode canapé**.
