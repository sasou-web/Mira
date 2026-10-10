# Historique

## Non publié

- **Mac : macOS 15 Sequoia ou plus récent.** Les Mac de GitHub sous macOS 14, qui construisent l’image disque, sont retirés le 2 novembre 2026, et les bibliothèques de mpv embarquées viennent de Homebrew, construites pour le macOS qui les compile. La 0.7.4 reste la dernière version pour macOS 14 Sonoma ; la construction refuse maintenant toute bibliothèque qui demanderait un macOS plus récent que celui annoncé.
- **SQLite 3.53.3** (Microsoft.Data.Sqlite 8.0.31) dans les apps Windows et Mac, à la place de SQLite 3.41.2 (2023), signalé vulnérable.
- **Construction** : une faille connue de gravité haute ou critique dans un paquet, même tiré par un autre, ou un avertissement du compilateur arrêtent la construction ; la mise en forme du code est vérifiée à chaque PR ; Dependabot propose chaque mois les mises à jour des paquets NuGet et des actions GitHub, une semaine après leur sortie au plus tôt ; les workflows ne gardent plus le jeton GitHub après avoir récupéré le code, et n’insèrent plus de valeurs d’un événement dans leurs commandes.
- **Contrôle de Mira web** (`tools/web/check.mjs`) : identifiants par défaut ceux de la CI.

## 0.7.4 — 2026-10-10

Mira sur le téléphone en un clic, après la page blanche chez un nouvel utilisateur : le guide de Mira pour Windows prépare tout et dit ce qui reste.

- **Mira sur ton téléphone, en un clic** (Mira pour Windows) : le guide réunit tout ce qu’il faut pour le téléphone dans une seule carte, au lieu de trois parties et cinq boutons à trouver dans le bon ordre.
  - Chaque étape est cochée quand elle est prête, numérotée sinon : Mira web sur Jellyfin, le pare-feu de Windows, Tailscale pour regarder hors de chez toi (facultatif), le démarrage avec Windows.
  - **Tout préparer** fait toutes les étapes possibles avec une seule autorisation de Windows, puis ouvre le QR code. Tailscale reste à installer soi-même ; de retour dans Mira, le guide se met à jour tout seul.
  - Ce que Mira ne peut pas changer devient une étape à toi, qui dit où agir : « Bloquer toutes les connexions entrantes » coché dans Windows, une règle de pare-feu qui bloque le port de Jellyfin (son nom est donné), un Jellyfin dont Mira ne trouve pas le programme (lancé par un autre compte, ou dans Docker). **Tout préparer** ne redemande jamais une autorisation pour rien.
  - Pendant que Jellyfin redémarre (après l’installation de Mira web), ou s’il est arrêté, la carte le dit et se met à jour dès qu’il répond.
  - Le QR code donne l’adresse Tailscale du PC quand elle est prête : elle marche chez toi comme ailleurs, sans recopier d’adresse. **Chez toi seulement** reste au choix sur la page du QR code.
  - L’adresse pour une TV ou une console (application Jellyfin) passe dans **Gérer Jellyfin**.
- **Page blanche sur le téléphone** : l’adresse du PC pouvait ne rien afficher, même la page de Jellyfin, chez toi comme avec Tailscale, alors que le guide disait que tout était prêt.
  - Un Jellyfin installé sans son service n’a que les règles de la fenêtre « Autoriser l’accès » de Windows, à son premier lancement : autorisé sur le type de réseau coché, bloqué sur les autres, et aucune règle si elle n’a pas été validée. Or Tailscale déclare son réseau privé, et la box est souvent comptée publique.
  - Le guide lit maintenant le pare-feu de Windows, sans droits d’administrateur, pour le Wi-Fi de la maison et pour Tailscale, chacun sur son type de réseau. **Tout préparer** retire les blocages de Jellyfin sur ces réseaux (ils restent sur les autres) et ajoute la règle de Mira, qui laisse entrer ton réseau et Tailscale, sur le port de Jellyfin seulement. Avec ou sans service.
- **« Serveur injoignable » dit pourquoi** (Mira web) : avec l’adresse Tailscale, vérifier que Tailscale est activé sur le téléphone ; avec celle de la maison, que le téléphone est sur son Wi-Fi.
## 0.7.3 — 2026-10-10

Mira web après le troisième essai sur iPhone (iOS 27) : les titres que le lecteur d’Apple refusait, et des onglets qui s’enchaînent sans à-coups.

- **Titres refusés** : certains affichaient « Cet appareil ne peut pas lire ce flux », et une qualité plus basse n’y changeait rien.
  - Jellyfin copie la vidéo tant que le fichier tient sous le débit choisi, et l’audio à toutes les qualités : baisser la qualité ne changeait donc pas ce que le lecteur recevait.
  - Mira redemande maintenant le titre à Jellyfin, la vidéo convertie, puis tout converti (H.264 et AAC stéréo), là où il en était, sans quitter le plein écran. Le message ne vient que si rien ne passe, avec l’erreur du lecteur en petit dessous.
  - Reproduit dans le vrai lecteur d’Apple, sur un Mac : un HEVC 10 bits HDR10 copié tel quel était refusé (« Media failed to decode ») ; il joue maintenant, converti.
  - Ce que Mira annonce à Jellyfin suit celui de Jellyfin pour Safari : HDR10 et HLG sur les appareils d’Apple, Dolby Vision seulement si l’appareil le lit (Jellyfin enlève sinon ce qu’il ne peut pas lire, ou convertit), vidéo entrelacée convertie et désentrelacée, niveau HEVC et images par seconde plafonnés.
