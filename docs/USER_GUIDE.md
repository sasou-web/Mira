# Mira

Application Windows pour parcourir une bibliothèque Jellyfin et regarder ses vidéos avec **mpv intégré**. Version de développement : **0.4.9**.

## Démarrer

Ouvrir **`dist/Mira/Mira.exe`**. L’adresse proposée est `http://127.0.0.1:8096` : Jellyfin reste lancé en arrière-plan sur le PC. Se connecter avec son compte Jellyfin, ou choisir **Explorer la démo** pour découvrir l’interface avec des titres et illustrations fictifs.

Le moteur déjà installé avec Jellyfin MPV Shim ou mpv.net est détecté automatiquement. Un autre `mpv-2.dll` ou `libmpv-2.dll` 64 bits peut être sélectionné dans **Réglages**. Mira charge directement la bibliothèque vidéo. La version publiée inclut le runtime .NET.

## Disponible

- Identité Windows unifiée : logo Mira multi-résolutions dans la fenêtre, Alt + Tab, les raccourcis et le menu Démarrer. Le raccourci Démarrer est créé au premier lancement du profil principal.
- Au survol de Mira dans la barre des tâches pendant une lecture : reculer de 10 s, lecture/pause, avancer de 10 s et épisode suivant (si disponible). Ces commandes pilotent le même lecteur mpv.
- Fiche multimédia Windows : titre, épisode, affiche, état de lecture et progression. Les commandes système et touches multimédias contrôlent Mira même en arrière-plan ; l’arrêt retire la session active.
- Icône Mira dans la zone de notification près de l’horloge (éventuellement dans les icônes masquées). Clic gauche pour ouvrir ; clic droit pour les commandes de lecture, réduire dans cette zone ou quitter. La fermeture par × quitte toujours Mira. Relancer le raccourci restaure la fenêtre déjà ouverte et sa lecture.
- Accueil avec reprise de lecture, fiches, films, séries/animes, bibliothèques et favoris.
- Cadre natif Windows 11 : coins arrondis, contour fin et ombre système, avec les animations Windows d’ouverture, de fermeture, de réduction et de restauration (si « Effets d’animation » est activé dans Windows). Les marges restent dans la zone visible en fenêtre agrandie ; la lecture plein écran reste bord à bord.
- Interface cinéma : typographie Nunito Sans embarquée, commandes blanches, rail transparent et fonds panoramiques Jellyfin. Les accents du bandeau suivent la couleur dominante de chaque image. Monogramme Mira et famille d’icônes vectorielles aux courbes arrondies ; boutons de navigation en carré adouci.
- Carrousel automatique : fondu entre les titres toutes les 6,5 secondes, indicateurs arrondis qui progressent entre les pixels, sélection manuelle. La rotation se suspend pendant les interactions, dans les autres écrans et lorsque la fenêtre est inactive.
- Défilement progressif, y compris au-dessus de « Continuer à regarder ». Maj + molette et les flèches déplacent la rangée horizontalement.
- Survol intégré aux affiches : anneau lumineux et zoom de 2,5 % animés ensemble, sans délai. Le survol suit aussi les cartes qui défilent sous une souris immobile ; une entrée ou sortie rapide reprend à la position visuelle actuelle, sans saut.
- « Continuer à regarder » place les films et séries les plus récemment regardés à gauche, en conservant une carte par série. La rangée revient au début après une lecture ou le passage au mini-lecteur. L’activité récente est enregistrée localement pour garder cet ordre après redémarrage et pendant une synchronisation différée.
- Écran de connexion centré et compact, aux couleurs de Mira ; depuis le compte, le fond reprend discrètement l’illustration de la bibliothèque.
- Transitions des pages, boutons et interrupteurs animés : fond de survol en fondu, léger grossissement des boutons-icônes, contour de focus uniquement au clavier ; réglage pour réduire les animations. Le titre d’une fiche apparaît avant les requêtes de détails, avec préchargement au survol.
- Favoris accessibles dans le catalogue via « Favoris », sans cœur dans le rail latéral. Un titre retiré quitte aussitôt la liste des favoris.
- Clic droit (ou Maj + F10) sur une affiche, une carte de reprise ou un épisode : lire, voir la fiche ou la série, marquer comme vu / non vu, ajouter ou retirer des favoris. La fiche propose aussi « Marquer comme vu ».
- Au démarrage, un fond sobre avec le logo masque la construction de la page et attend les premières images (900 ms maximum une fois les données disponibles). Pas de cascade de cases vides. Les images et fonds apparaissent en fondu ; une actualisation conserve les pages déjà chargées et la position du défilement.
- États vides adaptés (recherche, filtres, favoris) avec un bouton pour réinitialiser les filtres. Les cartes « Continuer à regarder » indiquent le temps restant.
- Filtres par genre, année, progression et bibliothèque ; tri par ajout, titre, année ou note sur l’ensemble du catalogue.
- Fiches avec sélection de saison et vignettes d’épisodes. La fiche et le favori de l’accueil ciblent la série ; le bouton de lecture indique l’épisode choisi par Jellyfin (« Reprendre · S01 E07 »), marqué « À suivre » dans la liste. Après une lecture lancée depuis une fiche, on revient sur cette fiche, progression et « ✓ Vu » à jour.
- Une fin naturelle, un arrêt au-delà de 90 % ou le passage à l’épisode suivant depuis le générique marque le titre comme vu et efface sa reprise. L’arrêt et le marquage sont enregistrés ensemble, puis livrés dans l’ordre à Jellyfin ; le statut reste visible hors connexion. Passer au suivant en milieu d’épisode conserve la progression.
- Réglages répartis entre lecture, audio/sous-titres, apparence et serveur, avec aperçu des sous-titres et préférences de densité. Ils sont enregistrés automatiquement en quittant l’écran.
- Navigation au clavier et à la souris : **Ctrl + K** ou **Ctrl + F** pour chercher, **Échap** efface la recherche puis revient en arrière, **Alt + ←** et le bouton « précédent » de la souris ferment la fiche, les réglages ou l’écran de compte. Le focus entre dans la fiche ouverte et revient sur l’affiche à la fermeture. **Entrée** valide la connexion.
- Recherche différée de 250 ms, annulation des anciennes requêtes, pagination par 60 titres.
- Cache SQLite de la bibliothèque et cache d’images sur disque, séparés par serveur et compte.
- Lecture mpv avec commandes et curseur masqués après 2,6 s sans interaction (4 s en pause), puis réaffichés au mouvement de la souris : pause, déplacement, volume, plein écran, pistes audio, sous-titres, vitesse et décalage.
- Captures d’écran propres : les commandes du lecteur sont exclues des captures Windows (Win + Maj + S, Impr. écran, Game Bar) à partir de Windows 10 2004. Elles le sont aussi des enregistrements et partages d’écran.
- Barre de progression fluide : position interpolée à chaque image, toute la hauteur est cliquable, un appui saute au point visé et se prolonge en glisser, avec l’image qui suit. Le survol affiche le temps et le chapitre visés.
- Chapitres du fichier (ou de Jellyfin) sous forme de points sur la barre. Passages « Passer l’opening », « Passer l’ending », récap et aperçu en bas à droite, d’après les segments Jellyfin (10.10+ : serveur ou extension comme Intro Skipper) ou les titres de chapitres. « Épisode suivant » apparaît pendant le générique final, ou dans les 20 dernières secondes.
- Avec « Enchaîner les épisodes », le bouton affiche un compte à rebours de 10 secondes (en temps de lecture, donc suspendu en pause) ; « Annuler » reste sur l’épisode et désactive l’enchaînement pour cette fin.
- Ouverture d’une vidéo signalée tout de suite (curseur occupé, indicateur jusqu’à la première image). Les commandes réagissent immédiatement : l’icône lecture / pause change au clic, le survol des boutons est un seul mouvement.
- Bouton épisode suivant à droite de la barre, avec les réglages. Le lecteur agrandi n’a plus de bouton « arrêter » : la flèche en haut à gauche ramène à la bibliothèque sans couper la vidéo.
- Mini-lecteur dans la bibliothèque sans redémarrage de la vidéo : coins arrondis, ombre, commandes au survol. Il se déplace à la souris et se range dans le coin le plus proche ; poussé au-delà du bord droit, il se replie derrière une languette qu’un clic ou une traction ramène. Le mode plein écran ou mini est conservé lors du passage au titre suivant.
- Sous-titres externes, langues préférées, taille des sous-titres, décodage matériel et épisode suivant.
- Progression enregistrée localement puis envoyée à Jellyfin toutes les 3 secondes et sur les changements de lecture. Une réponse lente ne bloque pas cette sauvegarde, et l’écriture sur disque se fait hors de l’interface pour ne pas saccader la lecture.
- File persistante pour les envois échoués, reprise des tentatives toutes les 3 secondes, état et erreur visibles dans Réglages → Jellyfin & synchronisation. Un rapport refusé définitivement (titre supprimé) ne bloque plus les suivants ; à la fermeture, Mira laisse jusqu’à 2 secondes au dernier rapport pour partir.
- Écoute des changements Jellyfin par WebSocket et actualisation de secours toutes les 45 secondes lorsque l’application est active.

