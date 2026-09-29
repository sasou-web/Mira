# Validation — 29 septembre 2026

Les chemins `.artifacts/...` cités ci-dessous désignent les preuves de validation locales, exclues du dépôt. La galerie publique utilise uniquement le mode démonstration. Un récapitulatif sans données personnelles est conservé dans [testing/latest-results.txt](testing/latest-results.txt). Les tests de base et la construction de l’archive sont aussi exécutés par GitHub Actions.

## Fluidité et reprise 0.4.9

- Distribution autonome installée dans `dist/Mira`, sauvegarde de 0.4.7 dans `.artifacts/previous-mira-0.4.7`. Les 184 fichiers du profil sont inchangés après copie (SHA-256), les 478 fichiers distribués correspondent à la version préparée. Raccourcis du projet et du menu Démarrer vérifiés : exécutable installé, logo dédié et AppUserModelID `Mira.Desktop`.

- Compilation Release sans erreur ni avertissement ; **30 tests** de bibliothèque et synchronisation réussis. Ajouts : ordre chronologique, priorité d’une reprise sur NextUp, activité d’une série après la fin d’un épisode, persistance et fusion sans écraser une date serveur plus récente.
- **50 contrôles du lecteur** réussis sur média synthétique muet : les nouveaux contrôles passent par lecture → arrêt → rangée rendue, puis lecture → mini-lecteur. Ils vérifient que le dernier film regardé passe à gauche et que le défilement horizontal revient au début. Résultats : `.artifacts/player-v049-check/result.txt`.
- **9 contrôles de mouvement** réussis : même valeur animée pour contour et zoom, interpolation, inversion en cours sans saut, transfert du survol sous un pointeur fixe pendant l’inertie, dimensions des cartes stables. Résultats : `.artifacts/visual-v049-final/motion-checks.txt`.
- Rendus de la bibliothèque à 1024, 1440 et 1920 unités WPF ; écrans de connexion et de compte inspectés à 1440 × 900, connexion à 1024 × 700. Indicateurs du carrousel arrondis et progression intermédiaire vérifiés. Rendus : `.artifacts/visual-v049-final`.
- Essai à la souris dans la fenêtre de validation : survol d’une carte « Continuer à regarder », molette depuis cette carte, transfert à une affiche puis à la rangée suivante sans déplacement du pointeur. Aucun média personnel lu ou modifié.
- **24 contrôles Windows** réussis à nouveau, avec le courtier multimédia réel et la vidéo synthétique. Résultats : `.artifacts/windows-v049-check/result.txt`. Les limites de vérification visuelle d’Explorer décrites ci-dessous restent applicables.
- Le classement utilise les dates Jellyfin disponibles et conserve les nouvelles lectures localement. Il ne reconstitue pas les dates manquantes des épisodes terminés avant cette version. Les contrôles vérifient la continuité des animations ; ils ne constituent pas une mesure de cadence sur toutes les configurations.

## Intégration Windows 0.4.8

- Compilation Release sans erreur ni avertissement ; **27 tests** de bibliothèque, synchronisation et comportement réussis.
- **24 contrôles Windows** réussis via le véritable courtier multimédia Windows et libmpv sur vidéo synthétique muette : publication du titre, épisode et affiche lisible, pause/reprise répétées idempotentes, seek système aller/retour, commandes de dix secondes, disponibilité du suivant, lecture de l’épisode suivant, réduction en zone de notification, commande pendant que la fenêtre est masquée, restauration par un second processus et nettoyage à l’arrêt/fermeture. Résultats : `.artifacts/windows-v048-final/result.txt`.
- **48 contrôles du lecteur** réussis, dont plein écran, mini-lecteur, raccourcis, volume et marquage vu lors du passage au suivant. Résultats : `.artifacts/player-v048-check/result.txt`.
- Les tests Windows n’enregistrent pas de raccourci Démarrer et publient une identité de validation distincte. Aucun épisode personnel lu ou modifié par ces essais.
- Limite du contrôle automatisé : la fiche Windows et les événements sont vérifiés via l’API système ; l’apparence de l’aperçu de la barre des tâches, les menus Explorer, la touche d’un clavier physique et les périphériques Bluetooth ne sont pas couverts par ce contrôle. Les couleurs des glyphes suivent le thème système, mais le changement de thème n’a pas été provoqué pendant les essais.