- **Onglets** : passer d’Accueil à Films était brut et l’écran se redessinait plusieurs fois.
  - L’onglet touché s’allume tout de suite ; son écran vient entier (ses premières affiches comprises, 250 ms au plus), et l’écran quitté s’y fond en 180 ms, sous les barres qui ne bougent pas.
  - Films et Séries s’ouvrent sur ce qu’ils montraient la dernière fois, puis se mettent à jour sur place : les cartes qui n’ont pas changé restent, avec leur affiche. L’Accueil fait de même rangée par rangée, et Réglages ne se redessine plus à chaque visite.
  - Les emplacements vides ont la forme des cartes, la ligne « N films » sa hauteur : rien ne bouge quand les cartes arrivent. Les affiches déjà chargées apparaissent sans fondu.
  - Au lancement, le logo de Mira reste jusqu’à ce que l’Accueil soit prêt, au lieu d’un écran noir avec la barre du bas.
- **Recherche** : le champ ne prend plus le clavier à l’arrivée sur l’onglet (iOS ne l’ouvrait pas, et une app de l’écran d’accueil garde parfois un écran plus court après le clavier, ce qui remonte la barre du bas). Touche l’onglet une seconde fois pour écrire.
- **Masquer la Dynamic Island** (ex-« Lecture discrète », iPhone seulement) : le réglage dit maintenant clairement que le film est muet quand l’iPhone est en mode silencieux. Établi dans les sources de WebKit : la seule session audio qu’iOS ne montre pas dans la Dynamic Island est aussi celle que le mode silencieux coupe ; une app web ne peut pas avoir les deux.
- **Plus de vibration** : elle ne fonctionnait pas sur iOS 27.

## 0.7.2 — 2026-10-10

Mira web après le deuxième essai sur iPhone : les sous-titres des animés dans le menu du lecteur d’Apple, et une app qui se comporte comme une app.

- **Sous-titres dans le menu du lecteur d’Apple** : le menu n’en proposait souvent aucun.
  - Les sous-titres ASS et SSA (ceux des animés) étaient incrustés dans l’image : Jellyfin ne les convertit jamais tout seul, et réencodait toute la vidéo pour les dessiner. Ils arrivent maintenant en texte, dans le menu, et la vidéo n’est plus réencodée pour eux (leur mise en forme, couleurs et positions, est perdue ; le texte reste).
  - Avec « Aucun » choisi sur la fiche, le menu restait vide : tous les sous-titres texte sont maintenant dans le flux, éteints, et un choix dans le menu les affiche sans relancer la vidéo.
  - Ils s’affichent à l’heure, aussi après un saut dans la vidéo : Jellyfin les datait pour un autre format de segments, avec 10 s de retard dans le lecteur d’Apple, que l’extension Mira corrige.
  - Leurs noms sont en français et distincts (« Français (ASS) », « Français (ASS · forcés) », « Anglais »), et un choix fait par iOS lui-même n’est plus pris pour le tien.
- **Dynamic Island** : elle s’affiche parce qu’iOS ne reconnaît pas une app de l’écran d’accueil comme l’app au premier plan pendant une vidéo ; rien dans la page n’y change rien, et, contrairement à ce que disait la 0.7.1, le lecteur d’Apple non plus. **Réglages → Lecture discrète** tente de l’éviter (le son suit alors le mode silencieux et se mêle à la musique des autres apps). À la fermeture du lecteur, plus rien ne reste dans le Centre de contrôle ni sur l’écran verrouillé.
- **Lecteur moins capricieux** :
  - Fermer le lecteur d’Apple pendant que l’épisode suivant se charge ferme vraiment : il ne se rouvre plus tout seul, et Retour pendant la préparation non plus. La fiche revient une fois l’image du lecteur rétractée.
  - Une pause de plus d’une minute ne coupe plus la conversion de Jellyfin : la lecture reprend aussitôt.
  - Une coupure de réseau (PC en veille, passage du Wi-Fi à la 4G) n’affiche plus « Cet appareil ne peut pas lire ce flux » : Mira attend le serveur et reprend là où elle en était, sans quitter le plein écran.
  - Sans générique repéré par Jellyfin, le décompte de l’épisode suivant ne coupe plus ses 10 dernières secondes, et revenir en arrière l’annule.
  - Sur Android : la vitesse choisie reste d’un épisode à l’autre, les commandes ne surgissent plus à chaque chargement, les doubles touchers s’additionnent (20 s, 30 s…), et les feuilles et notifications s’affichent aussi en plein écran.