Les commandes **Favoris** et la lecture mettent à jour le compte connecté. Le mode démo ne communique pas avec Jellyfin ; son bouton de lecture permet de choisir une vidéo locale.

## Raccourcis du lecteur

**Espace** ou **K** : lecture / pause · **← / →** : −10 / +10 secondes · **↑ / ↓** : volume · **F** ou double-clic vidéo : plein écran · **I** : mini-lecteur / agrandir · **M** : couper / réactiver le son · **S** : passer l’opening, l’ending ou lancer l’épisode suivant quand le bouton est affiché. Ces raccourcis restent actifs après un clic sur un bouton ou sur la barre. **Échap** ferme le menu ouvert, quitte le plein écran, puis réduit la lecture dans la bibliothèque. Un clic sur la vidéo met en pause ou reprend ; un double-clic sur le mini-lecteur l’agrandit. Le **×** du mini-lecteur arrête la lecture.

## Données locales

Le dossier `data` situé à côté de l’exécutable contient les préférences, le cache, la file de synchronisation et la session. Le mot de passe n’est pas enregistré. Le jeton de session est protégé avec **Windows DPAPI**, lié au compte Windows courant. Les URL de lecture utilisent un en-tête d’authentification ; elles ne contiennent pas le jeton. Le protocole WebSocket Jellyfin requiert un jeton dans son URL, qui n’est pas journalisée.

