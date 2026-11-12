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

**Films** et **Séries & animes** ont chacun leur page, avec des pastilles de genres, des filtres (genre, année, progression, bibliothèque, image, note, langue audio, sous-titres) et des tris (ajout, titre, année, note, dernière lecture, plus courts d’abord). **Favoris** est un filtre aussi.

Les filtres s’additionnent : « 4K, pas vus, en VF » donne une seule liste. **Image** : 4K, HD (720p et 1080p) ou SD ; une série compte par ses épisodes. **Note** : la note du public (celle de TMDb, sur 10) au moins. **Langue audio** et les langues de **Sous-titres** demandent Jellyfin 12 ou plus (les versions d’avant ne savent pas filtrer par langue, Mira ne le propose donc pas) ; **Avec sous-titres** et **Sans sous-titres** marchent partout, fichiers à côté de la vidéo compris. Un fichier dont l’audio n’a pas d’étiquette de langue n’est trouvé par aucune langue.

Sur l’affiche d’une série, un nombre dit combien d’épisodes restent à voir : un nouvel épisode d’une série que tu suis se voit dans la bibliothèque.

La fiche d’un titre montre l’image du fichier que tu lirais (« 4K · HDR10 », « 1080p ») et l’heure de fin si tu lançais maintenant. Sur une série, elle montre l’épisode suivant (« Reprendre · S01 E07 »). Dessous : la distribution et l’équipe (un clic montre leurs titres dans ta bibliothèque), les bonus (making-of, scènes coupées ; les bandes-annonces ont [leur bouton](library.md#bandes-annonces)), les titres similaires et les autres films de la même collection.

Les boutons à côté de **Lecture** gardent leur forme et ne changent que d’état : **Favori**, **Vu**, et **Garder sur ce PC** (Windows) ou **Garder sur ce Mac**. Leur nom s’affiche au survol.


## Bandes-annonces

Quand un titre a une bande-annonce, un bouton **Bande-annonce** apparaît sur sa fiche (sur iPhone, dans **…**). Une bande-annonce gardée sur ton serveur se lit dans Mira, comme un film : mets-la dans un dossier `trailers` à côté du film, ou nomme-la `Film (2020)-trailer.mkv`. Sinon, Jellyfin en connaît souvent une sur YouTube (par TMDb) : le bouton le dit (« Bande-annonce sur YouTube, dans ton navigateur ») et l’ouvre dans ton navigateur, jamais dans Mira.

Avec **Réglages → Lecture → Bande-annonce avant le film** (Windows et Mac, coupé par défaut), un film lancé depuis le début passe d’abord sa bande-annonce gardée sur ton serveur, comme au cinéma ; **Passer la bande-annonce** va droit au film. Jamais une bande-annonce du web, jamais en reprise, jamais dans un groupe.
## Marquer comme vu

Un titre compte comme vu quand il se termine, quand tu t’arrêtes après 90 %, ou quand tu passes à l’épisode suivant depuis le générique. Tu peux aussi le marquer à la main, depuis sa fiche ou son menu (clic droit ou appui long). Avec plusieurs saisons, **Marquer la saison comme vue** les marque tous d’un coup.

## Spoilers

Dans **Réglages → Lecture**, **Cacher les spoilers** (coupé par défaut) floute l’image d’un épisode que tu n’as pas commencé et remplace son résumé par « Pas encore vu » : son nom et son numéro restent. Sur les cartes larges (**Continuer à regarder**, **À suivre**), un épisode pas commencé montre l’image de sa saison. Commencé ou vu : tout revient. L’image cachée n’est même pas téléchargée.

## Épisodes manquants et à venir

Avec l’extension TheTVDB sur ton Jellyfin (**Réglages → Entretien → Épisodes manquants et sorties** de Mira pour Windows l’active, pour un administrateur), la page d’une série montre ce que ta bibliothèque n’a pas, chacun à sa place. Les épisodes sortis qui n’y sont pas forment une ligne fine entre ceux que tu as (« É3–4 · pas dans ta bibliothèque · sortis du 25 oct. au 1 nov. ») ; **Voir** les liste. Le prochain épisode à sortir est une ligne vide à sa place, avec son jour. Une saison dont tu n’as aucun épisode est dans la liste des saisons (« Saison 1 · absente »), montrée par une carte qui dit combien d’épisodes elle compte et quand ils sont sortis. Rien à lancer, rien d’urgent : **Réglages → Lecture → Montrer les épisodes manquants** coupé ne garde que ceux à venir.

## Lire sur un autre appareil

Sur iPhone, **…** d’un titre → **Lire sur…** liste les apps Jellyfin qu’on peut piloter (Mira pour Windows ou Mac, une app de télé) : le titre y démarre, puis une télécommande met en pause, avance et règle le volume. Mira pour Windows et Mac apparaissent dans cette liste tout seuls tant qu’ils sont ouverts.

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
