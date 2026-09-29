# Galerie de Mira 0.4.9

Ces **14 captures** sont rendues par l’application WPF réelle à partir de son catalogue fictif intégré. Elles ne contiennent ni compte personnel, ni historique réel, ni médias fournis par Jellyfin. Les illustrations vectorielles appartiennent à la démonstration ; avec un serveur connecté, les affiches et les fonds viennent de ta bibliothèque.

Les captures montrent les vues de navigation et de réglages. Elles ne montrent pas la vidéo native mpv ni les panneaux système Windows. Les comportements de ces composants sont documentés dans [VALIDATION.md](VALIDATION.md).

## Accueil et bibliothèque

![Accueil, bandeau et reprise](screenshots/01-home.png)

*Bandeau panoramique, indicateurs de durée et reprise de lecture.*

![Bibliothèque et filtres](screenshots/02-library.png)

*Recherche, genres, année, progression et tri.*

| Films | Séries |
| --- | --- |
| ![Films](screenshots/03-films.png) | ![Séries](screenshots/04-series.png) |

## Choisir une séance

| Favoris | Fiche d’un film |
| --- | --- |
| ![Favoris](screenshots/05-favorites.png) | ![Fiche](screenshots/06-film-details.png) |

| Survol intégré | Recherche |
| --- | --- |
| ![Survol](screenshots/11-hover.png) | ![Recherche](screenshots/12-search.png) |

## Réglages

![Réglages de lecture](screenshots/07-playback-settings.png)

![Audio et sous-titres](screenshots/08-subtitles-settings.png)

| Apparence | Jellyfin et synchronisation |
| --- | --- |
| ![Apparence](screenshots/09-appearance-settings.png) | ![Serveur](screenshots/10-server-settings.png) |

## Fenêtre compacte et connexion

| Fenêtre 1024 × 720 | Connexion |
| --- | --- |
| ![Fenêtre compacte](screenshots/13-compact-window.png) | ![Connexion](screenshots/14-login.png) |

Les captures ordinaires utilisent une fenêtre de 1440 × 960 unités WPF. L’export suit le DPI de Windows : la résolution PNG peut être supérieure à ces dimensions logiques.

Pour les reproduire sur Windows avec le SDK .NET 8 :

```powershell
./tools/capture-gallery.ps1
```

Le script crée un nouveau profil sous `.artifacts`, utilise `--demo` et ne contacte pas de serveur. Il copie seulement les captures numérotées dans `docs/screenshots`.
