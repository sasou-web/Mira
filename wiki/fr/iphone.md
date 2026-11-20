---
title: Installer sur iPhone et iPad
short: iPhone et iPad
description: Mira pour iPhone est une app native installée avec AltStore Classic et un identifiant Apple gratuit, rafraîchie chaque semaine par un PC Windows ou un Mac de la maison.
platforms: iOS 26 ou plus récent
---

Mira pour iPhone et iPad est une vraie app, avec ta bibliothèque, la recherche, son propre lecteur (Image dans l’image, AirPlay, épisode suivant), la Minuterie, Regarder ensemble et, hors de chez toi, Tailscale intégré. Elle n’est pas sur l’App Store : tu l’installes avec **AltStore Classic**, gratuit, qui la signe sur ton iPhone avec ton propre identifiant Apple.

## Ce que ça implique

- **iOS 26 ou plus récent** (iPhone 11 et plus récents).
- **Un identifiant Apple gratuit suffit.** Apple ne signe alors que 3 apps à la fois (AltStore compte pour une), et chaque app **expire au bout de 7 jours** si elle n’est pas rafraîchie. AltStore la rafraîchit en arrière-plan quand ton iPhone est sur le même Wi-Fi qu’un ordinateur où tourne **AltServer** : le PC de Jellyfin, toujours allumé, est parfait pour ça.
- AltServer tourne sous **Windows** et sur **macOS 11 ou plus récent**. Chaque étape ci-dessous dit ce qui change entre les deux.

> [!PRIVACY]
> Tu tapes ton identifiant Apple et son mot de passe dans AltServer, qui les envoie seulement à Apple, pour créer le certificat gratuit. AltStore télécharge la source de Mira sur `miracinema.app` et l’app sur GitHub. Mira elle-même ne parle qu’à ton Jellyfin, et à Tailscale seulement si tu actives Tailscale intégré.

## 1. Préparer l’ordinateur (une fois)

### Sous Windows

Sur le PC où tourne Jellyfin, avec ton compte Windows habituel :

