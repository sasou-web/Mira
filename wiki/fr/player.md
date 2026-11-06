---
title: Le lecteur
short: Le lecteur
description: Raccourcis, intros passées, image dans l’image, minuterie, réglages de l’image et du son, et lecture loin de la maison.
platforms: Windows, Mac, iPhone
---

Sous Windows et Mac, Mira lit avec **mpv**, dans sa propre fenêtre : presque tous les fichiers se lisent tels quels, avec leurs pistes audio, leurs sous-titres et leurs chapitres. Sur iPhone, Mira a son propre lecteur, construit sur celui d’Apple.

## Les commandes

La barre montre le titre et l’épisode à gauche, le chapitre et le temps à droite. Dessous : lecture/pause, épisode suivant et volume à gauche ; **⋮** (vitesse, minuterie, décalage des sous-titres, informations), audio et sous-titres, mini-lecteur, image dans l’image et plein écran à droite. Les commandes se cachent après quelques secondes sans bouger la souris.

- La barre de lecture est découpée en chapitres ; le survol montre le temps, le chapitre et, quand Jellyfin les a préparés, un aperçu de l’image.
- Le volume monte jusqu’à 200 % pour un titre enregistré trop bas : au-delà de 100 %, le son est amplifié puis limité, sans saturer.
- Un clic sur l’image met en pause ou reprend, un double clic passe en plein écran. Un clic près des barres ne fait qu’afficher les commandes, jamais de pause par erreur.

### Clavier

| Action | Windows | Mac |
| --- | --- | --- |
| Lecture / pause | <kbd>Espace</kbd> ou <kbd>K</kbd> | <kbd>Espace</kbd> ou <kbd>K</kbd> |
| Reculer / avancer de 10 secondes | <kbd>←</kbd> / <kbd>→</kbd>, <kbd>J</kbd> / <kbd>L</kbd> | <kbd>←</kbd> / <kbd>→</kbd> |
| Reculer / avancer d’une minute | <kbd>Maj</kbd> + <kbd>←</kbd> / <kbd>→</kbd> | |
| Aller à 0 %, 10 %… 90 % | <kbd>0</kbd> à <kbd>9</kbd> | |
| Volume | <kbd>↑</kbd> / <kbd>↓</kbd>, molette | <kbd>↑</kbd> / <kbd>↓</kbd> |
| Couper le son | <kbd>M</kbd> | <kbd>M</kbd> |
| Sous-titres coupés / remis | <kbd>C</kbd> | |
| Plein écran | <kbd>F</kbd>, <kbd>F11</kbd>, double clic | <kbd>F</kbd>, double clic |
| Mini-lecteur | <kbd>I</kbd> | |
| Passer l’intro, le récap ou le générique | <kbd>S</kbd> | <kbd>S</kbd> |
| Épisode suivant | <kbd>N</kbd> | <kbd>N</kbd> |
| Revenir, quitter le plein écran | <kbd>Échap</kbd> | <kbd>Échap</kbd> |

### Sur iPhone

Touche l’image pour afficher les commandes. Double toucher sur un côté pour reculer ou avancer de 10 secondes (les touchers s’additionnent). Glisse vers le haut ou le bas à gauche pour la luminosité, à droite pour le volume, en travers pour te déplacer. Appui long pour lire en ×2, pincer pour remplir l’écran, glisser vers le bas pour réduire le lecteur. Le verrou écarte les touchers involontaires.

## Intros, génériques et épisode suivant

Les boutons **Passer l’intro**, **Passer le générique** et **Passer le récap** viennent des segments de Jellyfin (Jellyfin 10.10 ou plus récent, du serveur ou d’une extension comme Intro Skipper) ou des noms de chapitres du fichier. Avec **Réglages → Lecture → Passer les intros et les récaps**, Mira les passe sans demander, une fois chacun : revenir dedans les rejoue.

**Épisode suivant** apparaît pendant le générique de fin ou dans les 20 dernières secondes. Avec **Enchaîner les épisodes**, un compte à rebours de 10 secondes démarre ; **Annuler** reste sur cet épisode.

Après 3 épisodes de suite sans que rien ne soit touché (clavier, souris, trackpad, manette, ou l’écran sur iPhone), Mira ne lance pas le suivant : il s’arrête à la fin de l’épisode et demande **Tu regardes toujours ?**, pour qu’une nuit endormi devant une série ne marque pas dix épisodes comme vus. **Continuer** lance le suivant. Jamais dans une party. Pour l’éteindre : **Réglages → Lecture → Demander si tu regardes toujours**.

Mira reprend un peu avant là où tu t’es arrêté, 5 secondes par défaut, pour retrouver le fil (**Réglages → Lecture**). **Depuis le début**, sur la fiche d’un titre, repart du début.

## Mini-lecteur et image dans l’image

- **Mini-lecteur** : la vidéo continue dans un coin de la bibliothèque pendant que tu navigues. Fais-le glisser dans n’importe quel coin ; sous Windows, pousse-le au-delà du bord droit pour le ranger derrière un onglet.
- **Image dans l’image** (Windows et Mac) : la vidéo dans une petite fenêtre à elle, au-dessus de tes autres fenêtres, avec ses sous-titres. Sur iPhone, l’Image dans l’image d’Apple.

## Minuterie

Dans le menu **⋮** du lecteur : pause dans 15 minutes, 30 minutes, 1 heure, ou à la fin de l’épisode. Sur iPhone, elle est sur la fiche du titre. Elle s’efface à la fermeture du lecteur.

## Image et son

- **Image** (Réglages → Lecture, Windows et Mac) : **Standard** (le rendu de mpv, le plus léger), **Cinéma** (par défaut : mouvement fluide sur tout écran, pas de bandes dans les dégradés) et **Maximale** (avec la mise à l’échelle la plus fine). Si l’ordinateur perd des images, Mira descend d’un niveau tout seul et te le dit.
- **Sortie audio** (Windows, Réglages → Audio & sous-titres) : le périphérique, le nombre de canaux, et **Son envoyé à l’ampli tel quel** pour un ampli ou une barre de son qui décode le Dolby et le DTS, Atmos compris. **Tester la sortie audio** joue un son sur chaque haut-parleur.
- **Décalage de l’audio** (menu ⋮) : si les lèvres et les voix ne collent pas, par pas de 0,1 s, jusqu’à la fermeture du lecteur.

## Loin de la maison : la qualité

À la maison, Mira lit le fichier tel quel. Ailleurs, un film en 4K peut ne pas passer par ta connexion : **Qualité** (Réglages → Lecture et menu **⋮** du lecteur) fixe une limite, ou **Auto** mesure la connexion jusqu’à Jellyfin. Sous la limite, Jellyfin convertit le fichier pendant que tu regardes. Si la connexion tombe, Mira attend Jellyfin, rouvre le flux une fois là où il en était, et seulement ensuite dit qu’il ne peut pas lire.

Convertir est lourd pour le serveur. Sur le PC de Jellyfin, Mira peut le confier à la carte graphique : **Réglages → Entretien → Conversions par la carte graphique**, essayé d’abord sur un de tes titres.
