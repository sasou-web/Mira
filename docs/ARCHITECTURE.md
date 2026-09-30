# Architecture de Mira 0.5.2

## Découpage

```text
Mira.Desktop (WPF)
  ├─ MainWindow : navigation, bibliothèque et écrans
  │   ├─ Catalog : requêtes filtrées, accueil, cartes et adaptation à la fenêtre
  │   ├─ Details : fiches, saisons et épisodes
  │   ├─ Carousel : progression et fondu du bandeau d’accueil
  │   ├─ Preview : survol intégré suivi pendant le défilement et préchargement
  │   ├─ Recency : activité récente et mise en tête de la reprise après lecture
  │   ├─ PlayerControls : barre de lecture, masquage, raccourcis, volume et menus ancrés à leur bouton
  │   ├─ MiniPlayer : glisser, aimantation aux coins, rangement derrière la languette
  │   ├─ PlayerSegments : chapitres, passages à sauter et épisode suivant
  │   ├─ Settings : préférences et sections de réglages
  │   ├─ TorLink : page TorLink, surveillance des téléchargements terminés, liste « Vers Jellyfin »
  ├─ Themes/Cinema.xaml : composants, couleurs, focus et contrôles sombres
  ├─ Views : famille vectorielle Mira, export du logo, cartes, transitions et défilement progressif
  ├─ Playback/MpvEngine : API C libmpv, rendu dans un HWND enfant
  ├─ Playback/WindowFrame : coins, contour DWM et marges de fenêtre agrandie
  ├─ Playback/FullscreenWindow : limites du moniteur et restauration de fenêtre
  ├─ Playback/PlayerOverlay : couche native de commandes et réception de la souris
  ├─ Playback/VideoHost : surface native de la vidéo
  ├─ TorLink : pseudo-console Windows, installation TorLink, terminal WebView2 + xterm.js (Assets/TorLink, intégrés en ressources)
  └─ Services : images, partage des requêtes de métadonnées, session DPAPI, préférences, mises à jour (Updater, UpdateApplier)
          │
Mira.Core
  ├─ JellyfinClient : requêtes HTTP, métadonnées, lecture et WebSocket
  ├─ ServerAddress : adresse saisie → adresses candidates, informations publiques et version minimale
  ├─ LibraryStore : cache SQLite, progression locale et file persistante
  ├─ DiskCache : budget des fichiers d’images, les moins récemment utilisés d’abord
  ├─ MotionState : interpolation du défilement et durée du carrousel, testables sans UI
  ├─ ContinueWatching : classement de la reprise par activité, une carte par série
  ├─ PlaybackMarkers : segments Jellyfin, titres de chapitres et passage actif
  ├─ ArtworkPalette : couleur dominante lisible du bandeau
  ├─ SyncService : livraison ordonnée, coalescence et reprise après erreur
  ├─ TorLink : état TorLink, lecture des .torrent, noms de sortie, plan de rangement, importeur et journal
```

Une application native C# / WPF permet de compiler et de livrer immédiatement sur le PC Windows équipé de .NET. libmpv est appelé directement par son API C ; aucun terminal, processus mpv externe ou lecteur HTML n’est utilisé pour la vidéo. Le rendu dispose de sa propre surface native. Une surface HwndSource transparente, possédée par la fenêtre et non activable, superpose les commandes au HWND vidéo. Elle reste ouverte lorsque les panneaux s'effacent : le fond d'alpha 1/255 conserve la réception de la souris, et WPF gère son curseur. Elle se ferme à la désactivation et à l'arrêt. Elle n'utilise pas Popup pour cette grande surface : WPF limite les Popup à 75 % de l'aire du moniteur. Seuls les menus de réglages restent des Popup. La source est créée masquée, puis montrée sans activation pour éviter un transfert de focus et une boucle de fermeture/réouverture.

## Intégration Windows

`WindowsIdentity` attribue l’AppUserModelID stable `Mira.Desktop` au processus, à la fenêtre et aux raccourcis. Le logo ICO comporte sept tailles (16 à 256 px) ; une copie avec un chemin propre sert à Explorer et à l’icône de notification. Le raccourci Démarrer et les métadonnées Shell sont enregistrés pour l’utilisateur courant. Aucun service, démarrage automatique ou changement de préférence Windows n’est ajouté. `--register-windows --shortcut <chemin>` permet au script de publication de créer aussi le raccourci du dossier.

