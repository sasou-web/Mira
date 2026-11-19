# Recette sur vrais appareils

Ce que la CI ne peut pas prouver : chaque case est une fonction qui n'a jamais tourné sur un vrai appareil. Coche quand tu l'as vue marcher ; si ça rate, dis-le au fil avec une capture (et, dès qu'il existe, le rapport de Réglages → « Exporter un rapport »). Chaque échec devient une PR. Pas besoin de tout faire d'un coup.

Légende : 🖥 ton PC · 🗄 PC de Jellyfin · 📱 iPhone · 🍎 Mac d’un ami · 👥 avec Sam

## PC de Jellyfin 🗄
- [ ] « Tout préparer » : une seule autorisation de Windows, puis toutes les étapes de la carte du téléphone cochées
- [ ] Après « Tout préparer » : le téléphone joint Jellyfin à la maison (Wi-Fi) et en 4G par Tailscale
- [ ] Avis « Jellyfin convertit maintenant avec ta … » (carte Intel), puis une conversion qui ne saccade pas
- [ ] Sauvegarde de Jellyfin faite toute seule (Réglages → Serveur Jellyfin : date de la dernière)
- [ ] Mise à jour de Jellyfin au clic (quand une version sort) : service revenu, Jellyfin répond
- [ ] Redémarrer le PC (ou après une mise à jour de Windows) puis ouvrir la session : Mira revient seul près de l’horloge, aucune fenêtre au premier plan ni dans la barre des tâches ; un téléchargement demandé depuis l’iPhone part sans ouvrir Mira ; clic sur l’icône = fenêtre à sa place habituelle (agrandie si elle l’était) ; Réglages → Serveur Jellyfin dit « Prêt »
- [ ] Téléchargements : un magnet public par TorLink, rangé dans la bibliothèque
- [ ] Un pack de deux saisons numéroté d’un bout à l’autre (« (Season 1-2) [Batch] », « - 01 » à « - 37 ») : d’abord tout en saison 1, puis dans les 10 à 20 minutes qui suivent (TheTVDB activé) les épisodes de la saison 2 renommés dans `Season 02`, et la saison 2 n’est plus « manquante »
- [ ] Un `.torrent` de C411 par qBittorrent, rangé (lien physique), qBittorrent continue de partager
- [ ] qBittorrent et un disque USB : débrancher le disque de son dossier de téléchargement pendant un téléchargement, la ligne dit « disque sans doute débranché » (la file reste) ; le rebrancher, il reprend seul en moins d’une minute (page Téléchargements ouverte)
- [ ] La lecture passe avant qBittorrent : un gros téléchargement en cours sur le disque des films, lancer un film depuis un autre appareil, il démarre et ne s’arrête pas pour charger ; dans qBittorrent (icône près de l’horloge), la tortue des vitesses réduites s’allume pendant la lecture et s’éteint une minute après
- [ ] qBittorrent et son port pris (rare, dur à provoquer à la main) : dans le Gestionnaire des tâches, jamais de qBittorrent qui redémarre en boucle ; si la page Téléchargements dit une panne de qBittorrent, la noter telle quelle
- [ ] Téléchargement qui atterrit sur le disque USB de 6 To monté dans `MEDIAS` (dès qu'il est là)
- [ ] Suivre une série : l'épisode suivant arrive tout seul (laisser tourner une semaine)
- [ ] Sous-titres automatiques activés : un film sans sous-titres en reçoit, bien calés
- [ ] Épisodes à venir (TheTVDB) activés : sur une série, une ligne fine « É3–4 · pas dans ta bibliothèque » entre les épisodes, ou le prochain en ligne vide au bout de sa saison ; une saison absente dans la liste des saisons
- [ ] Métadonnées à côté des fichiers : quelques heures après l’ouverture de Mira, un `movie.nfo` et un `folder.jpg` dans le dossier d’un film, un `tvshow.nfo` dans celui d’une série ; un titre corrigé par « Identifier… » garde sa correction dans son NFO
- [ ] « Chercher une meilleure version » : l'ancien fichier part à la Corbeille, le titre garde sa progression
- [ ] Corbeille sur le PC de Jellyfin : un épisode (clic droit) avec ses sous-titres, une saison (« … » de la ligne Épisodes), sur un fichier de test ; la fenêtre dit ce qui part ; tout revient depuis la Corbeille de Windows ; sur le WD monté dans un dossier

## Ton PC 🖥
- [ ] Ouverture lente du lecteur (dès la 0.9.15) : un film lourd hors de chez toi (conversion) ; au bout d’une seconde, roue sur fond noir au lieu d’un écran noir ; Échap pendant l’attente ramène la fiche (pas l’Accueil) ; relancé tout de suite, il s’ouvre. Pendant le compte à rebours de fin d’épisode laissé aller, Échap pendant la préparation du suivant : le lecteur ne revient pas. Touche C sur un film converti avec sous-titres PGS : coupés puis revenus ; l’épisode suivant garde le choix.
- [ ] Bonus hors de « Continuer à regarder » (dès la 0.9.15) : lancer un générique de Monogatari (pastille Bonus), l’arrêter à mi-chemin ; il n’est pas dans « Continuer à regarder », ni pendant le mini-lecteur ni après avoir relancé Mira ; pareil pour une bande-annonce avant un film arrêtée en cours
- [ ] Mise à jour de Mira proposée et faite (à la prochaine version)
- [ ] Bonus d’une série (« La pastille ») : sur Monogatari Series, pastille « Extras » après la saison 14 ; un clic montre Openings et Endings, une ligne chacun, « Tout afficher » les déplie ; une vignette lance le générique ; un film garde sa rangée « Bonus » (Mac et iPhone pareil)
- [ ] Lecteur : image « Cinéma » sans saccade sur un film 4K ; Informations dit « Cinéma »
- [ ] Un film HDR10 avec le HDR de Windows allumé, puis éteint : couleurs justes les deux fois ; ⋮ → Informations, ligne « Couleurs » : « envoyé en HDR à l’écran » puis « converti en SDR »
- [ ] Un film Dolby Vision (profil 5 ou 8) : pas de vert ni de violet ; « Couleurs » dit « Dolby Vision profil 5 » ou « 8 »
- [ ] Sortie audio : « Tester » joue chaque haut-parleur ; passthrough vers un ampli si tu en as un
- [ ] Image dans l'image : la petite fenêtre reste au-dessus, sous-titres lisibles, retour sans image noire
- [ ] Réduire et agrandir le lecteur : fluide, pas de noir aux deux bouts
- [ ] Défilement des Films et changement de page : fluide à 240 Hz
- [ ] Narrateur : Accueil, fiche et lecteur lus avec des noms (pas « bouton »)
- [ ] Snap Layouts au survol du bouton agrandir
- [ ] Tailscale intégré (sans l'app Tailscale, depuis un autre réseau) : se connecter, lire un film, se déconnecter
- [ ] Mode canapé sur la télé (HDMI) : Réglages → Général → Mode canapé, plein écran lisible depuis le canapé ; flèches partout (rail compris), Entrée, Retour arrière ; une manette Xbox (A, B, Y, LB/RB, dans le lecteur A, X, LB/RB) ; la télécommande de la télé si un adaptateur HDMI-CEC est là
- [ ] Filtres de Films : Filtrer → Image 4K + Langue audio Français + Non vus : seulement tes films 4K pas vus avec une piste française ; « Filtres · 3 » ; Réinitialiser rend tout ; « Plus courts d'abord »
- [ ] Bandes-annonces : un film avec un dossier `trailers` (ou `…-trailer.mkv`) : bouton « Bande-annonce » de la fiche, lue dans Mira ; un film sans : le bouton dit « sur YouTube, dans ton navigateur » et ouvre le navigateur ; Réglages → Lecture → « Bande-annonce avant le film » : elle passe d'abord, « Passer la bande-annonce » mène au film, rien en reprise ; « Depuis le début » d'un film laissé en cours : la bande-annonce, puis le film depuis son début
- [ ] Menu de la zone de notification (clic droit sur l’icône de Mira) : pendant un épisode, la carte (image, titre, progression, boutons) au-dessus de l’icône ; pause, ±10 s, épisode suivant et arrêter répondent sans fermer le menu ; hors lecture, seulement « Ouvrir Mira », « Réduire… » et « Quitter Mira » ; Mira rangée, plus de « Réduire… » ; barre des tâches en haut ou sur un côté, deuxième écran à une autre échelle : le menu reste dans l’écran du clic ; Échap, un clic ailleurs, et la touche Menu (Maj+F10) sur l’icône
- [ ] Télécommande : depuis l'iPhone, « … » d'un film → « Lire sur… » → ce PC : Mira s'ouvre et le lit (fenêtre réduite ou dans la zone de notification comprise) ; la télécommande de l'iPhone met en pause, ±10 s, volume, muet, Arrêter ; Jellyfin web (« Lire sur ») aussi ; Réglages → En ce moment sur Jellyfin d'un autre appareil : « Arrêter » arrête ce PC
- [ ] Film en deux fichiers (`Film - cd1.mkv` et `Film - cd2.mkv` dans le même dossier) : la 2e partie suit la 1re sans fermer le lecteur, « Partie 2 sur 2 » sous le titre ; arrêté dans la 2e, « Lecture » de la fiche y reprend

## iPhone 📱
- [ ] Installation par AltStore, rafraîchie après 7 jours sans rien faire
- [ ] Filtres de Films : pastilles Image, Note, Audio, Sous-titres ; « 4K » + « Audio · Français » ensemble, puis Réinitialiser
- [ ] Le même film en deux fichiers : la 2e partie suit la 1re, « Partie 2 sur 2 » sous le titre, « Lecture » reprend dans la bonne partie
- [ ] QR code de « Mira sur ton iPhone » (Guide de Mira pour Windows, ou Réglages → serveur sur Mac) scanné par l'appareil photo : Mira s'ouvre, serveur rempli, puis code ou mot de passe ; ensuite Jellyfin joint à la maison et en 4G
- [ ] Ouverture : le « m » puis l'Accueil, sans écran blanc
- [ ] Un film et un épisode lus du début à la fin ; épisode suivant tout seul
- [ ] Page d'une personne : sur la fiche d'un film, « Distribution et équipe » (réalisation d'abord, photos), toucher un acteur ouvre sa page (photo, naissance, biographie, ses films et séries de ta bibliothèque) ; Recherche trouve aussi un acteur par son nom
- [ ] « Tu regardes toujours ? » : 3 épisodes courts enchaînés sans toucher l'iPhone, le 4e ne part pas et la carte le demande ; « Continuer » le lance (même essai sur ton PC et le Mac d’un ami, sans toucher clavier, souris ni manette)
- [ ] Lecteur : double toucher ±10 s, glisser luminosité et volume, pincer, réduire vers le bas
- [ ] PiP d'Apple et AirPlay vers une télé
- [ ] Sous-titres d'un vrai animé (ASS) lisibles et calés
- [ ] Un film Dolby Vision profil 8 (Jellyfin 12.2 ou plus) puis HDR10 : lus sans conversion, couleurs justes
- [ ] Tailscale intégré en 4G (sans l'app Tailscale) : lecture, puis arrière-plan et retour
- [ ] Données mobiles : un film en 4G, ⋮ → Qualité dit « Données mobiles · 2 Mb/s » et l'image reste correcte ; quitter le Wi-Fi en pleine lecture : le film reprend au même endroit en 2 Mb/s, puis revient en qualité normale au retour du Wi-Fi
- [ ] Épisodes manquants et à venir (extension TheTVDB installée par Mira pour Windows) : sur une série suivie, rien au-dessus des saisons ; les manquants en ligne fine à leur place (Voir = la liste avec les jours), le prochain à sortir en ligne vide avec son jour, une saison absente (« Saison 1 · absente ») = une carte ; Réglages → Lecture → « Montrer les épisodes manquants » coupé = seulement ceux à venir
- [ ] Épisodes numérotés depuis le début de la série (Shingeki no Kyojin sur le Dell, S02E26–S04E89) : plus de « E1–12 · Not in your library » ni de « Get E1–12 » sur Windows, Mac et iPhone ; sur le Dell, les fichiers rangés par Mira renommés en S02E01… (sous-titres compris), Jellyfin montre leurs noms et images
- [ ] Collections : « … » d'un film → « Ajouter à une collection… » : coché dans une collection existante, puis une nouvelle nommée avec lui ; vues dans Mira pour Windows ; même chose par l'appui long sur une affiche
- [ ] Bandes-annonces : « … » → « Bande-annonce » d'un film qui en a une sur le serveur, lue dans Mira ; « Bande-annonce sur YouTube » ouvre Safari
- [ ] Ouverture avec le PC de Jellyfin éteint : message clair, puis l'Accueil quand il revient
- [ ] Un titre qui échouait avant : noter l'erreur et les infos du média
- [ ] « Garder sur cet iPhone » : un film MP4 et un MKV (converti), puis une saison ; Mira en arrière-plan pendant la copie (progression d'iOS, reprise au retour) ; en mode Avion, lus depuis Réglages → Sur cet iPhone et l'Accueil (pistes, sous-titres, épisode suivant, avance rapide dans le MKV converti) ; de retour à la maison, la reprise est la même sur le PC ; déconnexion = copies retirées ; copie lancée en Wi-Fi puis Wi-Fi coupé (4G/5G) : « En attente du Wi-Fi », rien ne passe sur les données mobiles, reprise seule au retour du Wi-Fi ; « Garder avec les données mobiles » allumé : elle continue en 4G/5G

## Mac d’un ami 🍎
- [ ] Installation du `.dmg` (« Ouvrir quand même ») et premier lancement
- [ ] Mise à jour automatique vers la version suivante (« Redémarrer »)
- [ ] Image ni grise ni noire ; sinon ⋮ → Informations, ligne « Réparé »
- [ ] Un film HDR10 (ou Dolby Vision) : couleurs justes, pas délavées ; ⋮ → Informations, ligne « Couleurs » : « HDR10 → SDR (limite du rendu) »
- [ ] Trackpad : défiler (Accueil : un geste = la page ou la rangée), glisser à deux doigts entre les pages (la page suit les doigts, revient si lâchée tôt), pincer dans les grilles (affiches qui glissent), balayage à trois doigts, pincer dans le lecteur
- [ ] Image dans l'image (petite fenêtre flottante)
- [ ] Sortie audio : Réglages → Audio & sous-titres → « Tester » joue chaque haut-parleur (casque, puis enceintes ou AirPods) ; ⋮ → Informations dit la sortie ; Dolby Digital vers un ampli si Alex en a un
- [ ] Mode canapé : plein écran agrandi, flèches et Retour arrière, une manette appairée (PlayStation ou Xbox) : A ouvre, B revient
- [ ] Tailscale intégré : connexion, trousseau gardé après une mise à jour
- [ ] Touches multimédias, Mira derrière une autre fenêtre pendant un épisode : F8 met en pause et reprend, F7 recule de 10 s, F9 passe à l'épisode suivant ; le bouton des AirPods met en pause ; centre de contrôle et barre des menus → « À l'écoute » : titre, affiche, barre qui avance, ±10 s ; le lecteur fermé, F8 revient à Musique
- [ ] Notifications : un nouvel épisode d'une série suivie (favori ou commencée) ajouté à Jellyfin, Mira ouvert ou dans le Dock : macOS demande d'abord « Mira souhaite envoyer des notifications » (Autoriser), puis la notification « Nouvel épisode » avec son image ; un clic ouvre la série ; dans un groupe Regarder ensemble, Mira derrière une autre fenêtre, « X a rejoint le groupe » ; refusé, plus rien ni plus de question
- [ ] Télécommande : depuis l'iPhone, « Lire sur… » → le Mac : le film part, pause, ±10 s, volume, Arrêter depuis la télécommande de l'iPhone
- [ ] Pomme → Redémarrer avec Mira ouverte, puis Mira dans le Dock (fenêtre fermée par ⌘W), puis pendant un film (et en image dans l'image) : le Mac redémarre sans « Mira a interrompu le redémarrage » ; au retour, le film reprend là où il était (« Continuer à regarder »)
- [ ] ⌘Q le PC de Jellyfin éteint : Mira disparaît du Dock en 5 s au plus
- [ ] Mise à jour depuis le Dock : une nouvelle version prête (avis « elle s’installera… »), fenêtre fermée par ⌘W ; quelques minutes après, Mira se relance seule, cachée (le point sous l'icône reste) ; un clic sur l'icône montre la nouvelle version (« Nouveautés »)
- [ ] Veille en plein film (depuis Jellyfin, pas une copie gardée) : capot refermé quelques minutes, puis rouvert : le film est en pause là où il était, Lecture le relance sans roue sans fin (Jellyfin injoignable au réveil : « Lecture impossible » et « Réessayer », le lecteur reste) ; Wi-Fi coupé en pleine lecture : « Connexion au serveur perdue », puis le film reprend quand le Wi-Fi revient
- [ ] Photo du compte depuis une photo d’iPhone (HEIC, prise en portrait, par AirDrop ou Photos) : Réglages → Compte & serveur → « Choisir une photo… » ; elle apparaît droite (pas couchée) dans la barre, puis sur l’iPhone

## Regarder ensemble 👥
- [ ] Ta party par le lien (Funnel) : Sam rejoint depuis Windows sans compte
- [ ] Même chose depuis un iPhone, puis depuis le Mac
- [ ] Pause, saut et épisode suivant suivis par tout le monde
- [ ] Déjà en lecture, créer le groupe puis inviter : l'invité arrive à la même minute, sans relancer
- [ ] 20 min ensemble par Funnel sans pause ni lecture en boucle (sauts, changement de piste audio)
- [ ] Toi dans la party de Sam (son Jellyfin), depuis Mira pour Windows
- [ ] Lien refait : l'ancien ne marche plus
- [ ] Le foyer : réglé sur le Dell (mot de passe, deux profils dont un à code), rejoint depuis un autre appareil, profil ouvert, changé, appareil retiré (quand les clients Windows, Mac, iPhone seront là)
