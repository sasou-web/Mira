# Mira

Application Windows pour parcourir une bibliothèque Jellyfin et regarder ses vidéos avec **mpv intégré**. Version de développement : **0.5.3**.

## Démarrer

Ouvrir **`dist/Mira/Mira.exe`**. L’adresse proposée est `http://127.0.0.1:8096` : Jellyfin reste lancé en arrière-plan sur le PC. Se connecter avec son compte Jellyfin, ou choisir **Explorer la démo** pour découvrir l’interface avec des titres et illustrations fictifs.

Sans serveur, **Pas encore de serveur ? Installer Jellyfin sur ce PC** installe Jellyfin et crée `Films`, `Séries` et `Animes` dans `Vidéos\Jellyfin`, ou dans un autre dossier choisi. Il configure aussi Jellyfin en français avec ton compte, puis te connecte. Windows demande une autorisation pendant l’installation. Détails dans [INSTALLATION.md](INSTALLATION.md#installer-jellyfin-depuis-mira).

Le moteur vidéo mpv s’installe depuis l’installateur (case cochée par défaut), depuis **Réglages → Lecture → Installer le moteur mpv**, ou en un clic à la première lecture : Mira télécharge une build Windows précise de mpv (31 Mo), vérifie ses empreintes et la range dans `data\mpv`. Un moteur déjà installé avec Jellyfin MPV Shim ou mpv.net est détecté et réutilisé ; un autre `mpv-2.dll` ou `libmpv-2.dll` 64 bits peut être sélectionné dans **Réglages**. Mira charge directement la bibliothèque vidéo. La version publiée inclut le runtime .NET.

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
- Adresse du serveur tolérante : `192.168.1.20`, `nas:8096`, un nom de domaine ou l’adresse copiée depuis la page web de Jellyfin. Sans `http://`, Mira essaie HTTPS, le port 8096 puis HTTP, et affiche l’adresse retenue. Une adresse qui n’est pas un serveur Jellyfin, un certificat non reconnu ou un Jellyfin antérieur à 10.9 sont signalés clairement, avant l’envoi du mot de passe.
- Transitions des pages, boutons et interrupteurs animés : fond de survol en fondu, léger grossissement des boutons-icônes, contour de focus uniquement au clavier ; réglage pour réduire les animations. Le titre d’une fiche apparaît avant les requêtes de détails, avec préchargement au survol.
- Favoris accessibles dans le catalogue via « Favoris », sans cœur dans le rail latéral. Un titre retiré quitte aussitôt la liste des favoris.
- Clic droit (ou Maj + F10) sur une affiche, une carte de reprise ou un épisode : lire, voir la fiche ou la série, marquer comme vu / non vu, ajouter ou retirer des favoris. La fiche propose aussi « Marquer comme vu ».
- Au démarrage, un fond sobre avec le logo masque la construction de la page et attend les premières images (900 ms maximum une fois les données disponibles). Pas de cascade de cases vides. Les images et fonds apparaissent en fondu ; une actualisation conserve les pages déjà chargées et la position du défilement.
- États vides adaptés (recherche, filtres, favoris) avec un bouton pour réinitialiser les filtres. Les cartes « Continuer à regarder » indiquent le temps restant.
- Filtres par genre, année, progression et bibliothèque ; tri par ajout, titre, année, note ou dernière lecture sur l’ensemble du catalogue.
- Fiches avec sélection de saison et vignettes d’épisodes. La fiche et le favori de l’accueil ciblent la série ; le bouton de lecture indique l’épisode choisi par Jellyfin (« Reprendre · S01 E07 »), marqué « À suivre » dans la liste. Après une lecture lancée depuis une fiche, on revient sur cette fiche, progression et « ✓ Vu » à jour.
- Fiches : distribution et réalisation sous les genres ; rangée « Titres similaires » proposée par Jellyfin, sur une ligne à la largeur de la fenêtre, avec les mêmes clics droits que la bibliothèque.
- Une fin naturelle, un arrêt au-delà de 90 % ou le passage à l’épisode suivant depuis le générique marque le titre comme vu et efface sa reprise. L’arrêt et le marquage sont enregistrés ensemble, puis livrés dans l’ordre à Jellyfin ; le statut reste visible hors connexion. Passer au suivant en milieu d’épisode conserve la progression.
- Réglages répartis entre lecture, audio/sous-titres, apparence, serveur, TorLink et mises à jour, avec aperçu des sous-titres et préférences de densité. Ils sont enregistrés automatiquement en quittant l’écran.
- Mises à jour automatiques : Mira cherche une nouvelle version à l’ouverture puis toutes les 6 heures, la télécharge et la vérifie (signature de Mira, taille, SHA-256), puis l’installe à la fermeture ; **Redémarrer** l’installe tout de suite. **Réglages → Mises à jour** montre l’état, lance une recherche et désactive la recherche automatique. Le dossier `data` n’est jamais modifié.
- Navigation au clavier et à la souris : **Ctrl + K** ou **Ctrl + F** pour chercher, **Échap** efface la recherche puis revient en arrière, **Alt + ←** et le bouton « précédent » de la souris ferment la fiche, les réglages ou l’écran de compte. Le focus entre dans la fiche ouverte et revient sur l’affiche à la fermeture. **Entrée** valide la connexion.
- Recherche différée de 250 ms, annulation des anciennes requêtes, pagination par 60 titres.
- Cache SQLite de la bibliothèque et cache d’images sur disque, séparés par serveur et compte. Les images occupent au plus 1 Go sur disque (ramenées à 768 Mo, les moins récemment vues d’abord) et 512 Mo en mémoire ; les pages de catalogue inutilisées depuis 30 jours sont oubliées.
- Lecture mpv avec commandes et curseur masqués après 2,6 s sans interaction (4 s en pause), puis réaffichés au mouvement de la souris.
- Barre de lecture : titre et ligne d’épisode (« Épisode 3 / 12 · nom », avec la saison si la série en compte plusieurs) à gauche, chapitre en cours et temps à droite. Dessous : lecture / pause, épisode suivant et volume à gauche ; **⋮** (vitesse, décalage des sous-titres, informations), audio et sous-titres, mini-lecteur et plein écran à droite. Le curseur de volume sort du haut-parleur au survol, ou un instant après ↑ / ↓.
- Menu audio et sous-titres en deux colonnes, la piste en cours cochée, avec sa langue, son codec et ses canaux ; « Désactivés » et « Charger un fichier… » pour les sous-titres. Le menu reste ouvert pour choisir l’autre piste ; Échap le ferme.
- Captures d’écran propres : les commandes du lecteur sont exclues des captures Windows (Win + Maj + S, Impr. écran, Game Bar) à partir de Windows 10 2004. Elles le sont aussi des enregistrements et partages d’écran.
- Barre de progression fluide : position interpolée à chaque image, toute la hauteur est cliquable, un appui saute au point visé et se prolonge en glisser, avec l’image qui suit. Le survol affiche le temps et le chapitre visés.
- Chapitres du fichier (ou de Jellyfin) : la barre est découpée en segments, celui sous le pointeur s’épaissit, et le chapitre en cours est nommé au-dessus du temps. Passages « Passer l’opening », « Passer l’ending », récap et aperçu en bas à droite, d’après les segments Jellyfin (10.10+ : serveur ou extension comme Intro Skipper) ou les titres de chapitres. « Épisode suivant » apparaît pendant le générique final, ou dans les 20 dernières secondes.
- Avec « Enchaîner les épisodes », le bouton affiche un compte à rebours de 10 secondes (en temps de lecture, donc suspendu en pause) ; « Annuler » reste sur l’épisode et désactive l’enchaînement pour cette fin.
- Ouverture d’une vidéo signalée tout de suite (curseur occupé, indicateur jusqu’à la première image). Les commandes réagissent immédiatement : l’icône lecture / pause change au clic, le survol des boutons est un seul mouvement.
- Bouton épisode suivant juste après lecture / pause, pour les épisodes. Le lecteur agrandi n’a pas de bouton « arrêter » : la flèche en haut à gauche ramène à la bibliothèque sans couper la vidéo.
- Mini-lecteur dans la bibliothèque sans redémarrage de la vidéo : coins arrondis, ombre, commandes au survol. Il se déplace à la souris et se range dans le coin le plus proche ; poussé au-delà du bord droit, il se replie derrière une languette qu’un clic ou une traction ramène. Le mode plein écran ou mini est conservé lors du passage au titre suivant.
- Sous-titres externes, langues préférées, taille des sous-titres, décodage matériel et épisode suivant.
- Progression enregistrée localement puis envoyée à Jellyfin toutes les 3 secondes et sur les changements de lecture. Une réponse lente ne bloque pas cette sauvegarde, et l’écriture sur disque se fait hors de l’interface pour ne pas saccader la lecture.
- File persistante pour les envois échoués, reprise des tentatives toutes les 3 secondes, état et erreur visibles dans Réglages → Jellyfin & synchronisation. Un rapport refusé définitivement (titre supprimé) ne bloque plus les suivants ; à la fermeture, Mira laisse jusqu’à 2 secondes au dernier rapport pour partir.
- Écoute des changements Jellyfin par WebSocket et actualisation de secours toutes les 45 secondes lorsque l’application est active.

Les commandes **Favoris** et la lecture mettent à jour le compte connecté. Le mode démo ne communique pas avec Jellyfin ; son bouton de lecture permet de choisir une vidéo locale.

## TorLink

Le bouton **TorLink** du rail (flèche de téléchargement) ouvre la page TorLink. L’interface de TorLink, installé séparément, y tourne telle quelle : recherche, file, pause, reprise, partage et réglages restent ceux de TorLink. Mira le retrouve par son raccourci Démarrer ou par le dossier indiqué dans les réglages, et le démarre une seule fois. Si TorLink est déjà ouvert dans une autre fenêtre, la page le signale au lieu d’en lancer un second. Sans le moteur WebView2, **Ouvrir dans une fenêtre** lance TorLink à part, comme son raccourci, avec la même vérification.

- Dans le terminal, les touches vont à TorLink, y compris Échap et les flèches ; **Alt + ←** revient à Mira. La molette fait défiler les listes. **Ctrl + C** copie le texte sélectionné ; sans sélection, il est transmis à TorLink, qui se ferme comme dans un terminal. **Ctrl + V** colle.
- Le panneau **Vers Jellyfin** suit chaque téléchargement terminé : en attente des fichiers, rangé, disponible, déjà présent, ignoré ou en échec. Selon l’état : **Voir** ouvre la fiche Mira, **Dossier** montre le fichier dans l’Explorateur, **Réessayer** relance le rangement et **Classer** le déplace vers Films, Séries ou Animes.
- Films : `Titre (Année)\Titre (Année).mkv`, sous-titres `Titre (Année).fr.srt` et bonus dans `Extras`. Séries et animes : `Série\Season 01\Série - S01E02.mkv`, génériques sans texte dans `Extras`. Un dossier existant du même titre est réutilisé, avec sa saison nommée. Échantillons, notes et fichiers annexes restent dans TorLink ; les jeux et téléchargements sans vidéo sont ignorés.
- Les animes sont reconnus par leur source TorLink (Nyaa, SubsPlease), un dossier existant dans Animes, ou un nom de sortie de fansub. Sans dossier Animes, ils rejoignent les séries.
- Un fichier existant n’est jamais remplacé. Mira ne crée de fichiers que dans les dossiers de la bibliothèque, et seulement des vidéos et des sous-titres. Un fichier identique qui s’y trouvait déjà est compté comme présent, mais **Classer** ne le déplace jamais.
- Jellyfin est prévenu des nouveaux dossiers ; une pastille sur le bouton TorLink et une notification annoncent le rangement, puis la disponibilité dans la bibliothèque, quand Jellyfin voit les fichiers au même chemin que Mira.
- TorLink reste un client BitTorrent : tant qu’il tourne, il échange avec des pairs, écoute un port et peut demander au routeur de l’ouvrir (UPnP, NAT-PMP). Mira n’ajoute ni port ni service réseau.
- Fermer Mira demande à TorLink de quitter, comme Ctrl + C, puis le termine au bout de 3 secondes s’il tourne encore. Ses téléchargements reprennent à la prochaine ouverture de la page. Réduire Mira dans la zone de notification laisse TorLink tourner.

**Réglages → TorLink** : rangement automatique, partage conservé (lien physique ou copie ; désactivé, le fichier est déplacé et TorLink cesse de le partager), emplacement de TorLink et dossiers Films, Séries et Animes. Laissés vides, ces dossiers sont ceux des bibliothèques Jellyfin, lus avec un compte administrateur, s’ils existent sur ce PC. Seuls les téléchargements terminés après la première activation, ou après la réactivation du rangement automatique, sont rangés d’eux-mêmes ; les cinq plus récents d’avant restent proposés avec **Importer**. Un reclassement est mémorisé : **Réessayer** garde la bibliothèque choisie.

Mira ne choisit ni ne vérifie les contenus. Les sources intégrées à TorLink indexent surtout des copies dont la diffusion n’est pas autorisée : ne télécharger que ce que tu as le droit d’obtenir.

## Raccourcis du lecteur

**Espace** ou **K** : lecture / pause · **← / →** : −10 / +10 secondes · **↑ / ↓** : volume · **F** ou double-clic vidéo : plein écran · **I** : mini-lecteur / agrandir · **M** : couper / réactiver le son · **S** : passer l’opening, l’ending ou lancer l’épisode suivant quand le bouton est affiché. Ces raccourcis restent actifs après un clic sur un bouton ou sur la barre. **Échap** ferme le menu ouvert, quitte le plein écran, puis réduit la lecture dans la bibliothèque. Un clic sur la vidéo met en pause ou reprend ; un double-clic sur le mini-lecteur l’agrandit. Le **×** du mini-lecteur arrête la lecture.

## Données locales

Si le cache d’un compte devient illisible (coupure de courant, disque défaillant), Mira le met de côté sous le nom `library-….db.bad-<date>`, en recrée un et le signale ; les envois de progression qui n’étaient pas encore partis sont perdus. Le dossier `data` situé à côté de l’exécutable contient les préférences, le cache, la file de synchronisation et la session. Il contient aussi `torlink-imports.json`, le journal des rangements TorLink (noms, chemins et états, sans lien magnet), et `webview2`, le profil du terminal intégré. Mira lit l’état de TorLink sans le modifier. En cas d’erreur inattendue, `errors.log` note la date, le type d’erreur et la méthode en cause, sans message ni adresse ; il est plafonné à 256 Ko. Le mot de passe n’est pas enregistré. Le jeton de session est protégé avec **Windows DPAPI**, lié au compte Windows courant. Les URL de lecture utilisent un en-tête d’authentification ; elles ne contiennent pas le jeton. Le protocole WebSocket Jellyfin requiert un jeton dans son URL, qui n’est pas journalisée.

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

Jellyfin 10.9 ou plus récent est nécessaire : un serveur plus ancien est refusé à la connexion. Le HDR, les sous-titres ASS complexes, les sorties audio multicanales, les configurations multi-écrans et les bibliothèques très volumineuses nécessitent des essais dédiés. La sélection de versions multiples d’un même film, les collections éditables et les profils avancés de rendu mpv sont également à venir.

La distribution ne contient pas libmpv : il est téléchargé depuis son distributeur à la demande. Les notices des composants inclus sont dans [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md). La signature de l’exécutable reste à prévoir.

TorLink a été vérifié avec sa version 1.1.1 et des téléchargements terminés synthétiques, dans un état et une bibliothèque isolés ; aucun vrai téléchargement n’a été rangé pendant les essais. Les dossiers de bibliothèque sont lus sur Jellyfin 12.1.0 ; le signalement à Jellyfin et le bouton « Voir » ont été vérifiés avec un serveur simulé. Avec Jellyfin dans Docker ou sur un autre PC, les dossiers se choisissent à la main et l’ajout peut attendre la prochaine analyse de la bibliothèque.

Le logo et les icônes actuels sont dessinés dans `Views/Icon.cs`, sans dépendance externe. Les SVG Phosphor de l’ancienne interface restent archivés sous licence MIT dans `Assets/Phosphor`.

La police [Nunito Sans](https://github.com/googlefonts/NunitoSans) est embarquée sous SIL Open Font License 1.1. Les sources, révisions et notices sont conservées dans `Assets/Fonts` et reproduites par `tools/import-font.ps1`.
