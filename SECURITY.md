# Signaler un problème de sécurité

Pour une vulnérabilité, utiliser **Security → Report a vulnerability** sur le dépôt GitHub. Ne pas publier de jeton, fichier `session.protected`, cache de bibliothèque ou mot de passe dans une issue publique. Si le signalement privé n’est pas disponible, ouvrir uniquement une issue demandant un canal de contact, sans détail exploitable ni donnée personnelle.

La version de développement publiée la plus récente est la seule version suivie. Il n’y a pas encore de délai de correction garanti. À partir de la 0.5.2, les copies installées se mettent à jour d’elles-mêmes (voir plus bas).

## Mises à jour automatiques

- Mira interroge uniquement l’API des releases GitHub du dépôt (`api.github.com`) et télécharge les fichiers de release depuis `github.com`, puis les serveurs de fichiers de GitHub (`release-assets.githubusercontent.com`, `objects.githubusercontent.com`), en HTTPS. Chaque redirection est vérifiée avant d’être suivie ; toute autre adresse est refusée. Aucun identifiant ni donnée de bibliothèque n’est envoyé : seulement l’en-tête `User-Agent: Mira/<version>`.
- Une version n’est installée que si `mira-update.json`, publié avec la release, porte une signature ECDSA P-256 valide d’une clé intégrée à Mira (`UpdateKeys.Trusted`), puis si le fichier téléchargé a exactement la taille et l’empreinte SHA-256 de ce manifeste. Une release non signée, modifiée, d’une autre version ou plus ancienne que la copie en cours n’est pas installée. Juste avant l’installation, le fichier est vérifié de nouveau, et pour une archive chaque fichier décompressé est comparé au contenu de l’archive.
- La clé privée ne quitte pas le PC du mainteneur : `tools/Mira.Release` la garde chiffrée par Windows (DPAPI, compte courant) dans `%APPDATA%\Mira Release`, hors du dépôt ; une copie de secours se fait avec une phrase secrète. DPAPI protège la clé d’une copie du fichier, pas d’un programme lancé sous le même compte Windows : signer depuis un compte réservé aux releases réduit ce risque. Sans cette clé, une nouvelle release n’est pas proposée aux copies installées ; si elle était perdue, une release signée par une nouvelle clé devrait être installée une fois à la main.
- Les téléchargements attendent dans `data\updates`, dans le profil de la copie, et seule la dernière version proposée y est gardée. L’installation reprend le même chemin que la copie : installateur Inno Setup silencieux pour l’utilisateur courant, ou nouvel exécutable qui attend la fermeture de Mira. Pour un dossier, les nouveaux fichiers sont d’abord copiés à côté des anciens, puis échangés par renommages ; en cas d’échec, les anciens reviennent, et une copie de retour arrière incomplète est gardée dans `data\updates`. Le dossier `data` n’est jamais modifié ailleurs que dans `data\updates`. Mira attend la fin d’une installation en cours avant de démarrer. Aucune installation ne démarre pendant l’arrêt de Windows ; une installation qui échoue deux fois n’est plus relancée à chaque fermeture.
- Les exécutables restent non signés : la vérification de Mira ne remplace pas une signature Authenticode, et SmartScreen ne s’applique pas aux fichiers téléchargés par Mira lui-même.

## Données traitées par l’application

- Le mot de passe sert à l’authentification Jellyfin et n’est pas conservé.
- Le jeton de session est protégé par Windows DPAPI, pour l’utilisateur Windows courant.
- Le dossier `data` contient aussi les préférences, un identifiant de périphérique, les images, le cache de bibliothèque et les événements de lecture en attente. Le cache n’est pas chiffré.
- Les échanges se font avec le serveur Jellyfin configuré. Le client envoie sa progression et ses changements de favoris/statut vu. Pour un serveur distant, utiliser HTTPS.
- Le protocole WebSocket Jellyfin requiert le jeton dans l’URL de connexion ; Mira ne journalise pas cette URL.
- Mira charge une bibliothèque native libmpv choisie sur le PC. Utiliser un moteur provenant d’une source de confiance.
- La page TorLink exécute le TorLink installé sur le PC, avec son Node.js : ce code n’est pas vérifié par Mira. Utiliser une installation de confiance.
- Mira lit l’état de TorLink (configuration, historique, file, fichiers `.torrent`) sans le modifier. `data/torlink-imports.json` garde les noms et chemins des téléchargements rangés, sans lien magnet ; `data/webview2` est le profil du terminal intégré.
- Le terminal est une page locale sans accès réseau, qui n’échange avec Mira que du texte de terminal. Mira n’ouvre aucun port ; le jeton Jellyfin n’est transmis ni à TorLink ni à la page.
- TorLink reste un client BitTorrent : tant qu’il tourne, y compris Mira réduit, il se connecte à des pairs, écoute un port et peut demander au routeur de l’ouvrir (UPnP, NAT-PMP), selon ses propres réglages.
- Le rangement ne crée que des fichiers vidéo et sous-titres, dans les dossiers de bibliothèque. Il ne remplace jamais un fichier existant et ne déplace jamais un fichier qu’il n’a pas placé ; en mode déplacement, le téléchargement quitte le dossier de TorLink.

L’installateur, l’exécutable portable et l’archive ne sont pas signés : Windows SmartScreen peut demander une confirmation. Chaque fichier de release a son empreinte SHA-256 pour vérifier le téléchargement ; elle ne remplace pas une signature de l’éditeur. L’installateur n’a pas besoin des droits administrateur et n’écrit que dans le profil de l’utilisateur courant (`%LOCALAPPDATA%\Programs\Mira`, menu Démarrer, entrée de désinstallation). L’exécutable portable laisse .NET extraire ses bibliothèques natives dans `%TEMP%\.net`.