1. Installe **iTunes** et **iCloud** depuis le site d’Apple, **pas** depuis le Microsoft Store (AltServer ne trouve pas les versions du Store) :
   - [iTunes 64 bits](https://www.apple.com/itunes/download/win64)
   - [iCloud](https://updates.cdn-apple.com/2020/windows/001-39935-20200911-1A70AA56-F448-11EA-8CC0-99D41950005E/iCloudSetup.exe)
   Désinstalle d’abord une version du Microsoft Store. **Ne te connecte pas à iCloud** : cette version refuse de toute façon (« Update iCloud for Windows »), clique sur **OK** puis **Cancel**, et ne la mets pas à jour.
2. Télécharge [AltServer pour Windows](https://cdn.altstore.io/file/altstore/altinstaller.zip), décompresse-le et lance `Setup.exe`.
3. Cherche **AltServer** dans Windows, clic droit → **Exécuter en tant qu’administrateur**. Si le pare-feu demande, autorise les **réseaux privés** seulement. Une icône en losange apparaît près de l’horloge.
4. Si son menu propose **Launch at Login**, coche-le. Le rafraîchissement ne marche que si une session Windows est ouverte sur le PC avec AltServer lancé.

### Sur un Mac

Rien d’autre à installer : sur Mac, c’est le Finder qui parle à l’iPhone, ni iTunes ni iCloud.

1. Télécharge [AltServer pour macOS](https://cdn.altstore.io/file/altstore/altserver.zip) (macOS 11 ou plus récent), ouvre le zip et range **AltServer** dans le dossier **Applications**.
2. Ouvre AltServer depuis Applications. Il n’a pas de fenêtre : une icône en losange apparaît dans la **barre des menus**, en haut à droite. Si macOS demande, autorise ses **notifications** : c’est par elles qu’il te dit qu’AltStore est installé.
3. Pour qu’il se lance tout seul : **Réglages Système → Général → Ouverture** → **+** sous **Ouvrir à la connexion** → **AltServer**.
4. Le rafraîchissement ne marche que si le Mac est éveillé avec AltServer lancé. Sur un Mac qui reste à la maison, empêche la veille : **Réglages Système → Énergie** → **Empêcher la suspension d’activité automatique lorsque l’écran est éteint** (sur un portable : **Batterie → Options** → **Empêcher la suspension d’activité automatique sur adaptateur secteur lorsque l’écran est éteint**).
5. Les AltServer récents n’ont plus besoin du module de Mail. Si une alerte **Install Mail Plug-in…** apparaît quand même, suis [les étapes d’AltStore](https://faq.altstore.io/altstore-classic/how-to-install-altstore-macos/enable-mail-plug-in).

## 2. Installer AltStore sur l’iPhone (une fois)

1. Branche l’iPhone à l’ordinateur, déverrouille-le et touche **Se fier** (sur Mac, clique aussi sur **Se fier** si le Mac le demande).
2. Active la synchronisation en Wi-Fi, c’est ce qui permet le rafraîchissement sans câble :
   - **Windows** : ouvre **iTunes**, connecte-toi avec ton identifiant Apple, clique sur l’icône de l’iPhone, puis dans **Résumé** coche **Synchroniser avec cet iPhone en Wi-Fi** et clique sur **Appliquer**.
   - **Mac** : ouvre le **Finder**, clique sur l’iPhone dans la barre latérale, puis dans **Général** coche **Afficher cet iPhone en Wi-Fi** et clique sur **Appliquer**.
3. Icône d’AltServer (près de l’horloge sous Windows, dans la barre des menus sur Mac) → **Install AltStore** → ton iPhone. Entre ton identifiant Apple et son mot de passe.
4. Attends la notification d’AltServer. Si l’icône d’AltStore n’apparaît pas, redémarre l’iPhone.
5. Sur l’iPhone : **Réglages → Général → VPN et gestion de l’appareil**, touche ton identifiant Apple, puis **Faire confiance**.
6. **Réglages → Confidentialité et sécurité → Mode développeur** : active-le. L’iPhone redémarre et te demande de confirmer.
7. **Réglages → Général → Actualisation en arrière-plan** : laisse-la activée pour **AltStore**.

## 3. Installer Mira

1. Ouvre **AltStore** → **Sources** → **+**, et ajoute :
   ```text
   https://miracinema.app/altstore.json
   ```
2. Ouvre la source **Mira** → **Mira** → **FREE**. AltStore télécharge l’app, la signe et l’installe.
3. Ouvre Mira et connecte-toi à Jellyfin. La première fois sur le Wi-Fi de la maison, iOS demande l’accès au **réseau local** : touche **Autoriser**, sinon Mira ne voit pas Jellyfin chez toi.

Sous Windows, la carte **Mira sur ton iPhone** du guide (et sur Mac, **Réglages → Compte**) montre un **QR code** : scanne-le avec l’appareil photo de l’iPhone et Mira s’ouvre avec ton serveur déjà trouvé ; il ne reste que ton compte à taper. Tu peux aussi te connecter avec un code : **Se connecter avec un code** affiche six chiffres, qu’un administrateur valide dans Mira pour Windows ou Mac (**Réglages → Amis & invités → Autoriser un appareil**) ou sur la page web de Jellyfin. Quick Connect doit être activé dans Jellyfin.

## Au quotidien

- **Mises à jour** : AltStore montre chaque nouvelle version de Mira dans **My Apps** ; touche **Update**.
- **Expiration** : **My Apps** montre les jours restants. Tant que l’ordinateur est allumé et éveillé avec AltServer et que l’iPhone passe sur son Wi-Fi une fois par semaine, AltStore rafraîchit seul. Sinon, ouvre AltStore chez toi et touche **Refresh All**. Une app expirée ne s’ouvre plus mais ne perd rien.
- **Hors de chez toi** : voir [Hors de chez toi](away.md#sur-iphone-et-ipad).

## Si ça coince

- **« Could not find AltServer »** pendant un rafraîchissement : l’iPhone et l’ordinateur ne sont pas sur le même Wi-Fi, AltServer n’est pas lancé (ou le Mac est en veille), ou la synchronisation Wi-Fi est décochée (iTunes sous Windows, Finder sur Mac). Un Wi-Fi d’école ou public peut bloquer : branche le câble.
- **AltStore installé depuis Safari** : c’est AltStore PAL (Union européenne), qui n’installe que des apps notarisées par Apple, donc pas Mira. Supprime-le et installe AltStore Classic avec AltServer.
- **AltServer ne voit pas l’iPhone** : sous Windows, vérifie qu’iTunes et iCloud viennent du site d’Apple ; sur Mac, vérifie que l’iPhone apparaît dans la barre latérale du Finder. Puis débranche et rebranche l’iPhone déverrouillé.
- **Erreur 503 ou -80046** : mets AltServer à jour (son icône → **Check for Updates…**), puis refais **Install AltStore**, sans supprimer AltStore.
- Les guides d’AltStore : [installer sous Windows](https://faq.altstore.io/altstore-classic/how-to-install-altstore-windows), [installer sur macOS](https://faq.altstore.io/altstore-classic/how-to-install-altstore-macos), [dépannage](https://faq.altstore.io/altstore-classic/troubleshooting-guide).
