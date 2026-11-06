---
title: Ta bibliothèque
short: Ta bibliothèque
description: L’accueil, les films et les séries, la recherche, les favoris, les collections, et les titres gardés sur ton ordinateur pour les regarder sans connexion.
platforms: Windows, Mac, iPhone
---

## L’accueil

L’accueil s’ouvre sur un grand bandeau de tes titres, puis **Continuer à regarder** : les films et les séries commencés, le plus récent d’abord, une carte par série, avec le temps qui reste. Dessous viennent les épisodes suivants, les titres ajoutés récemment et les rangées que tu as choisies.

- **Rangées de l’accueil** (Réglages → Apparence) permet de les remettre dans un autre ordre ou de les masquer, dont une rangée « Nouveau dans… » par bibliothèque. La disposition est gardée par appareil.
- Clic droit sur une carte de Continuer à regarder (appui long sur iPhone), puis **Retirer de Continuer à regarder** pour l’oublier jusqu’à la prochaine lecture. **Annuler** la remet.

## Films et séries

**Films** et **Séries & animes** ont chacun leur page, avec des pastilles de genres, des filtres (genre, année, progression, bibliothèque) et des tris (ajout, titre, année, note, dernière lecture). **Favoris** est un filtre aussi.

Sur l’affiche d’une série, un nombre dit combien d’épisodes restent à voir : un nouvel épisode d’une série que tu suis se voit dans la bibliothèque.

La fiche d’un titre montre l’image du fichier que tu lirais (« 4K · HDR10 », « 1080p ») et l’heure de fin si tu lançais maintenant. Sur une série, elle montre l’épisode suivant (« Reprendre · S01 E07 »). Dessous : la distribution et l’équipe (un clic montre leurs titres dans ta bibliothèque), les bonus (making-of, scènes coupées ; les bandes-annonces ont [leur bouton](library.md#bandes-annonces)), les titres similaires et les autres films de la même collection.

Les boutons à côté de **Lecture** gardent leur forme et ne changent que d’état : **Favori**, **Vu**, et **Garder sur ce PC** (Windows) ou **Garder sur ce Mac**. Leur nom s’affiche au survol.


## Bandes-annonces

Quand un titre a une bande-annonce, un bouton **Bande-annonce** apparaît sur sa fiche (sur iPhone, dans **…**). Une bande-annonce gardée sur ton serveur se lit dans Mira, comme un film : mets-la dans un dossier `trailers` à côté du film, ou nomme-la `Film (2020)-trailer.mkv`. Sinon, Jellyfin en connaît souvent une sur YouTube (par TMDb) : le bouton le dit (« Bande-annonce sur YouTube, dans ton navigateur ») et l’ouvre dans ton navigateur, jamais dans Mira.

Avec **Réglages → Lecture → Bande-annonce avant le film** (Windows et Mac, coupé par défaut), un film lancé depuis le début passe d’abord sa bande-annonce gardée sur ton serveur, comme au cinéma ; **Passer la bande-annonce** va droit au film. Jamais une bande-annonce du web, jamais en reprise, jamais dans un groupe.
## Marquer comme vu

Un titre compte comme vu quand il se termine, quand tu t’arrêtes après 90 %, ou quand tu passes à l’épisode suivant depuis le générique. Tu peux aussi le marquer à la main, depuis sa fiche ou son menu (clic droit ou appui long). Avec plusieurs saisons, **Marquer la saison comme vue** les marque tous d’un coup.

## Rechercher

<kbd>Ctrl</kbd> + <kbd>K</kbd> ou <kbd>Ctrl</kbd> + <kbd>F</kbd> sous Windows, <kbd>⌘</kbd> + <kbd>F</kbd> sur Mac. Dès deux lettres, Mira montre les personnes (acteurs, réalisateurs) dont le nom correspond, puis les titres, les épisodes et les collections.

## Collections

Sous Windows et Mac, le menu d’un film ou d’une série propose **Ajouter à une collection…** : coche les collections où il va, ou tape un nom pour en créer une (« Vendredis soir », « À montrer à Léa »). Une collection se renomme et se supprime depuis sa page ; ses titres restent dans ta bibliothèque et aucun fichier ne bouge. L’accueil a une rangée **Collections**.

Il faut le compte administrateur de Jellyfin, ou un compte que Jellyfin autorise à gérer les collections.

> [!TIP]
> **Réglages → Entretien → Ranger mes films en sagas** (administrateur) demande à Jellyfin de regrouper tout seul les films d’une même saga, d’après TMDb.

## Regarder sans connexion

Sous Windows et Mac, **Garder sur ce PC** (ou **Garder sur ce Mac**) sur la fiche d’un titre télécharge le fichier d’origine depuis Jellyfin, avec ses sous-titres, pour le regarder dans un train ou un avion. Les titres gardés sont dans **Sur ce PC**, dans la barre de gauche. Mira les lit avant de demander à Jellyfin, et l’épisode suivant s’enchaîne si tu l’as gardé aussi. Ton compte Jellyfin doit avoir le droit de télécharger.

## Clavier (Windows)

| Action | Touches |
| --- | --- |
| Rechercher | <kbd>Ctrl</kbd> + <kbd>K</kbd> ou <kbd>Ctrl</kbd> + <kbd>F</kbd> |
| Accueil, Films, Séries, Toute la bibliothèque | <kbd>Ctrl</kbd> + <kbd>1</kbd> à <kbd>Ctrl</kbd> + <kbd>4</kbd> |
| D’une affiche à sa voisine | flèches |
| Le menu d’une affiche | <kbd>Maj</kbd> + <kbd>F10</kbd>, ou clic droit |
| Revenir | <kbd>Échap</kbd>, <kbd>Alt</kbd> + <kbd>←</kbd>, ou le bouton retour de la souris |
| Guide de démarrage | <kbd>F1</kbd> |