- **Comme une app iOS** :
  - Un écran arrive par la droite et repart par la droite ; dans l’app de l’écran d’accueil, le glissement depuis le bord est celui d’iOS, sans mouvement en double. Les onglets ne s’empilent pas dans l’historique, et deux touchers rapides ne se marchent plus dessus.
  - Le grand titre s’efface quand le petit apparaît dans la barre, qui ramène en haut d’un toucher (elle laissait passer le toucher vers l’affiche cachée dessous). La barre d’état est en verre dépoli.
  - Les appuis se dessinent comme sur iPhone, et un défilement qui commence sur une carte ne la laisse plus enfoncée. Un appui long soulève la carte avant son menu, sans ouvrir la fiche en relâchant. Les feuilles ne s’empilent plus, se tirent vers le bas, et la page dessous ne défile plus.
  - **Favori** et **Vu** changent à l’instant, avec un petit rebond et une vibration (si le téléphone le permet), et reviennent en arrière si Jellyfin refuse.
  - Tirer vers le bas actualise l’écran (app de l’écran d’accueil et Android), avec un indicateur qui tourne vraiment.
  - Sur un iPhone Pro Max en paysage, la mise en page reste celle d’un téléphone.
- **Plus de sauts ni de clignotements** : la fiche ne repart plus en haut au retour du lecteur, ne referme plus un résumé ouvert, et coche l’épisode vu sur place ; elle s’affiche dès le toucher avec l’image et le titre de la carte, et le logo ne décale plus les boutons en arrivant. L’accueil, les grilles et la recherche se mettent à jour sur place. Les images qui n’avaient pas pu se charger réessaient quand le serveur revient.
- **Connexion** : la touche « suivant » du clavier passe au mot de passe au lieu d’envoyer le formulaire, le clavier s’ouvre dès le choix du profil, un mot de passe faux fait trembler le champ. **Se déconnecter** est immédiat.
- **Autres** : la photo d’un acteur s’affiche, avec sa biographie ; **Ouvrir sur un autre appareil** est un écran comme les autres, avec Retour et **Copier l’adresse** ; les réglages changés dans le lecteur s’y voient au retour ; le bandeau « serveur injoignable » attend un vrai silence avant de s’afficher.
- **Publication** : `tools/release.ps1` construit, vérifie et publie une version en une commande.

## 0.7.1 — 2026-10-09

Mira web après le premier essai sur un vrai iPhone : la lecture passe par le lecteur d’Apple, et Mira s’ouvre même quand le PC démarre.

- **Lecture dans le lecteur d’Apple sur iPhone et iPad** : la vidéo jouait dans la page de Safari, avec les commandes de Mira, et iOS la prenait pour une page qui joue en arrière-plan (Dynamic Island). Elle s’ouvre maintenant uniquement dans le lecteur plein écran d’Apple, avec ses commandes : barre de lecture, ±10 s, vitesse, AirPlay, image dans l’image, et son menu de sous-titres et de pistes audio.
  - Les sous-titres texte du fichier et ceux à côté de lui apparaissent dans ce menu. Un choix fait là est suivi, envoyé à Jellyfin et retenu pour la série.
  - L’épisode suivant démarre dans le même lecteur, sans quitter le plein écran.
  - Fermer le lecteur (**Terminé**, ou glisser vers le bas) ramène à la fiche, la position gardée par Jellyfin. Plus rien ne reste dans la Dynamic Island.
  - Si Safari demande un toucher avant de passer en plein écran, Mira affiche un bouton Lecture.
- **Audio et sous-titres choisis sur la fiche** : sous **Lecture**, une ligne indique la piste audio et les sous-titres de départ, et en propose d’autres. Avec une conversion par Jellyfin, c’est le seul endroit pour changer d’audio : le menu d’Apple ne le peut pas. Les sous-titres en image (PGS, DVD) y sont marqués « incrustés dans l’image ».
- **Langues comme sur Windows et Mac** : **Réglages → Langue audio** et **Langue des sous-titres**, avec les mêmes choix et les mêmes valeurs par défaut (japonais puis français ; sous-titres français puis anglais). Les sous-titres dans la langue de l’audio restent éteints, sauf les forcés. Les langues choisies pour une série valent pour ses épisodes suivants.
- **Mira s’ouvre même quand le PC est éteint ou démarre** : ouverte trop tôt, l’app de l’écran d’accueil affichait la page d’erreur de Safari et y restait. Le téléphone garde maintenant la page de Mira : elle s’ouvre, un bandeau indique que le serveur ne répond pas encore, et tout reprend seul dès que Jellyfin répond. Après une mise à jour de Mira web, la page se recharge d’elle-même.
- **Jellyfin joignable dès l’allumage du PC** (Mira pour Windows) : le guide indique, pour un Jellyfin sur ce PC, si ton téléphone le trouve dès l’allumage, avant même l’ouverture de ta session. Sinon, **Disponible dès l’allumage du PC** règle tout en une fois, avec l’autorisation de Windows : le service de Jellyfin démarre avec Windows sans délai, Windows le relance s’il s’arrête sur une erreur, et le pare-feu laisse entrer ton réseau local et Tailscale, sur le port de Jellyfin seulement. L’installateur de Jellyfin n’ajoute aucune règle de pare-feu pour son service.
- **Passer les intros et les récaps** : nouveau réglage, éteint par défaut. Quand Jellyfin a repéré ces passages, la lecture saute par-dessus, aussi dans le lecteur d’Apple, qui n’a pas de place pour le bouton.
- **Autre piste audio sur Android et dans Chrome** : sans liste de pistes audio dans le navigateur, le choix d’une autre piste était ignoré et la première jouait. Jellyfin envoie maintenant un flux avec la piste choisie.
- **Sous-titres changés tout seuls** : pendant le chargement des pistes, un changement fait par le navigateur pouvait être pris pour un choix, et d’autres sous-titres s’affichaient. Seul le menu du lecteur d’Apple est suivi.

