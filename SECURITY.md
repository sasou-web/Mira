# Signaler un problème de sécurité

Pour une vulnérabilité, utiliser **Security → Report a vulnerability** sur le dépôt GitHub. Ne pas publier de jeton, fichier `session.protected`, cache de bibliothèque ou mot de passe dans une issue publique. Si le signalement privé n’est pas disponible, ouvrir uniquement une issue demandant un canal de contact, sans détail exploitable ni donnée personnelle.

La version de développement publiée la plus récente est la seule version suivie. Il n’y a pas encore de délai de correction garanti ni de mise à jour automatique.

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
