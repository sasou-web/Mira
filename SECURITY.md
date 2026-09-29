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

L’exécutable de développement n’est pas signé. Les releases incluent un fichier SHA-256 pour vérifier le téléchargement ; cette empreinte ne remplace pas une signature de l’éditeur.
