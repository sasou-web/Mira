# L’extension Mira sur Jellyfin (et la fin de Mira web)

**Mira web est terminée.** Jusqu’à la 0.9.3, ton serveur Jellyfin servait Mira aux téléphones, dans Safari ou Chrome, à `http://<serveur>:8096/Mira`. Mira se concentre maintenant sur ses apps : [Windows](INSTALLATION.md), [Mac](INSTALLATION-MAC.md) et [iPhone](INSTALLATION-IOS.md). Sur Android, pas de Mira pour l’instant. Une icône de Mira web gardée sur l’écran d’accueil d’un téléphone ouvre maintenant une page qui le dit et mène à <https://miracinema.app> : tu peux la retirer.

L’extension, elle, reste sur Jellyfin. Elle sert :

- **ta party** : ton lien pour regarder ensemble, sa page (avec l’aperçu dans Discord ou Messages) et les invités sans compte, qui passent par sa passerelle ;
- **Télécharger sur le PC serveur** depuis ton iPhone, ton Mac ou un autre PC (son relais vers Mira pour Windows sur le PC de Jellyfin) ;
- **les sous-titres automatiques** et l’**Identifier…** des titres mal reconnus ;
- les sous-titres à l’heure d’une conversion pour l’app iPhone.

## Avec Mira pour Windows : tout en un clic

Sur le PC où tourne Jellyfin :

1. Ouvre le **Guide** (bouton **?** à gauche, ou F1).
2. La carte **Mira sur ton iPhone** coche ce qui est prêt et numérote ce qui reste :
   - **l’extension Mira sur Jellyfin** ;
   - **le pare-feu de Windows** : il laisse passer ton iPhone, sur le port de Jellyfin seulement, depuis ton Wi-Fi et Tailscale, jamais sur un réseau public : si Windows compte le Wi-Fi de ta box comme public, **Passer en privé** le change, avec une autorisation de Windows (seulement chez toi : le réseau d’un café doit rester public) ;
   - **hors de chez toi (facultatif)** : Tailscale, gratuit, sur ce PC ; sur l’iPhone, Tailscale intégré à Mira suffit ;
   - **dès l’allumage du PC** : Jellyfin démarre avec Windows, avant ta session (un démarrage différé choisi dans Windows est gardé), et repart après une erreur, trois fois au plus avant une journée sans erreur (avec son service Windows).
3. Clique sur **Tout préparer**. Windows demande une seule autorisation, au nom de **Windows PowerShell** (Microsoft), qui règle le pare-feu et le service ; puis Jellyfin redémarre quelques secondes.
4. La carte donne l’adresse à taper dans Mira sur l’iPhone ; **Installer Mira sur l’iPhone** ouvre [le guide d’installation](INSTALLATION-IOS.md).

**Ne plus ouvrir Jellyfin au réseau**, sous les étapes, retire la règle « Mira - Jellyfin » du pare-feu (une autorisation de Windows) ; **Tout préparer** la remet.

Il faut être connecté avec le compte administrateur de Jellyfin, ce qui est le cas si Mira a installé Jellyfin.

Avant d’installer l’extension, Mira vérifie sur GitHub qu’elle est bien celle de sa version, signée comme ses propres mises à jour, dans une release que plus personne ne peut modifier ; sinon, il n’installe rien et le guide dit pourquoi. Le dépôt qu’il ajoute à Jellyfin ne propose que cette version vérifiée (et remplace un dépôt de Mira ajouté à la main). Ensuite, l’extension suit la version de Mira pour Windows : quand Mira se met à jour, ouvre-le une fois avec le compte administrateur de Jellyfin ; il fait passer Jellyfin à la nouvelle version et le redémarre dès que personne ne regarde.

## Sans Mira pour Windows

Depuis la page web de Jellyfin :

1. **☰ → Tableau de bord → Extensions → Dépôts**, puis **+** :
   - nom : `Mira` ;
   - URL : `https://github.com/sasou-web/Mira/releases/latest/download/jellyfin-manifest.json`.
2. **Catalogue** : choisis **Mira**, puis **Installer**.
3. Redémarre Jellyfin (**Tableau de bord → Redémarrer**).

Jellyfin met ensuite l’extension à jour tout seul à chaque nouvelle version de Mira. Il ne vérifie que l’empreinte donnée par la même release de GitHub : il fait confiance à GitHub et au compte qui publie Mira. Avec un PC Windows, Mira pour Windows (ci-dessus) fait plus sûr.

**Hors de chez toi** : installe Tailscale sur le serveur. Dans Jellyfin, **Tableau de bord → Réseau → LAN networks**, ajoute `100.64.0.0/10` (avec, si la liste était vide, `127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12` et `192.168.0.0/16`). Sous Windows, le pare-feu doit laisser entrer `jellyfin.exe` sur son port depuis `100.64.0.0/10`.

## Si quelque chose ne va pas

- **Mira sur l’iPhone ne trouve pas le serveur** : ouvre le **Guide** de Mira pour Windows : la carte **Mira sur ton iPhone** dit ce qui manque (le plus souvent le pare-feu : **Tout préparer**).
- **Ta party ou Télécharger sur le PC serveur disent que l’extension manque** : regarde dans **Tableau de bord → Extensions → Mes extensions** que **Mira** y figure, « Actif », puis redémarre Jellyfin.
- **L’extension garde l’ancienne version après une mise à jour** : ouvre d’abord Mira pour Windows à jour, avec le compte administrateur de Jellyfin. Pour l’avoir tout de suite : **Tableau de bord → Tâches planifiées → Mettre à jour les extensions** (▶), puis **Tableau de bord → Redémarrer**.