## Correctif 0.4.7

- Compilation Release sans erreur ni avertissement ; **28 tests** réussis, dont lecture libmpv + HTTP authentifié sur le serveur fictif. Nouveaux tests : fin de générique avant 90 %, persistance après réouverture, reprise du marquage vu sans renvoyer l’arrêt déjà accepté.
- **48 contrôles du lecteur** réussis avec une vidéo synthétique muette : ajout des vrais chemins de commande « épisode suivant » en milieu d’épisode, raccourci du générique, échéance du compte à rebours et fin naturelle avec ancienne position native. Les premiers conservent la reprise, les trois derniers marquent vu et effacent la reprise.
- **9 contrôles de mouvement** et rendus à 1024, 1440 et 1920 unités WPF réussis. Le mouvement réel du pointeur lève immédiatement la garde de défilement. Survol des cartes de reprise et des affiches, puis molette depuis une carte, vérifiés à la souris sur un profil séparé.
- Nouveau logo et 36 icônes vectorielles Mira : planche de contrôle dans `.artifacts/visual-v047/mira-icon-family.png`, PNG et ICO multi-tailles issus du même dessin. Centrage des boutons latéraux, marge droite des affiches et filtre de genre compact corrigés.
- Voile de démarrage déclaré dès le XAML, levé après préparation des premières images avec attente bornée ; les cascades de cartes sont supprimées à l’ouverture. La libération du voile est vérifiée par le contrôle visuel. La durée perçue du premier lancement sans cache reste dépendante du serveur.
- Aucun épisode personnel marqué vu pendant les essais. Les anciennes progressions ne sont pas converties en bloc : une progression seule ne permet pas de déterminer si l’épisode a réellement été terminé.

## Correctif 0.4.6

- Animations Windows d’ouverture, de fermeture et de réduction : la fenêtre garde le style de légende natif (`WS_CAPTION`), recouvert entièrement par le cadre personnalisé. Le contrôle du lecteur vérifie ce style, l’absence de barre de titre native (zone client égale à la fenêtre), puis son retrait en plein écran et son retour à la sortie : 43 vérifications réussies. Contrôle visuel et 8 contrôles de mouvement réussis.
- L’animation elle-même est jouée par Windows et n’a pas été observée automatiquement ; elle dépend du réglage « Effets d’animation », actif sur ce PC.

## Correctif 0.4.5

- Cause du survol « en deux temps » des boutons du lecteur mesurée avec la sonde `--perf-probe` du contrôle du lecteur (fenêtre 1480 × 930 unités WPF, DPI 200 %, écran 240 Hz). Chaque mouvement de souris recréait la découpe de la couche de commandes et redéplaçait sa fenêtre transparente : pendant un survol, l’animation tombait de 231 à **107 images/s** (27 % d’un cœur). Après correction : **231 images/s**, 9 % d’un cœur. La fenêtre transparente elle-même n’est pas limitante (231 images/s, 240 en mini-lecteur) ; une lecture de propriété mpv coûte 0,01 ms.
- Autres causes d’à-coups retirées : icône lecture / pause changée au clic (et non au relevé suivant), une seule courbe pour le fond et l’échelle du survol, écriture SQLite de la progression hors du fil de l’interface, calcul de la couleur du bandeau en arrière-plan, redessin de la barre de progression par pas de 0,4 pixel.
- Compilation Release sans erreur ni avertissement. **26 tests** réussis, dont l’intégration libmpv + HTTP. Nouveaux : titre arrêté au-delà de 90 % compté comme vu, rapport refusé (404) qui ne bloque pas la file, rapport d’arrêt envoyé avant la fermeture.
- Contrôle du lecteur : 40 vérifications réussies, dont l’indicateur de chargement et le curseur occupé levés à la première image, le compte à rebours « Épisode suivant dans 10 s » pendant le générique et son annulation.
- Contrôle visuel et 8 contrôles de mouvement réussis ; état vide de recherche, fiche avec bouton « Marquer comme vu » vérifiés sur les rendus.
- Non vérifié automatiquement : le menu contextuel ouvert à la souris, les raccourcis Alt + ← et bouton « précédent » de la souris, l’enregistrement automatique des réglages, le retour sur la fiche après une vraie lecture Jellyfin, l’épisode proposé par `Shows/NextUp` sur la bibliothèque personnelle et le marquage vu / non vu côté serveur (aucune mutation n’a été faite sur la bibliothèque personnelle).