## 0.7.0 — 2026-10-09

Mira arrive sur iPhone, iPad et Android : ton serveur Jellyfin la sert lui-même, sans App Store ni compte Apple, et Mira pour Windows l’installe en un clic.

- **Mira sur iPhone, iPad et Android (Mira web)** : la version de Mira pour téléphone et tablette, servie par ton serveur Jellyfin grâce à l’extension Mira. Rien à installer sur le téléphone, ni App Store ni compte Apple. Ouvre `http://<adresse du serveur>:8096/Mira`, puis, sur iPhone, **Partager → Sur l’écran d’accueil** : elle s’ouvre en plein écran comme une app.
  - Choix du profil à la connexion, accueil avec bandeau et Continuer à regarder, films et séries avec filtres, recherche (titres et personnes), fiches avec distribution, titres similaires et longues saisons par tranches de 100, favoris, vu ou non vu, retrait de Continuer à regarder.
  - Lecteur fait pour Safari : le fichier tel quel quand l’iPhone le lit, sinon une conversion HLS par Jellyfin, le plus souvent sans réencodage. Reprise, ±10 s (double toucher sur les côtés), aperçus sur la barre de progression, intro et récap à passer, épisode suivant avec décompte, pistes audio et sous-titres, vitesse, qualité (automatique hors de chez toi), AirPlay, image dans l’image, plein écran d’iOS, commandes sur l’écran verrouillé.
  - Progression partagée avec Mira sur Windows et Mac. L’extension se met à jour avec Jellyfin à chaque version de Mira.
- **Installer Mira web depuis Mira pour Windows** : **Guide → Sur tes autres appareils → Installer Mira web** ajoute l’extension à Jellyfin, le redémarre, puis affiche un QR code à scanner avec le téléphone.

## 0.6.0 — 2026-10-08

Mira arrive sur Mac, le volume monte jusqu’à 200 % sans saturer, et les séries de plus de 500 épisodes sont complètes.

- **Mira pour Mac** : une application pour les Mac à puce Apple (macOS 14 ou plus récent), avec l’interface cinéma et le lecteur mpv intégré de la version Windows. Connexion à un serveur Jellyfin existant, accueil avec bandeau et Continuer à regarder, films, séries, recherche, filtres et favoris, fiches avec saisons, distribution et titres similaires, lecture avec reprise, chapitres, opening à passer, épisode suivant, pistes audio et sous-titres, volume jusqu’à 200 % et plein écran. La progression passe par la même file d’envoi que sous Windows. Livrée en image disque (`.dmg`) dans chaque release ; non signée par Apple, elle s’autorise une fois dans Réglages Système. L’application Windows ne change pas.
- **Volume jusqu’à 200 %** : pour un film ou un épisode enregistré trop bas, le curseur de volume va au-delà de 100 %, repère au milieu. Le son est amplifié avant d’arriver à la sortie, puis un limiteur retient les crêtes : même les passages forts ne saturent pas, contrairement au volume au-delà de 100 % d’mpv seul. Le niveau s’affiche à côté du curseur, en blanc quand le son est amplifié, et la molette sur le haut-parleur règle le volume.
- **Séries de plus de 500 épisodes** : Mira n’en demandait que les 500 premiers. Sur un long animé, les saisons suivantes manquaient sur la fiche, et l’épisode suivant n’était plus trouvé après le 500ᵉ. Toute la série est maintenant chargée, sans les épisodes que Jellyfin affiche comme manquants (sans fichier à lire).
- **Longues saisons par tranches de 100** : une saison de plus de 100 épisodes se choisit par tranche (« Saison 1 · 101–200 »), et la fiche s’ouvre sur celle de l’épisode à suivre. Elle n’affiche plus des centaines d’épisodes et de vignettes d’un coup.
- **Les titres d’un acteur ou d’un réalisateur** : sur une fiche, les noms de la distribution et de la réalisation sont cliquables. Un clic affiche tous ses titres présents dans ta bibliothèque, avec une pastille « Avec … » (ou « Réalisés par … ») qui retire ce filtre. Les autres filtres et le tri s’y ajoutent, et Retour ramène à la fiche.

## 0.5.9 — 2026-10-07

La fenêtre tient vraiment dans l’écran et se rouvre comme on l’a laissée, et un titre peut être retiré de Continuer à regarder.

