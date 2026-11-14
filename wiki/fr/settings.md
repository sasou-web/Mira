---
title: Où trouver quoi
short: Réglages
description: La carte des réglages de Mira sous Windows, Mac et iPhone, et à quoi sert chaque partie.
platforms: Windows, Mac, iPhone
---

Chaque changement s’applique et s’enregistre tout de suite : il n’y a pas de bouton Enregistrer.

## Windows et Mac

Les réglages s’ouvrent par la roue dentée de la barre de gauche (<kbd>⌘</kbd> + <kbd>,</kbd> sur Mac), sur **L’essentiel** : la version de Mira et où en est sa mise à jour (avec **Redémarrer et mettre à jour** quand une nouvelle version est prête), ton compte et si Jellyfin répond, et les six réglages qu’on change le plus (qualité, langues de l’audio et des sous-titres, style des sous-titres, épisode suivant, passer les intros, langue de Mira). Ce sont les mêmes réglages que dans leur partie : changés ici, changés là-bas. Un point sur la roue et sur **Mises à jour** dit qu’une nouvelle version est prête. La recherche (<kbd>Ctrl</kbd> + <kbd>F</kbd>, <kbd>⌘</kbd> + <kbd>F</kbd>) trouve n’importe quel réglage, par son nom ou un mot courant (« mise à jour », « flou », « loin de chez moi »).

| Partie | Ce qu’on y trouve |
| --- | --- |
| **Lecture** | Reprendre là où tu t’es arrêté, enchaîner les épisodes, « Tu regardes toujours ? », passer les intros et les récaps, bande-annonce avant le film, qualité, et dans les réglages avancés l’image et le décodage matériel |
| **Audio & sous-titres** | Langues audio et de sous-titres préférées, taille et style des sous-titres, sortie audio et ampli |
| **Accueil & bibliothèque** | Rangées de l’accueil, carrousel automatique, taille des affiches (Windows), cacher les spoilers, montrer les épisodes manquants, les titres gardés pour regarder sans connexion |
| **Général** | Langue de Mira, mode canapé, réduire les animations (Windows), notifications de nouveaux épisodes |
| **Compte & serveur** | Ton compte Jellyfin et ta photo, se déconnecter ou changer de compte, Tailscale intégré, et **Réessayer** quand Jellyfin ne répond pas |
| **Confidentialité** | Tout ce que Mira envoie, à qui et quand, avec l’interrupteur de chaque connexion qui en a un |
| **Mises à jour** | La version de Mira, ses mises à jour, et sous Windows le moteur mpv |

**Ton bilan** (ce que tu as regardé ce mois-ci et cette année, d’après Jellyfin) s’ouvre depuis la carte du compte de **L’essentiel**.

Sous **Ton serveur**, pour l’administrateur de Jellyfin :

| Partie | Ce qu’on y trouve |
| --- | --- |
| **Amis & invités** | Ajouter un ami (un compte à lui), ton lien de party et ses invités, autoriser un appareil par son code |
| **Soin de la bibliothèque** | Titres à corriger, sous-titres automatiques, épisodes manquants, sagas de films, aperçus de la barre de lecture, lectures impossibles signalées (Windows) |
| **Serveur Jellyfin** | Qui regarde sur Jellyfin en ce moment, et sur le PC de Jellyfin : sauvegarde et mise à jour de Jellyfin, conversions par la carte graphique (Windows) ; l’extension de Mira à installer quand Jellyfin ne l’a pas (Mac) |

L’aide (**?** dans la barre, <kbd>F1</kbd> sous Windows) est à part : où vont tes vidéos, la carte **Mira sur ton iPhone** qui prépare le serveur, et **Signaler un problème** (un rapport à exporter).

## iPhone et iPad

L’onglet **Réglages** contient :

- **Compte** : ton serveur, ton compte, ta photo, **Se déconnecter**.
- **Lecture** : langues, intros et récaps, épisode suivant, reprendre un peu avant, cacher les spoilers, montrer les épisodes manquants et **Données mobiles** (plafond de qualité en 4G, 2 Mb/s par défaut).
- **Ailleurs qu’à la maison** : Tailscale intégré et le chemin que prend Mira vers Jellyfin.
- **Administration** (administrateur de Jellyfin) : qui regarde sur Jellyfin en ce moment.
- **Cet iPhone → Confidentialité** : tout ce que Mira envoie, à qui et quand, et l’interrupteur de Tailscale intégré.
- **À propos** : la version et le code source.

## Langue

Mira parle anglais et français. Par défaut, il suit la langue choisie à l’installation, sinon celle du système. Change-la dans **Réglages → Général → Langue de Mira** : elle s’applique à la prochaine ouverture de Mira. Les langues audio et de sous-titres par défaut la suivent.

## Mode canapé

Pour un PC ou un Mac branché à la télé, qu’on regarde depuis le canapé (Windows et Mac). Allume-le dans **Réglages → Général → Mode canapé** : il s’applique tout de suite, et reste allumé aux prochaines ouvertures de Mira.

- Mira prend tout l’écran, et chaque page est dessinée en plus grand, comme sur une fenêtre de 1280 × 720 : lisible depuis l’autre bout de la pièce. Le lecteur garde sa taille.
- Un anneau blanc montre ce qui est choisi. Les flèches vont d’un élément au suivant n’importe où sur l’écran, rail compris ; <kbd>Entrée</kbd> ouvre, <kbd>Échap</kbd> ou <kbd>Retour arrière</kbd> revient.
- Dans le lecteur, <kbd>Échap</kbd> ou <kbd>Retour arrière</kbd> arrête le film et revient à sa fiche, comme à la télé.
- Une manette (Xbox, ou une manette qui se présente comme elle ; PlayStation et autres manettes appairées à un Mac) : **A** ouvre, **B** revient, **Y** cherche, **LB** et **RB** changent de page. Dans le lecteur, **A** met en pause, **X** active ou coupe les sous-titres, **Y** passe l’intro, **LB** et **RB** sautent d’une minute. Branche une manette mode canapé éteint et Mira propose de l’allumer.
- Une télécommande de télé, par le HDMI-CEC de la télé avec un adaptateur qui tape des touches : ses flèches, OK et retour marchent comme celles du clavier, et ses touches lecture et pause arrivent à Mira comme les touches multimédias d’un clavier.

> [!NOTE]
> Le mode canapé ne lit la manette que quand Mira est au premier plan, et n’envoie rien nulle part.

Pour en sortir, de nouveau **Réglages → Général → Mode canapé**.