Pour un serveur distant, utiliser HTTPS. Pour déplacer l’application, copier son dossier complet. Une session protégée sur un autre compte Windows nécessite une nouvelle connexion.

## Développement

Prérequis : Windows 10 2004 ou plus récent, x64, SDK .NET 8, une bibliothèque libmpv x64. La solution utilise C# / WPF pour l’interface native, SQLite pour le stockage et `HttpClient` pour Jellyfin. Le SDK Windows est référencé par le framework cible ; Windows Forms sert uniquement à l’icône de notification et à son menu.

```powershell
dotnet restore src/Mira.Desktop/Mira.Desktop.csproj --configfile NuGet.Config
dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release
```

Pour créer la distribution :

```powershell
./tools/publish.ps1
```

Les commandes de test et le relevé de validation sont dans [VALIDATION.md](VALIDATION.md). L’organisation du code et les choix de conception sont décrits dans [ARCHITECTURE.md](ARCHITECTURE.md).

## Limites de cette première version

La lecture native et les échanges HTTP ont été testés avec des médias synthétiques et un serveur fictif isolé. L’interface, les images et la recherche ont aussi été vérifiées en lecture seule avec la bibliothèque personnelle sur Jellyfin **12.1.0**, à plusieurs tailles de fenêtre.

La prise en charge d’anciennes versions de l’API Jellyfin n’est pas encore garantie. Le HDR, les sous-titres ASS complexes, les sorties audio multicanales, les configurations multi-écrans et les bibliothèques très volumineuses nécessitent des essais dédiés. La sélection de versions multiples d’un même film, les collections éditables et les profils avancés de rendu mpv sont également à venir.

La distribution ne contient pas libmpv. Les notices des composants inclus sont dans [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md). La signature de l’exécutable reste à prévoir.

Le logo et les icônes actuels sont dessinés dans `Views/Icon.cs`, sans dépendance externe. Les SVG Phosphor de l’ancienne interface restent archivés sous licence MIT dans `Assets/Phosphor`.

La police [Nunito Sans](https://github.com/googlefonts/NunitoSans) est embarquée sous SIL Open Font License 1.1. Les sources, révisions et notices sont conservées dans `Assets/Fonts` et reproduites par `tools/import-font.ps1`.