- **La fenêtre tient vraiment dans l’écran** : depuis la 0.5.6, la taille adaptée à l’écran (90 % sur un écran 1080p à 125 ou 150 %) était calculée trop tôt, puis remplacée par la taille par défaut de 1480 × 930. Sur ces écrans, la fenêtre redébordait en 0.5.8.
- **Retirer de Continuer à regarder** : clic droit sur une carte de la rangée. Le film ou la série en disparaît, ainsi que du bandeau, jusqu’à sa prochaine lecture. Un titre en cours perd aussi son point de reprise, sur Jellyfin également, pour ne plus être proposé sur tes autres appareils. **Annuler**, dans la notification, remet tout.
- **Mira retient sa fenêtre** : elle se rouvre à la taille où tu l’as laissée, agrandie si elle l’était, sans jamais dépasser l’écran. Le plein écran et le mini-lecteur ne sont pas retenus.

## 0.5.8 — 2026-10-05

Une fenêtre à la taille de l’écran, la même barre de gauche partout, et Jellyfin accessible hors de chez soi avec Tailscale.

- **Fenêtre normale au démarrage** ([#13](https://github.com/sasou-web/Mira/issues/13)) : Mira ne s’ouvre plus agrandie. Sur un écran trop petit pour sa taille par défaut (1080p à 125 ou 150 %), la fenêtre prend 90 % de l’écran, centrée.
- **Barre de gauche identique sur toutes les pages** ([#13](https://github.com/sasou-web/Mira/issues/13)) : la fiche d’un titre et la page Téléchargements commençaient à droite de la barre, qui laissait voir derrière elle l’accueil et les couleurs de son bandeau. Elles passent maintenant dessous, comme les Réglages et le Guide.
- **Regarder hors de chez toi** ([#12](https://github.com/sasou-web/Mira/issues/12)) : l’adresse donnée par le guide ne marche que sur le réseau de la maison. Le guide ajoute une partie **Hors de chez toi** :
  - sans Tailscale, il explique comment l’installer, gratuitement, sur ce PC et sur l’appareil ;
  - avec Tailscale, il affiche l’adresse à utiliser et un bouton **Autoriser Tailscale**.
  
  À l’installation, Mira laisse l’accès depuis Internet désactivé dans Jellyfin, qui refusait donc aussi les appareils Tailscale. Ce bouton ajoute seulement le réseau de Tailscale aux réseaux locaux de Jellyfin : le reste d’Internet reste refusé, sans rien ouvrir sur la box.

## 0.5.7 — 2026-10-02

Les téléchargements TorLink sont déplacés sans doublon, Actualiser fait vraiment analyser les dossiers par Jellyfin, et Favoris devient un filtre.

- **Téléchargements TorLink déplacés, jamais en double** : un téléchargement terminé est maintenant **déplacé** dans la bibliothèque. Avant, il y était copié, ou lié par un lien physique qui le faisait apparaître dans deux dossiers.
  - Sur le même disque, c’est un simple changement de dossier : instantané, sans place en plus.
  - Sur un autre disque, chaque fichier est copié puis aussitôt supprimé du dossier de TorLink, un par un.
  - Quand TorLink n’a pas encore de dossier, Mira le fait télécharger sur le disque de la bibliothèque, dans « Téléchargements TorLink » : les déplacements y sont toujours instantanés.
  - TorLink cesse de partager ce qui a été déplacé : relancé, il ne va plus chercher les fichiers disparus sur le réseau. Les dossiers vidés quittent celui de TorLink.
  - **Réglages → TorLink → Continuer à partager** (désactivé par défaut, y compris pour les profils existants) garde l’ancien lien physique, sans place en plus, mais jamais de copie.
- **Actualiser, pour de vrai** : le bouton **Actualiser** demande à Jellyfin d’analyser tes dossiers, affiche l’avancement (« Analyse… 48 % »), recharge la liste et annonce les nouveaux titres. Avant, il relisait seulement la liste, sans voir les fichiers ajoutés, et presque sans signe visible. Sans droits d’administrateur sur Jellyfin, Mira recharge la liste et dit pourquoi elle ne peut pas lancer l’analyse.
- **Favoris** : c’est maintenant un filtre qui s’allume, se combine avec Films, Séries ou la recherche, et se désactive au second clic. **Réinitialiser** le désactive : avant, la page restait sur les favoris.

## 0.5.6 — 2026-10-02

TorLink s’installe d’un clic depuis Mira, les écrans tiennent sur un écran 1080p même à 150 %, et des textes plus courts.

- **TorLink s’installe depuis Mira** : la page **Téléchargements** (flèche du rail) liste les téléchargeurs. Activer **TorLink** l’ouvre s’il est déjà sur le PC (copie de `npx torlnk`, installation npm globale, raccourci). Sinon, Mira installe pour ce compte, sans droits administrateur :
  - Node.js 24.21.0, le zip officiel vérifié par son SHA-256 ;
  - TorLink 1.9.0, installé par npm dans son dossier `data`.
  Le désactiver le ferme. Plus besoin de dossier ni de `torlink.bat`.
- **Adapté à l’écran** : sur un écran 1080p à 125 % ou 150 %, la fenêtre dépassait de l’écran. Elle s’ouvre maintenant agrandie, et sa taille minimale passe à 960 × 600.
  - L’accueil, la connexion, l’installation de Jellyfin et les nouveautés rétrécissent au lieu de déborder.
  - Le guide passe sur deux colonnes quand la place le permet.
  - Sous Windows 10, la fenêtre agrandie ne cache plus ses bords hors de l’écran (haut des boutons de fenêtre, rail, bas de page) : ce réglage n’était appliqué que sous Windows 11.
- **Textes plus courts** : « Nom d’utilisateur » et « Mot de passe » à la création du compte Jellyfin (au lieu de « Son mot de passe »), boutons « Se connecter » et « Installer », et des explications réduites à l’essentiel. Les nouveautés détaillent seulement la dernière version.
- **« Continuer à regarder » montre la bonne saison** : un épisode n’affiche plus l’image générale de la série, qui pouvait montrer une autre saison, avec un autre arc ou d’autres personnages. Mira choisit dans cet ordre : la vignette de la saison, son fond, l’image de l’épisode lui-même, et en dernier recours l’image de la série. Le bandeau du haut garde l’image de la série, avec son logo.

## 0.5.5 — 2026-10-01

Un guide pour bien démarrer, les nouveautés résumées après chaque mise à jour, et Jellyfin plus fiable.

- **Bienvenue** : au tout premier lancement, Mira explique en trois cartes ce que font Jellyfin et Mira, puis propose trois façons de commencer : installer Jellyfin sur ce PC, se connecter à son serveur, ou découvrir la démo.
- **Guide de démarrage** : bouton **?** de la barre de gauche, touche **F1**, **Réglages → Jellyfin**, ou **Où mettre mes vidéos ?** quand la bibliothèque est vide. Il montre :
  - les dossiers de chaque bibliothèque Jellyfin, avec un bouton **Ouvrir**, et des exemples de noms que Jellyfin reconnaît ;
  - le bouton **Ouvrir Jellyfin dans le navigateur**, vers son tableau de bord (bibliothèques, comptes, accès depuis Internet) ;
  - l’adresse à entrer dans l’application Jellyfin d’un téléphone ou d’une TV (celle du PC sur le réseau de la maison), avec **Copier** ;
  - l’essentiel de Mira.

  Il s’ouvre une fois après la première connexion, et juste après l’installation de Jellyfin par Mira.
- **Nouveautés** : après une mise à jour, un écran plein résume les points forts de la version, sans le détail. Les pages de release sur GitHub reprennent ce même résumé court ; le détail reste ici.
- Bibliothèque vide : au lieu de « Rien à afficher ici », Mira dit où ranger les vidéos et ouvre le guide.

Installation de Jellyfin depuis Mira : corrections après le premier essai sur un vrai PC.

- Un Jellyfin déjà installé sur le PC, même arrêté, n’était pas reconnu : son installateur écrit sa clé dans la partie 32 bits du registre, que Mira ne lisait pas. Mira pouvait donc relancer l’installateur par-dessus. Il le reconnaît maintenant, par cette clé ou par son service, et ne réinstalle jamais par-dessus un Jellyfin existant.
- L’installateur de Jellyfin peut afficher « Could not start the Jellyfin Server service » alors que son service démarre bien. Mira s’arrêtait sur ce message, même quand Jellyfin tournait juste après. Il vérifie maintenant lui-même que Jellyfin répond et continue. Quand Jellyfin ne démarre vraiment pas, Mira affiche la cause écrite dans son journal, par exemple un port déjà pris, au lieu d’un simple « ne répond pas ».
- **Écoute des changements avec les Jellyfin récents** : avec la 12.1 installée par Mira, et les autres versions récentes, Jellyfin refusait l’écoute en temps réel de Mira. Son journal affichait « Token is required » toutes les 30 secondes. Les ajouts et les « vu » faits ailleurs n’apparaissaient donc qu’à l’actualisation. Mira envoie maintenant sa session dans l’en-tête de la connexion, comme pour ses autres requêtes, au lieu de l’adresse.
- Un Jellyfin 12 en plein démarrage n’est plus pris pour absent. Sa page de démarrage annonce « assistant non terminé » même quand il l’est : Mira attend que le serveur lui-même réponde. Un autre programme sur le port 8096 est signalé avant toute installation.

## 0.5.4 — 2026-10-01

Jellyfin et le moteur vidéo s’installent depuis Mira, et des pannes rares qui pouvaient bloquer Mira sont maintenant contenues.

- **Jellyfin installé et configuré depuis Mira** : sous le formulaire de connexion, **Pas encore de serveur ? Installer Jellyfin sur ce PC**. Mira télécharge l’installateur officiel de Jellyfin 12.1 et vérifie son empreinte SHA-256, la même que celle du manifeste winget de Jellyfin. Il le lance (Windows demande une autorisation), crée `Films`, `Séries` et `Animes` dans `Vidéos\Jellyfin` ou dans un autre dossier choisi, remplit l’assistant de premier démarrage de Jellyfin et se connecte. L’assistant reçoit : interface et métadonnées en français, ton compte administrateur, trois bibliothèques, accès depuis Internet désactivé. TorLink retrouve ces trois bibliothèques sans réglage.
- Jellyfin tourne comme service Windows, sous le compte Network Service : Mira lui donne le droit de lire le dossier choisi. Un fichier que TorLink y range par lien physique ou par déplacement garde les droits de son dossier de téléchargement ; il reçoit maintenant ce même droit, sinon Jellyfin ne le verrait pas.
- **Moteur vidéo mpv installé par Mira** : une case « Télécharger le moteur vidéo mpv » dans l’installateur (cochée par défaut), un bouton **Installer le moteur mpv** dans **Réglages → Lecture**, et un bouton **Installer** proposé à la première lecture quand aucun moteur n’est trouvé. La lecture démarre une fois le moteur prêt.
- Mira télécharge une build précise des builds Windows de mpv (31 Mo, depuis SourceForge), vérifie son empreinte SHA-256, extrait `libmpv-2.dll`, vérifie aussi la sienne, et la range dans `data\mpv`, que les mises à jour ne touchent pas. Un fichier altéré n’est jamais installé. Un moteur déjà présent (mpv.net, Jellyfin MPV Shim, ou choisi dans les réglages) est gardé.

- Un disque plein, une base locale verrouillée ou abîmée pendant une lecture n’ouvre plus une fenêtre d’erreur toutes les 3 secondes : la lecture continue et la ligne de synchronisation indique « Stockage local indisponible ».
- Dans ce cas, Mira ne pouvait plus se fermer : la fenêtre disparaissait mais le processus restait ouvert, et les envois suivants de la session étaient bloqués. Corrigé.
- Un cache local illisible (coupure de courant, disque défaillant) empêchait d’ouvrir le compte à chaque démarrage. Il est maintenant mis de côté (`.bad-…`) et recréé depuis Jellyfin, avec un message ; seuls les envois de progression qui n’étaient pas encore partis sont perdus.
- L’écoute des changements de Jellyfin ne s’arrête plus pour la session sur un message inattendu, et un serveur qui ferme la connexion aussitôt ouverte n’est plus recontacté toutes les 2 secondes.
- Un fichier `device-id` vide n’envoie plus un identifiant d’appareil vide à Jellyfin ; une commande envoyée au lecteur juste après son arrêt ne peut plus fermer Mira.

## 0.5.3 — 2026-10-01

Connexion plus simple, fiches plus riches et caches qui ne grossissent plus sans fin.

- **Connexion** : l’adresse du serveur peut s’écrire sans `http://` (`192.168.1.20`, `nas:8096`, `jellyfin.maison.lan`) ou se coller depuis la barre d’adresse de la page web de Jellyfin (`…:8096/web/#/home.html`). Mira essaie HTTPS, puis le port 8096 de Jellyfin, puis HTTP, et garde la première adresse où un serveur Jellyfin répond, avant d’envoyer le mot de passe. Une adresse passée en HTTPS par le serveur est retenue telle quelle.
- Messages de connexion explicites : adresse qui n’est pas un serveur Jellyfin (Emby compris), certificat HTTPS non reconnu, serveur muet, et version trop ancienne. Mira utilise les routes de **Jellyfin 10.9** : un serveur plus ancien est refusé avec sa version au lieu d’échouer ensuite sur des erreurs 404.
- **Fiches** : distribution (« Avec », six premiers rôles) et réalisation dans la colonne des genres ; rangée **Titres similaires** sous la fiche, d’après Jellyfin, aussi large que la fenêtre. Clic pour ouvrir, clic droit pour lire, marquer vu ou ajouter aux favoris.
- Nouveau tri **Dernière lecture** dans le catalogue.
- **Caches bornés** : les images sur disque (tous comptes confondus) sont ramenées à 768 Mo au-delà de 1 Go, les moins récemment vues d’abord ; les images décodées gardées en mémoire sont limitées à 512 Mo ; les pages de catalogue et positions locales déjà envoyées de plus de 30 jours sont oubliées. L’accueil, la reprise, l’historique et les envois en attente restent.
- `errors.log` est écrit dans le profil utilisé (`--data` compris), avec la méthode en cause et sans message d’erreur, et plafonné à 256 Ko.

## 0.5.2 — 2026-09-29

Mises à jour automatiques et nouvelle barre de lecture.

- **Mises à jour automatiques** : Mira cherche une nouvelle version sur GitHub à l’ouverture, puis toutes les 6 heures, télécharge le fichier qui correspond à sa copie (installateur, exécutable portable ou archive) et l’installe à la fermeture. **Redémarrer** l’installe tout de suite et rouvre Mira. Le dossier `data` n’est jamais modifié.
- Chaque version passe une vérification avant d’être installée : manifeste `mira-update.json` signé (ECDSA P-256) par une clé intégrée à Mira, puis taille et empreinte SHA-256 du fichier. Une release modifiée sur GitHub ou en chemin, non signée ou plus ancienne est refusée.
- Un téléchargement interrompu reprend où il s’était arrêté. Mira attend la fin d’une installation en cours avant de démarrer, annonce au démarrage suivant si la mise à jour a réussi, et ne relance plus à chaque fermeture une version qui a échoué deux fois.
- **Réglages → Mises à jour** : version, état, recherche manuelle, redémarrage pour installer, lien vers les nouveautés, et interrupteur des mises à jour automatiques.
- **Barre de lecture** redessinée : titre et épisode (« Épisode 3 / 12 · nom ») à gauche, chapitre en cours et temps à droite, au-dessus d’une barre découpée par chapitres ; lecture, épisode suivant et volume à gauche ; options, audio et sous-titres, mini-lecteur et plein écran à droite.
- Le curseur de volume sort du haut-parleur au survol, et un instant après ↑ / ↓. L’icône indique son niveau.
- Menus du lecteur ouverts au-dessus de leur bouton : pistes audio et sous-titres en deux colonnes, cochées, avec langue, codec et canaux ; vitesses, décalage des sous-titres et informations de lecture sous **⋮**.
- Les boutons ±10 secondes quittent la barre, comme sur la référence choisie ; ← et → restent les raccourcis.
- Outils de release : `tools/Mira.Release` (clé de signature protégée par Windows, hors du dépôt), manifeste signé par `tools/package.ps1`, essai de bout en bout `tools/update-check.ps1`.

### À savoir

Les copies en 0.5.1 ou plus ancienne n’ont pas ces mises à jour : il faut installer la 0.5.2 une fois à la main. Une release publiée sans `mira-update.json` signé n’est pas proposée aux copies installées.

## 0.5.1 — 2026-09-29

Installation simplifiée.

- **Installateur Windows** `Mira-0.5.1-win-x64-setup.exe` : installation pour l’utilisateur courant, sans droits administrateur, dans `%LOCALAPPDATA%\Programs\Mira`, avec raccourci Démarrer, raccourci Bureau facultatif et désinstallation depuis les paramètres de Windows. Une mise à jour s’installe par-dessus et garde les données ; la désinstallation demande avant de les supprimer.
- **Exécutable portable** `Mira-0.5.1-win-x64-portable.exe` : Mira en un seul fichier, qui crée son dossier `data` à côté de lui.
- La page du terminal TorLink est servie depuis les ressources de Mira ; les liaisons clavier de mpv et l’icône Windows sont intégrées à l’exécutable portable.
- Contrôle TorLink : avec un autre TorLink ouvert sur le PC, il vérifie la page puis s’arrête, au lieu d’échouer.

## 0.5.0 — 2026-09-29

TorLink dans Mira, jusqu’à la bibliothèque Jellyfin.

- Page **TorLink** dans le rail : l’interface de TorLink, installé séparément et non modifié, s’affiche dans Mira (pseudo-console Windows, xterm.js dans WebView2). Une seule instance ; fermer Mira ferme TorLink proprement.
- Rangement automatique des téléchargements terminés dans les dossiers des bibliothèques Jellyfin : `Titre (Année)\Titre (Année).mkv`, `Série\Season 01\Série - S01E02.mkv`, sous-titres, bonus et packs de saison. Les dossiers existants sont réutilisés ; aucun fichier existant n’est remplacé, et un fichier que Mira n’a pas placé n’est jamais déplacé.
- Lien physique sur le même disque : TorLink continue de partager sans doublon. Sinon copie, ou déplacement au choix.
- Films, séries et animes distingués par la source TorLink, les dossiers existants et le nom de sortie ; « Classer comme… » corrige un classement. Jeux et téléchargements sans vidéo ignorés.
- Jellyfin est prévenu des nouveaux dossiers ; « Vers Jellyfin » suit chaque titre jusqu’à son apparition et propose « Voir », quand le serveur voit les fichiers au même chemin que Mira.
- **Réglages → TorLink** : rangement automatique, partage, emplacement de TorLink et dossiers de bibliothèque manuels.

### Limites connues

TorLink et ses sources restent des composants externes : Mira ne choisit ni ne vérifie ce qui est téléchargé. Comme tout client BitTorrent, TorLink échange avec des pairs, écoute un port et peut demander au routeur de l’ouvrir (UPnP, NAT-PMP) tant qu’il tourne. Les dossiers sont lus depuis Jellyfin avec un compte administrateur, s’ils existent sur ce PC ; sinon, ils se choisissent dans les réglages. Avec Jellyfin dans Docker ou sur un autre PC, les dossiers choisis doivent être ceux que le serveur partage, et l’ajout peut attendre sa prochaine analyse de la bibliothèque. Seuls les téléchargements terminés après l’activation sont rangés automatiquement ; les cinq plus récents d’avant restent proposés avec « Importer ».

## 0.4.9 — 2026-09-29

Première publication publique du projet et de la distribution Windows x64.

- Bibliothèque Jellyfin : accueil, films, séries, recherche, filtres, favoris, fiches et saisons.
- Lecture intégrée libmpv, plein écran, mini-lecteur déplaçable, pistes, sous-titres, chapitres et passage au suivant.
- Synchronisation persistante, progression locale, reprise après déconnexion et statut vu après fin de lecture.
- Identité Mira dans Windows, zone de notification, commandes dans l’aperçu de la barre des tâches et session multimédia système.
- Survol des cartes unifié et suivi pendant le défilement ; indicateurs de carrousel arrondis ; connexion redessinée.
- « Continuer à regarder » trié par dernière lecture, avec reprise locale après redémarrage.
- Galerie reproductible à partir des données fictives intégrées, documentation, notices tierces et script de création d’une archive propre.

### Limites connues

Windows x64 uniquement, interface française, libmpv à installer séparément. Distribution non signée et mise à jour manuelle. Transcodage, choix entre plusieurs versions d’un film, édition des collections et tests approfondis HDR/multicanal encore à réaliser. Les rendus de démonstration ne représentent pas des médias fournis avec l’application.
