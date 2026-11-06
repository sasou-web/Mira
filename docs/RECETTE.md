# Recette sur vrais appareils

Ce que la CI ne peut pas prouver : chaque case est une fonction qui n'a jamais tourné sur un vrai appareil. Coche quand tu l'as vue marcher ; si ça rate, dis-le au fil avec une capture (et, dès qu'il existe, le rapport de Réglages → « Exporter un rapport »). Chaque échec devient une PR. Pas besoin de tout faire d'un coup.

Légende : 🖥 PC d'ogsas · 🗄 Latitude (PC de Jellyfin) · 📱 iPhone · 🍎 Mac de Rayro · 👥 avec Dafty Cute

## PC de Jellyfin (Latitude) 🗄
- [ ] « Tout préparer » : une seule autorisation de Windows, puis toutes les étapes de la carte du téléphone cochées
- [ ] Après « Tout préparer » : le téléphone joint Jellyfin à la maison (Wi-Fi) et en 4G par Tailscale
- [ ] Avis « Jellyfin convertit maintenant avec ta … » (carte Intel), puis une conversion qui ne saccade pas
- [ ] Sauvegarde de Jellyfin faite toute seule (Réglages → Entretien : date de la dernière)
- [ ] Mise à jour de Jellyfin au clic (quand une version sort) : service revenu, Jellyfin répond
- [ ] Téléchargements : un magnet public par TorLink, rangé dans la bibliothèque
- [ ] Un `.torrent` de C411 par qBittorrent, rangé (lien physique), qBittorrent continue de partager
- [ ] Téléchargement qui atterrit sur le disque USB de 6 To monté dans `MEDIAS` (dès qu'il est là)
- [ ] Suivre une série : l'épisode suivant arrive tout seul (laisser tourner une semaine)
- [ ] Sous-titres automatiques activés : un film sans sous-titres en reçoit, bien calés
- [ ] Épisodes à venir (TheTVDB) activés : une ligne « manquant » ou « sort … » sur une série
- [ ] « Chercher une meilleure version » : l'ancien fichier part à la Corbeille, le titre garde sa progression

## PC d'ogsas 🖥
- [ ] Mise à jour de Mira proposée et faite (à la prochaine version)
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
- [ ] Mode canapé sur la télé (HDMI) : Réglages → Apparence → Mode canapé, plein écran lisible depuis le canapé ; flèches partout (rail compris), Entrée, Retour arrière ; une manette Xbox (A, B, Y, LB/RB, dans le lecteur A, X, LB/RB) ; la télécommande de la télé si un adaptateur HDMI-CEC est là
- [ ] Bandes-annonces : un film avec un dossier `trailers` (ou `…-trailer.mkv`) : bouton « Bande-annonce » de la fiche, lue dans Mira ; un film sans : le bouton dit « sur YouTube, dans ton navigateur » et ouvre le navigateur ; Réglages → Lecture → « Bande-annonce avant le film » : elle passe d'abord, « Passer la bande-annonce » mène au film, rien en reprise
- [ ] Télécommande : depuis l'iPhone, « … » d'un film → « Lire sur… » → ce PC : Mira s'ouvre et le lit (fenêtre réduite ou dans la zone de notification comprise) ; la télécommande de l'iPhone met en pause, ±10 s, volume, muet, Arrêter ; Jellyfin web (« Lire sur ») aussi ; Réglages → En ce moment sur Jellyfin d'un autre appareil : « Arrêter » arrête ce PC

## iPhone 📱
- [ ] Installation par AltStore, rafraîchie après 7 jours sans rien faire
- [ ] QR code de « Mira sur ton iPhone » (Guide de Mira pour Windows, ou Réglages → serveur sur Mac) scanné par l'appareil photo : Mira s'ouvre, serveur rempli, puis code ou mot de passe ; ensuite Jellyfin joint à la maison et en 4G
- [ ] Ouverture : le « m » puis l'Accueil, sans écran blanc
- [ ] Un film et un épisode lus du début à la fin ; épisode suivant tout seul
- [ ] Page d'une personne : sur la fiche d'un film, « Distribution et équipe » (réalisation d'abord, photos), toucher un acteur ouvre sa page (photo, naissance, biographie, ses films et séries de ta bibliothèque) ; Recherche trouve aussi un acteur par son nom
- [ ] « Tu regardes toujours ? » : 3 épisodes courts enchaînés sans toucher l'iPhone, le 4e ne part pas et la carte le demande ; « Continuer » le lance (même essai sur le PC d'ogsas et le Mac de Rayro, sans toucher clavier, souris ni manette)
- [ ] Lecteur : double toucher ±10 s, glisser luminosité et volume, pincer, réduire vers le bas
- [ ] PiP d'Apple et AirPlay vers une télé
- [ ] Sous-titres d'un vrai animé (ASS) lisibles et calés
- [ ] Un film Dolby Vision profil 8 (Jellyfin 12.2 ou plus) puis HDR10 : lus sans conversion, couleurs justes
- [ ] Tailscale intégré en 4G (sans l'app Tailscale) : lecture, puis arrière-plan et retour
- [ ] Données mobiles : un film en 4G, ⋮ → Qualité dit « Données mobiles · 2 Mb/s » et l'image reste correcte ; quitter le Wi-Fi en pleine lecture : le film reprend au même endroit en 2 Mb/s, puis revient en qualité normale au retour du Wi-Fi
- [ ] Épisodes manquants et à venir (extension TheTVDB installée par Mira pour Windows) : sur une série suivie, la ligne au-dessus des saisons (« 1 épisode manquant : … » ou « Prochain épisode : …, samedi »), touchée = la liste avec les jours
- [ ] Collections : « … » d'un film → « Ajouter à une collection… » : coché dans une collection existante, puis une nouvelle nommée avec lui ; vues dans Mira pour Windows ; même chose par l'appui long sur une affiche
- [ ] Bandes-annonces : « … » → « Bande-annonce » d'un film qui en a une sur le serveur, lue dans Mira ; « Bande-annonce sur YouTube » ouvre Safari
- [ ] Ouverture avec le PC de Jellyfin éteint : message clair, puis l'Accueil quand il revient
- [ ] Un titre qui échouait avant : noter l'erreur et les infos du média
- [ ] « Garder sur cet iPhone » : un film MP4 et un MKV (converti), puis une saison ; Mira en arrière-plan pendant la copie (progression d'iOS, reprise au retour) ; en mode Avion, lus depuis Réglages → Sur cet iPhone et l'Accueil (pistes, sous-titres, épisode suivant, avance rapide dans le MKV converti) ; de retour à la maison, la reprise est la même sur le PC ; déconnexion = copies retirées

## Mac de Rayro 🍎
- [ ] Installation du `.dmg` (« Ouvrir quand même ») et premier lancement
- [ ] Mise à jour automatique vers la version suivante (« Redémarrer »)
- [ ] Image ni grise ni noire ; sinon ⋮ → Informations, ligne « Réparé »
- [ ] Trackpad : défiler (Accueil : un geste = la page ou la rangée), glisser à deux doigts entre les pages (la page suit les doigts, revient si lâchée tôt), pincer dans les grilles (affiches qui glissent), balayage à trois doigts, pincer dans le lecteur
- [ ] Image dans l'image (petite fenêtre flottante)
- [ ] Sortie audio : Réglages → Audio & sous-titres → « Tester » joue chaque haut-parleur (casque, puis enceintes ou AirPods) ; ⋮ → Informations dit la sortie ; Dolby Digital vers un ampli si Rayro en a un
- [ ] Mode canapé : plein écran agrandi, flèches et Retour arrière, une manette appairée (PlayStation ou Xbox) : A ouvre, B revient
- [ ] Tailscale intégré : connexion, trousseau gardé après une mise à jour
- [ ] Touches multimédias, Mira derrière une autre fenêtre pendant un épisode : F8 met en pause et reprend, F7 recule de 10 s, F9 passe à l'épisode suivant ; le bouton des AirPods met en pause ; centre de contrôle et barre des menus → « À l'écoute » : titre, affiche, barre qui avance, ±10 s ; le lecteur fermé, F8 revient à Musique
- [ ] Notifications : un nouvel épisode d'une série suivie (favori ou commencée) ajouté à Jellyfin, Mira ouvert ou dans le Dock : macOS demande d'abord « Mira souhaite envoyer des notifications » (Autoriser), puis la notification « Nouvel épisode » avec son image ; un clic ouvre la série ; dans un groupe Regarder ensemble, Mira derrière une autre fenêtre, « X a rejoint le groupe » ; refusé, plus rien ni plus de question
- [ ] Télécommande : depuis l'iPhone, « Lire sur… » → le Mac : le film part, pause, ±10 s, volume, Arrêter depuis la télécommande de l'iPhone
- [ ] Veille en plein film (depuis Jellyfin, pas une copie gardée) : capot refermé quelques minutes, puis rouvert : le film est en pause là où il était, Lecture le relance sans roue sans fin ; Wi-Fi coupé en pleine lecture : « Connexion au serveur perdue », puis le film reprend quand le Wi-Fi revient

## Regarder ensemble 👥
- [ ] Ta party par le lien (Funnel) : Dafty Cute rejoint depuis Windows sans compte
- [ ] Même chose depuis un iPhone, puis depuis le Mac
- [ ] Pause, saut et épisode suivant suivis par tout le monde
- [ ] Toi dans la party de Dafty Cute (son Jellyfin), depuis Mira pour Windows
- [ ] Lien refait : l'ancien ne marche plus
