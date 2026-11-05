---
title: Audio et sous-titres
short: Audio et sous-titres
description: Tes langues retenues série par série, le style des sous-titres, tes propres fichiers, et les sous-titres trouvés et recalés par le serveur.
platforms: Windows, Mac, iPhone
---

## Tes langues, série par série

Dans **Réglages → Audio & sous-titres**, liste tes langues audio et tes langues de sous-titres préférées, dans l’ordre. Mira choisit parmi les pistes de chaque fichier.

Change l’audio ou les sous-titres d’un épisode, et Mira s’en souvient pour toute la série : les épisodes suivants reçoivent le même choix, reconnu par la langue et le nom de la piste (« Full » plutôt que « Signs », sous-titres coupés…). Un épisode sans piste semblable revient à tes langues préférées. Ces choix suivent ton compte sur tes autres appareils.

La même page règle la taille et le style des sous-titres (fond sombre, blanc ou jaune, gras, hauteur à l’écran), avec un aperçu.

## Dans le lecteur

Le menu **audio et sous-titres** montre les deux listes côte à côte, la piste en cours cochée, avec sa langue, son codec et ses canaux.

- **Charger un fichier…** lit un fichier de sous-titres à toi (`.srt`, `.ass`, `.vtt`). Ensuite, **Garder sur Jellyfin** l’envoie à Jellyfin, pour que tes autres appareils l’aient aussi.
- **Chercher des sous-titres…** apparaît quand Jellyfin a une extension de sous-titres, comme Open Subtitles ou les sous-titres automatiques de Mira. Mira dit d’abord ce qui sera envoyé au service.
- **Décalage des sous-titres** (menu ⋮) les décale s’ils arrivent trop tôt ou trop tard, jusqu’à la fermeture du lecteur. Sur iPhone, il est dans le menu des sous-titres, par pas de 0,5 s.

## Sous-titres automatiques

L’extension de Mira sur Jellyfin peut trouver seule des sous-titres pour chaque nouveau film ou épisode, dans les langues que tu choisis, vérifier qu’ils vont avec la vidéo et les recaler dessus. Sous Windows et Mac, connecté avec le compte administrateur de Jellyfin : **Réglages → Entretien → Sous-titres automatiques**.

1. Choisis tes **Langues**, et si les sous-titres pour sourds et malentendants (SDH) sont préférés, indifférents ou évités.
2. Choisis les sources : **OpenSubtitles** marche sans compte (un compte ne fait que relever le quota du jour) ; **SubDL** demande une clé API gratuite à toi.
3. **Essayer les sources**, puis active **Chercher tout seul**.

Quand un titre arrive, l’extension de Mira cherche, puis réessaie de temps en temps pendant un mois si rien n’est assez sûr. Elle ne cherche jamais pendant une lecture. Elle ne garde un candidat que si elle en est sûre : un fichier dont l’empreinte correspond au tien, ou une release du même groupe et de la même source. Avant de le garder, elle compare sa durée à celle de la vidéo, et **Recaler sur la vidéo** l’aligne sur la piste texte du fichier lui-même ou sur ses voix. **Journal** montre ce qui a été gardé, écarté, et pourquoi.

> [!PRIVACY]
> Les sources reçoivent l’empreinte et la taille du fichier (OpenSubtitles), les identifiants IMDb ou TMDb du titre, la saison et le numéro d’épisode, le titre, l’année et la langue. Jamais le nom du fichier, jamais rien sur toi. Tout se passe sur ton serveur Jellyfin : tes appareils n’envoient rien.

**Chercher aussi pour ce qui est déjà là** passe toute ta bibliothèque une fois par langue. Ça consomme ton quota du jour, donc ça peut prendre plusieurs jours.