`WindowsIntegration` gère les quatre `ThumbButtonInfo` WPF, un `NotifyIcon` et la session `SystemMediaTransportControls` attachée au HWND principal par `SystemMediaTransportControlsInterop.GetForWindow`. Les commandes sont dispatchées vers les mêmes méthodes que l’interface mpv, avec pause/reprise idempotentes, vérification de disponibilité et rejet des commandes d’une ancienne lecture. L’état de pause et le titre sont publiés immédiatement ; la timeline est actualisée au plus une fois par seconde hors saut, changement de durée ou vitesse. L’affiche provient du cache authentifié et est fournie à Windows par un flux mémoire, sans URL privée ni jeton.

Les boutons disparaissent après l’arrêt ; la session multimédia est désactivée et l’icône de notification supprimée à la fermeture. Les échecs COM de la session ne coupent pas la vidéo. Les profils de validation ont des identités séparées et ne créent aucun raccourci dans Démarrer. `InstanceActivation` utilise un pipe nommé limité au même utilisateur Windows ; une seconde ouverture accorde la mise au premier plan au processus existant puis lui demande de restaurer la fenêtre.

Références : [interop WinRT pour les applications .NET de bureau](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-com-interop-csharp), [identité AppUserModelID](https://learn.microsoft.com/en-us/windows/win32/shell/appids), [demande de déplacement dans la lecture](https://learn.microsoft.com/en-us/uwp/api/windows.media.systemmediatransportcontrols.playbackpositionchangerequested).

## Synchronisation

1. Le lecteur émet un démarrage seulement lorsque mpv confirme le chargement du fichier.
2. La progression est écrite dans une transaction SQLite avant tout envoi réseau.
3. Une seule boucle envoie les événements, dans l’ordre démarrage → progression → arrêt.
4. Les progressions consécutives en attente d’une même session sont remplacées par la plus récente. Les événements de démarrage et d’arrêt sont conservés.
5. Chaque remplacement reçoit un nouvel identifiant : une réponse ancienne ne peut pas effacer une mise à jour récente.
6. Une panne ou une expiration de session laisse les événements sur disque. Les reprises sont effectuées au lancement et périodiquement. Un jeton expiré nécessite une reconnexion.

La position locale récente est appliquée aux métadonnées provenant du serveur pour éviter un retour visuel en arrière juste après la lecture. Elle reste prioritaire si des envois sont en attente. Lorsque les envois sont terminés, cette priorité expire après une minute afin de laisser les changements effectués par les autres clients apparaître.

`LastPlayedDate` est conservé dans les données utilisateur. `ContinueWatching` déduplique par série, privilégie une véritable reprise et trie selon la dernière activité du film ou de la série. Un historique local de 64 lectures maintient aussi la date de la série lorsqu’un épisode terminé cède sa place au suivant. `LibraryStore.RememberPlayback` enregistre cet historique et la reprise ; une réponse serveur retardée ne les remplace pas pendant la minute de grâce ou tant que les envois restent en attente. Une date serveur plus récente reste prioritaire. L’historique de lectures terminées avant cette version n’est pas reconstitué.

Une commande locale `complete` écrit atomiquement l’arrêt puis un événement `watched` dans la file, et garde le statut terminé dans une table `completed`. Le marquage utilise `UserPlayedItems` après `Sessions/Playing/Stopped`. Une erreur du second appel ne renvoie pas le premier. Les sorties par générique et compte à rebours passent explicitement ce signal, même avant 90 % ; une fin naturelle ne relit pas une ancienne position native. Le cache d’épisodes est invalidé, la reprise retirée et un ancien résultat NextUp encore en transit est filtré localement.

Les écritures de la file passent par une chaîne de tâches du pool de threads, dans l’ordre des appels : l’interface n’attend jamais SQLite, et un « stop » ne peut pas précéder la dernière progression. Le nombre d’envois en attente est mis en cache après chaque écriture ou livraison. Une réponse 4xx définitive (hors 401, 403, 408 et 429) écarte le rapport concerné. La boucle de reprise survit à une erreur de stockage. À l’arrêt du service, la file dispose de 2 secondes pour se vider avant l’annulation des requêtes.

La livraison est **au moins une fois** : si le serveur accepte un envoi et que la réponse se perd, il peut recevoir à nouveau ce rapport. L’ordre est conservé, mais le protocole Jellyfin ne fournit pas de clé d’idempotence pour ces appels.

## Réactivité

- Requêtes asynchrones, recherche avec délai de 250 ms et annulation de la recherche précédente.
- Cache local affiché au lancement, puis actualisé.
- Téléchargements d’images limités à six simultanément ; décodage hors du thread UI.
- Pagination explicite : au plus 60 nouveaux titres par chargement.
- Les critères sont envoyés au serveur et inclus dans la clé du cache ; ils s’appliquent au catalogue complet.
- Les facettes utilisent Filters2 avec récursion et Years. L’ancien endpoint Filters ne traverse pas les bibliothèques enfants de la racine utilisateur.
- Images adaptées à leur usage : affiches 500 px, cartes panoramiques 720 px, fonds 2560 px et logos 900 px.
- Les événements WebSocket sont regroupés avant de demander une actualisation ; la lecture en cours ne déclenche pas de reconstruction de bibliothèque.
- Les cartes inchangées conservent leur instance et leur image après actualisation. La position du défilement en cours reste intacte.
- Les métadonnées au survol et à l’ouverture partagent les requêtes en cours via un cache de deux minutes, attaché au client connecté. Les erreurs ne restent pas en cache ; les notifications Jellyfin invalident les métadonnées.
- Les fiches présentent immédiatement le titre déjà connu, puis chargent en parallèle image, métadonnées, épisodes et titres similaires (`Items/{id}/Similar`, facultatif : une erreur laisse la fiche complète). Un numéro de requête empêche une ancienne fiche d’écraser la suivante. La distribution vient de `People`, que seule la requête d’un titre renvoie.
- Les images décodées sont partagées par clé et gardées au plus 512 Mo (largeur × hauteur × octets par pixel), ramenées à 384 Mo en libérant les moins récemment demandées ; les cartes affichées gardent leur propre référence. Sur disque, le dossier `images` de tous les comptes est ramené à 768 Mo au-delà de 1 Go à chaque connexion, hors du fil de l’interface, selon la date de dernière écriture : un fichier relu est « touché » au plus une fois par jour. Un fichier supprimé entre-temps est simplement retéléchargé ; un fichier ouvert est laissé. Les `.tmp` de plus d’une heure partent aussi.
- Dans SQLite, les pages de catalogue (une par vue, bibliothèque, filtre et tri) non réécrites depuis 30 jours sont oubliées à l’ouverture, comme les positions locales déjà livrées. L’accueil, la reprise, l’historique de lecture et tout ce qui attend un envoi restent.

## Mouvement et navigation

La molette est traitée en phase de tunneling par le ScrollViewer de la page. Une rangée horizontale ne peut ainsi plus absorber le défilement vertical. Maj + molette conserve le déplacement horizontal. L’interpolation exponentielle est indépendante de la cadence des images ; elle accumule les impulsions et inverse immédiatement sa direction lorsque la molette change de sens. Le pilote écoute CompositionTarget.Rendering uniquement pendant un déplacement.

La durée d’une diapositive est de 6,5 secondes. Le carrousel partage l’horloge du rendu WPF et suspend sa durée pendant une interaction, l’affichage d’un autre écran, la lecture, ou l’inactivité de la fenêtre. Il prépare le prochain fond avant le fondu. La sélection manuelle remet la durée à zéro. La réduction des animations arrête la rotation et applique les défilements directement.

Le survol reste contenu dans l’affiche : anneau clair et zoom de 2,5 %, sans légende, ou bouton de reprise au centre d’une carte panoramique. Une propriété animée unique pilote le zoom et l’opacité pendant 160 ms, avec une courbe quadratique. Le remplacement de l’animation garde la valeur actuellement affichée jusqu’au premier tick du nouveau mouvement. Aucune temporisation ni requête réseau ne conditionne le survol ; seules les métadonnées sont préchargées après 180 ms d’intention stable. Chaque changement de défilement programme un hit test après disposition, à priorité de rendu : le survol suit la carte réellement sous le pointeur sans attendre la fin de l’inertie. Le focus clavier utilise le même anneau ; un focus obtenu par clic n’en laisse pas. Les images déjà en cache et les cartes actualisées ne rejouent pas une seconde animation d’apparition.

`CarouselProgress` dessine le fond et le remplissage en capsules arrondies, sans alignement forcé aux pixels et sans transformer le rayon des extrémités. L’horloge du carrousel invalide seulement le dessin, pas la disposition.

Les boutons partagent un gabarit avec une couche de survol teintée de leur propre couleur de texte (claire sur fond sombre, sombre sur fond blanc), animée en fondu. Seuls les boutons-icônes grossissent légèrement : un texte aligné sur les pixels scintille lorsqu'on le met à l'échelle. Le contour de focus passe par `FocusVisualStyle` et n'apparaît qu'à la navigation clavier. Les icônes sont centrées par alignement, sans marge intérieure supérieure à l'espace disponible. Les dimensions restent fixes. Les transitions de pages portent un numéro pour résister aux navigations rapides.

Les géométries Mira sont figées sur une grille de 24 unités, avec traits et jointures arrondis. Le monogramme utilise deux arches dans un carré adouci. `BrandAssets.Export` produit les PNG et ICO multi-tailles depuis exactement la même géométrie que le rail ; le SVG source est également conservé. Les ressources Phosphor ne sont plus utilisées par `Icon`.

## Lecteur et mise à l’échelle

Le lecteur occupe les deux lignes de la fenêtre, sans modifier la hauteur du bandeau lors du passage en plein écran. Les repositionnements de commandes sont regroupés au prochain rendu ; le même HWND vidéo et la même couche de commandes sont conservés entre les modes. Le chrome de fenêtre n'apparaît pas dans le mini-lecteur et disparaît avec les commandes en lecture agrandie. Le défilement de la bibliothèque ne peint aucun fond sur sa barre supérieure.

Le mode mini redimensionne la même surface vidéo à 384 × 216 unités WPF ; le moteur et la position ne changent pas. Le plein écran utilise les limites physiques du moniteur (barre des tâches incluse), enregistre le placement et les contraintes de taille, puis les restaure à la sortie. Le placement personnalisé des menus utilise les pixels physiques que WPF transmet au callback ; marges et hauteur des commandes sont converties selon le DPI actif.

Les raccourcis du lecteur sont lus en phase de tunneling (`PreviewKeyDown`) sur la fenêtre et sur la couche de commandes, avant le contrôle qui a le focus : un bouton cliqué ne rejoue plus son action sur Espace et un curseur ne capture plus les flèches. La barre de progression ne prend pas le focus. Les touches reçues par la fenêtre native mpv passent par `mira-input.conf` et des `script-message` vers les mêmes actions. Un numéro de session écarte les événements retardés d’un ancien lecteur.

La barre de progression (`Views/Timeline`) reçoit la position de mpv toutes les 100 ms, puis l’extrapole à chaque image selon la vitesse de lecture ; un relevé proche est fondu plutôt qu’appliqué d’un coup, et la position demandée par un saut est conservée tant que mpv rapporte encore l’ancienne. Le dessin n’est pas aligné sur les pixels, ce qui rend le mouvement continu. Toute la hauteur du contrôle est une zone de clic ; l’appui saute au point visé et capture la souris pour le glisser, avec des sauts « keyframes » limités à un toutes les 90 ms et un saut exact au relâchement.

Les chapitres découpent la ligne en segments séparés de 3 unités : chaque segment est découpé par un rectangle arrondi, puis rempli (reste, mémoire tampon, survol, lu) ; le bord lu reste droit à l’intérieur d’un segment. Deux chapitres trop proches pour un espace se fondent. Au survol, le segment visé s’épaissit plus que les autres et le curseur rond n’apparaît que pendant le survol ou le glisser. La barre du bas suit une grille unique : titre et ligne d’épisode (`PlayerText.Subtitle`, complétée par la liste des épisodes de la série déjà demandée pour l’épisode suivant), chapitre et temps, ligne de progression, puis deux groupes de boutons dont la rangée déborde des marges de la largeur de leur fond, pour aligner les glyphes sur le texte. Les ombres dégradées ne reçoivent pas les clics : un clic sous elles met en pause comme ailleurs sur la vidéo. Les deux menus partagent un seul `Popup`, placé au-dessus du bouton qui l’a ouvert et aligné sur son bord droit.

Les passages proviennent d’abord de `/MediaSegments/{id}` (Jellyfin 10.10+), puis des titres de chapitres du fichier lus par mpv, ou de Jellyfin à défaut. Les chapitres nommés « Opening », « OP », « Ending », « ED », « Générique de fin », « Preview »… deviennent des passages ; un « Générique » seul dépend de sa position et l’« avant-générique » n’est jamais sauté. Un passage d’ouverture, de récap ou d’aperçu de plus de 6 minutes est ignoré.

Chaque mise à jour d’une fenêtre transparente WPF recopie toute sa surface. Un mouvement de souris ne fait donc que noter l’activité : la découpe arrondie n’est reconstruite que si la taille ou le rayon change, et `SetWindowPos` n’est appelé que si le rectangle change. Les changements de disposition (taille, déplacement, mini-lecteur, plein écran) repositionnent eux-mêmes la couche. La barre de progression ne se redessine qu’au-delà de 0,4 pixel physique de déplacement.

La couche de commandes est une fenêtre à part : `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` la retire des captures sans toucher à la vidéo. Cette valeur n’est appliquée qu’à partir de Windows 10 2004 ; avant, elle noircirait les commandes.

Le mini-lecteur garde le même HWND vidéo. Ses coins sont arrondis par une région Win32 sur l’hôte vidéo (le découpage WPF n’atteint pas un HWND enfant) et l’ombre est dessinée par une bordure WPF placée derrière. Pendant un glisser, la fenêtre principale capture la souris : la couche de commandes peut se fermer sans interrompre le geste. Au relâchement, la position projetée selon la vitesse choisit le coin ; au-delà de 30 % hors du bord droit, le lecteur se range et laisse une languette. Les changements de compte arrêtent la lecture avant de libérer la synchronisation.

## Identité visuelle

La fenêtre est déclarée `SingleBorderWindow` : ce style de légende natif est la condition pour que Windows joue ses animations d’ouverture, de fermeture et de réduction. WindowChrome étend la zone client sur toute la fenêtre, donc aucune barre native n’apparaît. Le plein écran passe temporairement en `WindowStyle.None` pour rester une surface nue.

Sous Windows 11, DWM dessine le cadre arrondi, son contour discret et son ombre. WindowChrome conserve une extension de verre minimale pour laisser DWM composer ces bords ; la grande couche de commandes vidéo est découpée au même rayon. Le plein écran et la fenêtre maximisée retirent les arrondis. Les limites physiques de la zone de travail compensent la partie invisible du cadre de redimensionnement en fenêtre maximisée.

Nunito Sans est embarquée en quatre graisses, sans téléchargement au lancement. La palette générale est blanche sur noir. Le bandeau extrait sa couleur d’une miniature de 64 × 48 pixels : les pixels transparents, presque blancs/noirs ou neutres sont exclus, puis un histogramme pondéré choisit la famille chromatique dominante. La teinte est éclaircie pour les contrôles ; une image neutre conserve du blanc. Les résultats sont mémorisés par image et la couleur change par fondu.

## TorLink

TorLink reste un programme séparé, lancé sans modification depuis son dossier : `node\node.exe dist\cli.cjs`, ou le Node.js du PC à défaut. `TorLinkInstallation` prend le dossier choisi dans les réglages, sinon la cible des raccourcis `torlink*.lnk` du menu Démarrer et du Bureau, puis une installation npm globale ; il vérifie `package.json` (paquet `torlnk`) et `dist/cli.cjs`. Avant un lancement, les lignes de commande des processus `node` sont lues par `NtQueryInformationProcess` : si TorLink tourne déjà ailleurs, Mira n’en démarre pas un second, car les deux partageraient la même file et les mêmes fichiers.

**Terminal.** `PseudoConsole` crée une pseudo-console Windows (ConPTY) et démarre TorLink suspendu dans un Job Object `KILL_ON_JOB_CLOSE`, si bien qu’aucun processus TorLink ou Node lancé dans la page ne survit à Mira. Sans WebView2, « Ouvrir dans une fenêtre » lance TorLink à part, comme son raccourci, après la même vérification d’instance ; ce TorLink-là vit indépendamment de Mira. La sortie UTF-8 est lue sur un thread dédié, regroupée jusqu’au prochain passage du dispatcher, puis dessinée par xterm.js 6 dans `Assets/TorLink/terminal.html`. `WebView2CompositionControl` est composé par WPF, sans HWND superposé : mini-lecteur, notifications, fiche et réglages passent au-dessus du terminal. La page n’envoie que quatre messages validés : prête, taille (20 à 500 colonnes, 5 à 300 lignes), saisie (64 Kio au plus) et retour. Les touches que WebView2 transmet à WPF restent à TorLink, Échap et flèches comprises, sauf Alt + ← et la touche « précédent ». La molette devient des flèches, comme TorLink l’attend. À la fermeture, Ctrl + C est envoyé, puis le Job est terminé après 3 secondes. TorLink quitte avec le code 0 quand on le lui demande ; un autre code affiche dans la page la dernière ligne qu’il a écrite, par exemple une version de Node.js trop ancienne.

**Isolation de la page.** Ses fichiers sont des ressources intégrées à Mira : `WebResourceRequested` répond à chaque requête vers un nom d’hôte réservé (`https://torlink.mira.example/`), avant tout accès réseau, et renvoie 404 pour le reste. La page a une CSP `default-src 'none'` : ni réseau, ni cadre, ni formulaire. Les autres navigations, nouvelles fenêtres, téléchargements et demandes d’autorisation sont refusés ; outils de développement, menus contextuels, zoom, remplissage automatique, accélérateurs du navigateur et services réseau d’arrière-plan sont désactivés. Le profil WebView2 est isolé dans `data/webview2`. Mira n’ouvre aucun port : il échange avec TorLink uniquement par la pseudo-console. TorLink hérite de l’environnement de Mira, qui ne contient aucun jeton Jellyfin.

**Rangement.** `TorLinkState` lit l’état de TorLink sans jamais l’écrire : `config.json` (dossier de téléchargement), `history.json` (téléchargements terminés), `queue.json` et les `.torrent` de `Data/torrents`. Ces lectures partagent l’écriture et la suppression : TorLink remplace ses fichiers en renommant une copie, et une lecture de Mira ne doit jamais faire échouer ce remplacement. La variable `TORLINK_STATE_DIR` est respectée. Un `FileSystemWatcher` sur `history.json`, regroupé sur 1,2 s et doublé d’un contrôle toutes les 20 s, déclenche `TorLinkImporter` sur le pool de threads, une passe à la fois.

1. La liste exacte des fichiers vient du `.torrent`, avec les chemins reconstruits comme WebTorrent (`fs-chunk-store`) ; à défaut, du dossier téléchargé. Un fichier absent ou de taille différente fait attendre, jusqu’à 6 essais.
2. `ReleaseName` et `MediaPlanner` déduisent le type (source TorLink, dossier existant, nom de fansub, numérotation absolue), le titre, l’année, la saison et l’épisode. Les dossiers existants sont réutilisés par clé normalisée et année ; les noms sont rendus valides sous Windows. Jeux et téléchargements sans vidéo sont ignorés.
3. `MediaImporter` n’accepte que des destinations sous la racine de la bibliothèque et des extensions vidéo ou sous-titres. Il crée un lien physique (`CreateHardLinkW`) sur le même volume, sinon copie vers un fichier `.mira-part` renommé à la fin ; le déplacement est facultatif. Un fichier existant n’est jamais remplacé : de même taille, il compte comme déjà présent ; sinon, c’est un conflit. Un fichier présent qui n’est ni suivi ni un lien physique du téléchargement est noté « trouvé » : « Classer » ne le déplace jamais. L’élagage des dossiers vidés s’arrête à la première racine de bibliothèque, qui n’est jamais supprimée, même imbriquée dans une autre.
4. `data/torlink-imports.json` garde la date d’activation, puis l’état, les chemins et le type choisi par « Classer » pour chaque téléchargement (500 au plus), sans lien magnet. Seuls les téléchargements terminés après cette date sont rangés automatiquement. Une nouvelle tentative garde le type choisi et les fichiers déjà rangés, y compris ceux qu’un déplacement a sortis du dossier de TorLink ; une passe interrompue par la fermeture enregistre ce qui est fait. Un journal illisible est mis de côté (`.bad-…`) avant d’en commencer un autre.

**Jellyfin.** Les dossiers proviennent de `GET /Library/VirtualFolders` : la première bibliothèque de films, une bibliothèque de séries dont le nom ou le dossier contient le mot « anime », « animes » ou « animés » (pas « Animation » ni « Dessins animés ») pour les animes, l’autre pour les séries. Ils ne sont retenus que s’ils existent sur ce PC, et les dossiers choisis dans les réglages passent avant. Cet appel demande un compte administrateur ; un refus 401/403 ne ferme pas la session. Après un rangement, `POST /Library/Media/Updated` signale les dossiers créés, et ceux quittés après un reclassement ; la surveillance de Jellyfin les trouverait aussi, après son délai. La disponibilité est vérifiée toutes les 20 s et à chaque changement de bibliothèque reçu par WebSocket, pendant 6 heures : une seule requête `Items?fields=Path` pour tous les titres en attente, parmi les 100 derniers films et épisodes ajoutés, avec comparaison exacte des chemins. `Library/Media/Updated` n’exige qu’un compte connecté (vérifié dans la description OpenAPI de Jellyfin 12.1.0).

## Session et fichiers

Avant l’authentification, `ServerAddress` transforme l’adresse saisie en candidates : une adresse avec son schéma est seule candidate ; sans schéma, HTTPS puis HTTP en premier selon le port (443 et 8920 : HTTPS), ou, sans port, HTTPS, `http://…:8096` puis HTTP. La partie `/web…`, la requête et le fragment d’une adresse copiée depuis la page web sont retirés. Toutes sont interrogées en même temps sur `System/Info/Public` (6 s au plus) ; la première dans l’ordre qui répond comme Jellyfin (identifiant présent, produit Jellyfin) est retenue, et une candidate préférée encore en attente n’a plus que 1,5 s une fois qu’une autre a répondu. L’adresse finale d’une redirection (HTTP vers HTTPS) est conservée. Une version antérieure à 10.9 est refusée : Mira utilise `UserItems`, `UserViews`, `UserPlayedItems` et `UserFavoriteItems`. Aucun identifiant n’est envoyé pendant cette recherche.

Le mot de passe ne sert qu’à l’authentification. Le jeton est chiffré par DPAPI pour l’utilisateur Windows courant. Les requêtes multimédias utilisent un en-tête d’authentification. Les caches et files sont isolés par serveur et identifiant de compte.

La lecture directe d’un chemin local est utilisée uniquement si le serveur est une adresse de boucle locale, si Jellyfin décrit la source comme un fichier et si ce fichier est accessible. Sinon, le lecteur utilise le flux original fourni par Jellyfin. La conversion vidéo n’est pas implémentée dans cette version.

Les choix de pistes, paramètres avancés et sources multiples devront à terme être réunis dans un contrôleur de lecture distinct des événements de fenêtre. Les migrations de base et une abstraction de vues plus complète font partie des étapes suivantes avant une diffusion large.

## Distribution

`tools/package.ps1` produit trois formes de la même application, à partir d’une publication autonome win-x64 (runtime .NET inclus), chacune avec son empreinte SHA-256 :

- `Mira-<version>-win-x64.zip` : le dossier de l’application, avec notices et licences.
- `Mira-<version>-win-x64-portable.exe` : une publication en un seul fichier (`PublishSingleFile`, assemblages compressés, bibliothèques natives extraites par .NET au premier lancement). Rien n’y dépend de fichiers voisins : la page TorLink vient des ressources, et `AppFiles` écrit dans `data/app` les liaisons clavier de mpv et l’icône Windows intégrées. Le dossier `data` reste à côté de l’exécutable.
- `Mira-<version>-win-x64-setup.exe` : l’installateur Inno Setup 6 (`installer/Mira.iss`) du dossier de l’application, pour l’utilisateur courant et sans droits administrateur, dans `%LOCALAPPDATA%\Programs\Mira`. Mira y écrit lui-même son raccourci Démarrer (`--register-windows`). Une mise à jour ferme Mira par le Restart Manager et garde `data` ; la désinstallation retire les raccourcis et l’identité Windows seulement s’ils désignent cette installation, et demande avant de supprimer `data`.

Aucun de ces fichiers n’est encore signé. Quand la clé de signature des mises à jour est sur le PC, `package.ps1` écrit aussi `mira-update.json` (version, nom, taille et SHA-256 de chaque fichier) et sa signature `mira-update.json.sig`, par `tools/Mira.Release`.

## Mises à jour

`Updater` (Mira.Desktop) reconnaît la forme de la copie : installateur si `unins*.exe` et `unins*.dat` d’Inno Setup sont à côté de `Mira.exe`, exécutable portable si `Mira.dll` n’y est pas (publication en un seul fichier), dossier sinon ; une sortie `bin\Debug` ou `bin\Release` n’est jamais mise à jour, ni un profil de validation, sauf flux de test local. Vingt secondes après l’ouverture puis toutes les 6 heures (1 heure après un échec), `UpdateClient` (Mira.Core) lit `GET /repos/sasou-web/Mira/releases`, écarte les brouillons et les balises autres que `vX.Y.Z`, et prend la plus haute version supérieure à la sienne qui publie `mira-update.json`. Le manifeste et sa signature (64 octets, ECDSA P-256 sur SHA-256, en base64) sont téléchargés avec une limite de taille, puis vérifiés avec les clés de `UpdateKeys.Trusted` ; la version du manifeste doit être celle de la release, et le fichier de la forme de la copie doit y figurer avec la même taille que dans la release. Toutes les requêtes passent en HTTPS vers `api.github.com`, les téléchargements de release de `github.com` et les serveurs de fichiers `release-assets.githubusercontent.com` et `objects.githubusercontent.com` : les redirections sont suivies à la main, chacune vérifiée avant d’être contactée. Un JSON d’une forme inattendue devient une erreur de mise à jour, jamais une exception de l’application. Le client ne revient pas sur le fil de l’interface pendant le téléchargement ; il écrit un `.partial`, calcule le SHA-256 au fil de l’eau, abandonne un transfert muet pendant 60 s et reprend un `.partial` interrompu par une requête `Range`.

Le fichier attend dans `data/updates/<version>`, seule version gardée ; l’archive y est décompressée une fois (`app/Mira`), et la version de l’exécutable ou de l’installateur doit être celle annoncée. À la fermeture de Mira, après l’arrêt de la lecture, de TorLink et de la synchronisation, ou tout de suite avec **Redémarrer** (vérification faite avant de fermer, hors du fil de l’interface), `Apply` vérifie de nouveau le téléchargement (pour une archive : le zip, puis chaque fichier décompressé par sa taille), note la tentative dans `data/updates/pending.json`, puis :

- installateur : lance `setup.exe /VERYSILENT` (ou `/SILENT` avec redémarrage) `/SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS`, qui reprend le dossier de l’installation et tient le `SetupMutex` `MiraSetup`. Avec `/RELAUNCH=1`, `DeinitializeSetup` de `installer/Mira.iss` rouvre Mira quand Setup se termine, installé ou non (Inno Setup restaure les fichiers d’un Setup en échec ou annulé) ;
- portable et dossier : lance la nouvelle version avec `--apply-update portable|folder --target … --wait <pid> --from <version> --version <version>`. `UpdateApplier` n’ouvre ni fenêtre ni profil ; il tient le verrou `Local\Mira-Update-<clé du profil>`, refuse une cible qui n’est pas Mira, attend la fin de l’ancien processus, puis remplace l’exécutable par `File.Replace` (sans marque de téléchargement), ou met à jour le dossier en deux temps : chaque nouveau fichier est d’abord copié à côté de sa destination (`.mira-new`), sans toucher à la copie installée, puis chaque ancien fichier part dans `data/updates/rollback-…` et le nouveau prend son nom. Au premier échec, les anciens reviennent (avec les mêmes nouvelles tentatives) ; ceux qui ne le peuvent pas sont listés dans `INCOMPLETE.txt` et la copie de retour arrière est gardée. Le dossier `data` n’est jamais copié ni modifié. Avec `--relaunch`, et seulement pour une cible reconnue, Mira rouvre avec `--updated-from`.

Au démarrage, Mira attend (30 s au plus) la fin d’un de ces verrous avant de prendre son verrou d’instance, puis lit `pending.json` : version atteinte, l’enregistrement disparaît et Mira annonce la nouvelle version ; sinon l’échec est annoncé une fois et compté. Après deux échecs d’une même version, elle n’est plus installée à la fermeture, seulement par **Redémarrer**. Si « Redémarrer » ne peut rien lancer, Mira se relance lui-même (`--after <pid>`). Aucune installation ne démarre pendant l’arrêt de Windows. La nouvelle version efface ensuite les téléchargements des versions installées, les copies de retour arrière complètes et les `.mira-new` d’une mise à jour interrompue. `tools/update-check.ps1` rejoue ces trois chemins de bout en bout avec un flux local (`Mira.Tests --update-server`) et une clé d’essai, passée par `--update-key` : les options `--update-feed` et `--update-key` ne sont lues qu’avec un profil `--data` et une adresse de boucle locale.