## Correctif 0.4.4

- Compilation Release sans erreur ni avertissement. **23 tests** réussis, dont l'intégration libmpv + HTTP authentifié avec le serveur fictif. Nouveaux tests : requête `/MediaSegments` typée et filtrage, classement des titres de chapitres (opening, ending, récap, aperçu, générique selon sa position, avant-générique exclu), priorité des segments serveur, passage actif et bascule vers l'épisode suivant.
- Contrôle du lecteur (`--player-check`) : 37 vérifications réussies sur la version compilée et sur `dist/Mira`, avec `.artifacts/lecture-chapitres.mkv` (18 s, chapitres « Opening », « Partie A », « Ending » ajoutés par ffmpeg). Nouveautés couvertes : icônes non rognées et centrées dans chaque bouton, barre qui avance entre deux relevés mpv, Espace qui met en pause même quand l'événement vise le bouton +10 s, flèche droite qui avance de 10 s depuis n'importe quel bouton, masquage des commandes et du curseur en pause, exclusion de la couche de commandes des captures, points de chapitres, bouton « Passer l'intro » et saut à la fin du passage, mini-lecteur arrondi dans son coin, aimantation au coin le plus proche, rangement derrière la languette avec la vidéo conservée, retour par la languette.
- Contrôle visuel (`--visual-check --demo`) et 8 contrôles de mouvement réussis ; le survol des affiches n'a plus de légende et peut revenir sur l'affiche sous le pointeur après le défilement. Rendus de la couche de commandes dans `.artifacts/player-v044-check-result`.
- Non vérifié automatiquement : le geste réel de glisser à la souris (le contrôle appelle la fin de glisser directement), les coins arrondis de la vidéo native et l'ombre tels que composés par Windows, le rendu réel d'une capture Win + Maj + S, et les segments Jellyfin sur la bibliothèque personnelle (aucune lecture ni mutation n'y a été faite).

## Correctif 0.4.3

Cadre arrondi et contour natifs vérifiés sur Windows 11 (DPI 200 %), dans la bibliothèque et avec une vidéo synthétique muette. Agrandissement, plein écran et retour en fenêtre testés à la souris et au clavier. Les marges invisibles de redimensionnement Windows sont compensées en mode agrandi pour conserver les boutons dans la zone visible. Les 23 contrôles WPF/libmpv passent avec le nouveau cadre.

## Correctif 0.4.2

- Compilation Release sans erreur ni avertissement ; 23 assertions WPF/libmpv réussies. Alignement vertical volume/curseur/bouton pause, marges inférieures, interruption du fondu et conservation de la surface native compris.
- Rendus de la bibliothèque réelle à 1024, 1440 et 1920 unités WPF réussis ; 8 contrôles de mouvement/navigation réussis. Résultats dans `.artifacts/player-v042-check-result` et `.artifacts/visual-v042`.

- Essais réels à la souris sur une vidéo synthétique muette, profil séparé : les commandes cachées reviennent sans Échap en fenêtre et en plein écran. `GetCursorInfo` confirme le curseur système caché pendant l'inactivité, puis visible après mouvement.
- Même surface de commandes observée avant/après passage du fenêtré au plein écran 3840 × 2160 (DPI 200 %), puis au mini-lecteur 380 × 214 unités WPF. La vidéo conserve son instance et sa position.
- Clic pause, panneau de réglages, fermeture du panneau par Échap, réduction par son bouton, défilement avec mini-lecteur et survol intégré vérifiés dans la fenêtre de test.
- Survol constaté sur une affiche puis supprimé dès la molette ; aucun panneau ne recouvre les autres affiches. La barre supérieure reste transparente pendant le défilement.
- Le journal opt-in `player-ui-probe.jsonl` consigne uniquement les états d'interface pour `--qa-window --demo --test-media`, dans le profil de test. Il n'est pas activé en utilisation normale.

## Correctif 0.4.1

Bandeau épuré : suppression des raccourcis redondants, de la mention au-dessus du titre et du bouton pause du carrousel. Les réglages couvrent toute la fenêtre, y compris sous le rail, avec animation du contenu indépendante du fond.

## Résultats

- Compilation Release : aucune erreur ni avertissement au dernier contrôle.
- Tests automatisés : **20 réussis**, dont un avec la bibliothèque mpv.net réellement installée (`libmpv-2.dll`).
- Version 0.4 inspectée sur la bibliothèque réelle : accueil, catalogue, fiches, aperçus au survol et réglages, à 1024, 1440 et 1920 pixels de largeur. Rendus réalisés par l’application hors du bureau avec un profil de validation séparé.
- Molette synthétique routée depuis une carte de « Continuer à regarder » : événement traité par la page, passage observé d’environ 67 à 108 pixels au cours de l’interpolation.
- Rotation du bandeau, segment de durée à 50 %, conservation des instances de cartes après actualisation, aperçu au bord inférieur, mode de mouvement réduit et changements rapides de page vérifiés dans WPF.
- Titre de fiche présenté avant toute attente réseau ; 1 ms observée dans le profil préchargé. Cette mesure concerne la présentation initiale, pas le chargement complet ni le débit d’affichage du PC.
- Menus, raccourci Ctrl+K et recherche « Samurai » vérifiés dans la fenêtre Windows en 0.2 (un résultat : Samurai Champloo).
- Facettes réelles : 21 genres et 16 années récupérés. Les menus incluent une option « Tous / Toutes » supplémentaire.
- Lecture d’une vidéo synthétique 1280 × 720 à 24 images/s dans la surface mpv intégrée : image affichée et progression active.
- Pause vérifiée, puis déplacement de 13,25 s à 3,25 s avec la commande −10 s.
- Bibliothèque chargée dans l’application depuis un serveur HTTP de test isolé.
- Flux HTTP exigeant l’authentification : chargement dans libmpv, reprise à 2 s, pause, déplacement à 8 s, détection de piste audio et arrêt.
- Réception des rapports `Sessions/Playing`, `Sessions/Playing/Progress`, `Sessions/Playing/Stopped` dans cet ordre, avec les positions attendues.
- La bibliothèque personnelle a été consultée en lecture seule pour la validation visuelle. Les essais de lecture et de synchronisation utilisent un serveur fictif séparé.
- Lecteur 0.4 : 18 assertions WPF/libmpv réussies sur un média synthétique : seek, mini-lecteur, navigation, agrandissement, dimensions physiques du plein écran et restauration, mute/unmute (y compris volume zéro), vitesse, sous-titres, placement des menus à 200 %, raccourcis natifs et continuité des modes entre vidéos.
- Vérification à la souris : plein écran sur le moniteur 3840 × 2160 à 200 %, retour en fenêtre, menu de vitesse et sélection 1,25×, panneau dans les limites du lecteur, réduction dans la bibliothèque et navigation en conservant la vidéo.
- Contrôles vidéo superposés observés en fenêtre et en mode mini ; disparition lors de la lecture inactive et réapparition à la pause. Média synthétique muet uniquement.
- Police Nunito Sans embarquée résolue par WPF ; rendus exportés au DPI réel, pas rééchantillonnés à 96 DPI.
- Aperçus : zoom réduit à 2,5 %, panneau compact, respect des limites inférieures. Palette blanche et accent calculé par illustration.

## Tests couverts

1. Validation et normalisation d’adresse avec préservation d’un sous-chemin de proxy.
2. Authentification JSON, en-tête de session et encodage de recherche.
3. Persistance, ordre et coalescence de la file de synchronisation.
4. Réponse ancienne concurrente avec une progression plus récente.
5. Reprise locale et isolation entre comptes.
6. Sauvegarde réactive malgré un serveur lent.
7. Reconnexion et livraison ordonnée après une coupure.
8. Stockage de session protégé par DPAPI.
9. Bornes de progression et numérotation des épisodes.
10. Intégration libmpv + HTTP authentifié + synchronisation.
11. Paramètres de filtres, encodage, pagination et séparation des caches filtrés.
12. Chargement récursif des genres et extraction des années.
13. Migration des anciens réglages et persistance des préférences d’apparence/reprise.
14. Interpolation équivalente avec des pas de temps simulant 30, 60 et 144 images/s.
15. Inversion du défilement et bornes après réduction de la taille du contenu.
16. Durée du carrousel, pause, reprise et remise à zéro après sélection.
17. Partage des requêtes de préchargement et invalidation des métadonnées.
18. Nouvelle tentative après une erreur temporaire de préchargement.
19. Extraction de teintes différentes et lisibles pour des images rouges et bleues.
20. Repli blanc pour les images neutres et transparentes.
21. Segments Jellyfin : types demandés, passages inconnus ou trop courts ignorés.
22. Titres de chapitres transformés en passages à sauter.
23. Fusion des passages, passage actif et « Épisode suivant » en fin de lecture.
24. Arrêt local au-delà de 90 % : titre vu, reprise à zéro.
25. Rapport refusé définitivement écarté sans bloquer la file.
26. Rapport d’arrêt livré pendant la fermeture du service de synchronisation.

## Reproduire

```powershell
dotnet restore tests/Mira.Tests/Mira.Tests.csproj --configfile NuGet.Config
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-restore
```

Le test d’intégration demande un fichier `.artifacts/lecture-test.mp4` de plus de 10 secondes, libmpv, et le serveur fictif :

```powershell
node tools/mock-jellyfin.mjs
# Dans un autre terminal :
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release -- --mpv-integration
```

Les données et journaux de test restent sous `.artifacts/`, ignoré par Git. `--prepare-ui` crée exclusivement une session fictive dans `.artifacts/mock-profile` pour les essais de l’interface.

Un contrôle de rendu intégré peut exporter les vues de l’application sans interagir avec le bureau : passer `--data <profil de test séparé> --visual-check <dossier de sortie>`. Le profil doit déjà contenir une connexion protégée valide, ou ajouter `--demo`. Les fichiers `result.txt`, `layout.txt` et `motion-checks.txt` consignent le résultat. Les contrôles du lecteur natif ne sont pas capturés par ce rendu WPF.

Le contrôle du lecteur se lance sur un profil isolé :

```powershell
src/Mira.Desktop/bin/Release/net8.0-windows10.0.19041.0/Mira.exe --data .artifacts/player-check/profile --demo --test-media .artifacts/lecture-test.mp4 --player-check .artifacts/player-check
```

Le résultat est écrit dans `result.txt`. Les changements de modes utilisent le lecteur natif et un rectangle physique hors du bureau ; les vérifications à la souris complètent ce contrôle pour les fenêtres superposées. Avec une vidéo chapitrée, le contrôle vérifie aussi les passages à sauter :

```powershell
ffmpeg -i .artifacts/lecture-test.mp4 -i .artifacts/chapitres.txt -map_metadata 1 -map_chapters 1 -c copy .artifacts/lecture-chapitres.mkv
```

## À vérifier avec l’utilisateur

Reprise entre Mira et ses autres clients, sous-titres utilisés habituellement, HDR éventuel, écran(s) et périphérique audio. Le plein écran a été vérifié sur le moniteur actuel. Les parcours d’épisodes réels et les configurations multi-écrans restent à éprouver avec les contenus et périphériques habituels.
