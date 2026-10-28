# Installer Mira pour iPhone (AltStore Classic)

Mira pour iPhone et iPad est une app native (SwiftUI, iOS 26, Liquid Glass). Elle n’est pas sur l’App Store : tu l’installes avec **AltStore Classic**, gratuit, qui la signe sur ton iPhone avec ton propre identifiant Apple. Pour l’instant (phase 0), elle se connecte à ton Jellyfin et montre l’Accueil et les fiches ; la lecture passe encore par Mira web, dans Safari. Tes amis gardent Mira web ([Mira sur iPhone, iPad et Android](INSTALLATION-WEB.md)), qui n’expire jamais.

## Ce que ça implique

- **iOS 26 ou plus récent** (iPhone 11 et plus récents).
- **Un identifiant Apple gratuit** suffit. Apple limite ce qu’il signe : **3 apps** à la fois (AltStore compte pour une), et chaque app **expire au bout de 7 jours** si elle n’est pas rafraîchie. AltStore la rafraîchit tout seul en arrière-plan quand ton iPhone est sur le même Wi-Fi qu’un PC où tourne **AltServer** : le PC serveur, toujours allumé, s’en charge.
- **Vie privée** : ton identifiant Apple et son mot de passe sont tapés dans AltServer, qui les envoie seulement à Apple (pour créer le certificat gratuit). AltStore télécharge la source de Mira sur `miracinema.app` et l’app sur GitHub. Mira elle-même ne parle qu’à ton serveur Jellyfin : aucun compte chez nous, aucune statistique.
- AltStore (Riley Testut, [code source](https://github.com/altstoreio/AltStore)) existe depuis 2019 ; ses textes sont en anglais, d’où les noms de boutons en anglais ci-dessous.

## 1. Préparer le PC serveur (une seule fois)

Sur le PC où tourne Jellyfin, avec ton compte Windows habituel :

1. Installe **iTunes** et **iCloud** depuis le site d’Apple, **pas** depuis le Microsoft Store (AltServer ne trouve pas les versions du Store) :
   - iTunes 64 bits : <https://www.apple.com/itunes/download/win64>
   - iCloud : <https://updates.cdn-apple.com/2020/windows/001-39935-20200911-1A70AA56-F448-11EA-8CC0-99D41950005E/iCloudSetup.exe>
   Si une version du Microsoft Store est déjà là, désinstalle-la d’abord (Paramètres → Applications).
   **Ne te connecte pas à iCloud** : AltServer n’a besoin que de ce qu’il installe. Cette version refuse d’ailleurs la connexion (« Update iCloud for Windows to the latest version to sign in ») : clique sur **OK** puis **Cancel**, et surtout ne la mets pas à jour par le Microsoft Store. Ton identifiant Apple, tu le tapes dans AltServer à l’étape 2.
2. Télécharge **AltServer pour Windows** : <https://cdn.altstore.io/file/altstore/altinstaller.zip>. Décompresse `altinstaller.zip`, puis lance `Setup.exe`.
3. Dans la recherche de Windows, tape **AltServer**, clic droit → **Exécuter en tant qu’administrateur**. Si le pare-feu demande, autorise les **réseaux privés** seulement. Une icône en losange apparaît près de l’horloge.
4. Pour qu’AltServer reparte après un redémarrage : si son menu (clic sur l’icône) propose **Launch at Login**, coche-le ; sinon, relance-le comme à l’étape 3 après chaque redémarrage. Le rafraîchissement ne marche que si une session Windows est ouverte sur le PC et AltServer lancé.

## 2. Installer AltStore sur ton iPhone (une seule fois)

1. Branche ton iPhone au PC serveur en USB, déverrouille-le et touche **Se fier** à cet ordinateur.
2. Ouvre **iTunes**, connecte-toi avec ton identifiant Apple, clique sur l’icône de l’iPhone, puis dans **Résumé** coche **Synchroniser avec cet iPhone en Wi-Fi** et clique sur **Appliquer**. C’est ce qui permet le rafraîchissement sans câble.
3. Clic sur l’icône d’AltServer → **Install AltStore** → ton iPhone. Tape ton identifiant Apple et son mot de passe.
4. Attends la notification d’AltServer (quelques secondes). Si l’icône d’AltStore n’apparaît pas, redémarre l’iPhone.
5. Sur l’iPhone : **Réglages → Général → VPN et gestion de l’appareil**, touche ton identifiant Apple, puis **Faire confiance**.
6. **Réglages → Confidentialité et sécurité → Mode développeur** : active-le. L’iPhone redémarre et te demande de confirmer.
7. **Réglages → Général → Actualisation en arrière-plan** : laisse-la activée pour **AltStore**.

## 3. Installer Mira

1. Ouvre **AltStore** → onglet **Sources** → **+** → colle l’adresse suivante, puis valide :
   `https://miracinema.app/altstore.json`
2. Ouvre la source **Mira** → **Mira** → **FREE**. AltStore télécharge l’app, la signe avec ton identifiant Apple et l’installe.
3. Ouvre **Mira**, tape l’adresse de ton serveur (`192.168.1.198` à la maison, `100.114.247.13` avec Tailscale), ton nom d’utilisateur et ton mot de passe. À la première connexion au réseau de la maison, iOS demande l’accès au **réseau local** : touche **Autoriser** (sinon Mira ne voit pas Jellyfin en Wi-Fi).

La source ne propose Mira qu’à partir de la première version publiée avec l’app iPhone (la 0.8.6). Avant, ou pour essayer une version pas encore publiée :

1. Sur l’iPhone, dans Safari connecté à GitHub, ouvre l’exécution « iPhone build » voulue (onglet **Actions** du dépôt) et télécharge l’artefact **Mira-ios**.
2. Dans **Fichiers**, touche le `.zip` téléchargé pour en sortir `Mira-x.y.z-ios.ipa`.
3. Dans **AltStore** → **My Apps** → **+** (en haut à gauche) → choisis le `.ipa`.

## Au quotidien

- **Mises à jour** : AltStore montre chaque nouvelle version de Mira dans **My Apps** ; touche **Update**.
- **Expiration** : **My Apps** indique les jours restants. Tant que le PC serveur est allumé avec AltServer et que ton iPhone passe sur son Wi-Fi au moins une fois par semaine, AltStore rafraîchit seul. Sinon, ouvre AltStore chez toi et touche **Refresh All**. Une app expirée ne s’ouvre plus mais ne perd rien : un rafraîchissement suffit.
- **Se déconnecter** : Mira → **Réglages** → **Se déconnecter** ferme aussi la session sur Jellyfin.

## Si ça coince

- **« Could not find AltServer »** pendant un rafraîchissement : l’iPhone et le PC ne sont pas sur le même Wi-Fi, AltServer n’est pas lancé, ou la synchronisation Wi-Fi d’iTunes est décochée.
- **iCloud demande une mise à jour pour se connecter** : normal avec la version du lien, ne te connecte pas (voir l’étape 1). Pour garder iCloud connecté sur ce PC, suis « Windows Store iCloud » dans le guide de dépannage d’AltStore ci-dessous.
- **AltStore installé depuis Safari** : c’est AltStore PAL (Union européenne), qui n’installe que des apps notariées par Apple, donc pas Mira. Supprime-le et installe AltStore Classic par AltServer (étape 2).
- **AltServer ne voit pas l’iPhone** : vérifie qu’iTunes et iCloud viennent bien du site d’Apple, débranche et rebranche l’iPhone déverrouillé.
- **Mira dit que Jellyfin est injoignable** : à la maison, vérifie le Wi-Fi et l’accès au réseau local (Réglages → Apps → Mira → Réseau local) ; ailleurs, active Tailscale.
- Guide officiel et dépannage : <https://faq.altstore.io/altstore-classic/how-to-install-altstore-windows>, <https://faq.altstore.io/altstore-classic/troubleshooting-guide>.

## Plus tard : AltStore PAL

Le jour où un compte Apple Developer (99 USD par an) est pris, la même app peut partir sur **AltStore PAL** : tes amis l’installent sans ordinateur et elle n’expire plus. Rien à refaire dans Mira ; détail dans l’étude `ameliorations/app-iphone.md` du projet.
