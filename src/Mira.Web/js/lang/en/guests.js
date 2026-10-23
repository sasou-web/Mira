// English of Mira web, keyed by the French text of the code (see ../../i18n.js): « Inviter par lien », the guest's
// screens and the host's links.
export default {
  // guests.js
  'Le lien ouvre Mira chez ton invité, sans compte ni Tailscale, et le fait entrer dans ton groupe. Il ne sert qu’à un seul appareil, celui qui l’ouvre en premier. Tu peux le retirer à tout moment ; il s’efface seul après 7 jours sans servir.':
    'The link opens Mira for your guest, with no account or Tailscale, and brings them into your group. It works on one device only, the first to open it. You can remove it at any time; it deletes itself after 7 days unused.',
  'Regarde avec moi sur Mira : {link}': 'Watch with me on Mira: {link}',
  'Lien copié : envoie-le à {name}.': 'Link copied: send it to {name}.',
  'Adresse publique de Mira': 'Public address of Mira',
  Enregistrer: 'Save',
  'Pour qu’un lien marche depuis Internet, ouvre Mira au monde par Tailscale Funnel sur le PC de Jellyfin, dans PowerShell :':
    'For a link to work from the Internet, open Mira to the world with Tailscale Funnel on the Jellyfin PC, in PowerShell:',
  'Funnel n’ouvre que Mira, jamais le reste de Jellyfin. Écris ici l’adresse qu’il affiche :': 'Funnel opens only Mira, never the rest of Jellyfin. Enter the address it shows here:',
  'Son prénom': 'Their first name',
  'Prénom de ton invité': 'Your guest’s first name',
  'Créer le lien': 'Create the link',
  'Création du compte de l’invité…': 'Creating the guest’s account…',
  'Envoie ce lien à {name} : il ouvre Mira et rejoint ton groupe.': 'Send this link to {name}: it opens Mira and joins your group.',
  'Pas encore ouvert · s’efface seul le {date}': 'Not opened yet · deletes itself on {date}',
  'un appareil': 'a device',
  'Ouvert sur {device} · a servi à {time} · s’efface seul après 7 jours sans servir': 'Opened on {device} · last used at {time} · deletes itself after 7 days unused',
  'Ouvert sur {device} · a servi le {date} · s’efface seul le {end} sans servir': 'Opened on {device} · last used on {date} · deletes itself on {end} if unused',
  // invite.js
  'Sans compte ni Tailscale ?': 'No account or Tailscale?',
  'Crée un lien pour quelqu’un sans compte depuis Réglages → Liens d’invités.': 'Create a link for someone without an account in Settings → Guest links.',
  // views/guest.js
  'Ce lien ne marche plus : demande-en un nouveau à la personne qui t’a invité.': 'This link no longer works: ask the person who invited you for a new one.',
  'Tu entres dans la soirée…': 'Joining the watch party…',
  'Le lien n’a pas marché': 'The link didn’t work',
  'Ce lien est pour ton invité': 'This link is for your guest',
  'Tu es déjà connecté à Mira ici. Envoie ce lien à la personne que tu invites : il ne sert qu’à un seul appareil.':
    'You’re already signed in to Mira here. Send this link to the person you’re inviting: it works on one device only.',
  'La soirée est finie pour ce lien': 'This link’s watch party is over',
  // views/guests.js
  'Lecture des liens…': 'Reading the links…',
  'Liens d’invités': 'Guest links',
  'Aucun lien en cours.': 'No links at the moment.',
  '{n} lien en cours.': ['{n} link at the moment.', '{n} links at the moment.'],
  'Adresse des liens': 'Address of the links',
  'Nouveau lien': 'New link',
  'Liens en cours': 'Current links',
  'Le lien de {name}': '{name}’s link',
  Lien: 'Link',
  'Retirer le lien de {name}': 'Remove {name}’s link',
  Retirer: 'Remove',
  'Adresse enregistrée.': 'Address saved.',
  Changer: 'Change',
  'Changer l’adresse des liens': 'Change the address of the links',
  'Un autre lien': 'Another link',
  'Retirer ce lien ?': 'Remove this link?',
  '{name} sort du groupe tout de suite et ne pourra plus revenir avec ce lien. Son compte est supprimé de Jellyfin.':
    '{name} leaves the group right away and can’t come back with this link. Their account is deleted from Jellyfin.',
  'Le lien de {name} ne marchera plus. Son compte est supprimé de Jellyfin.': '{name}’s link will stop working. Their account is deleted from Jellyfin.',
  'Retirer le lien': 'Remove the link',
  'Lien de {name} retiré.': '{name}’s link removed.',
  // views/join.js
  'Garde cette page ouverte : le titre démarre ici dès que {name} le lance, au même moment chez chacun.':
    'Keep this page open: the title starts here as soon as {name} plays it, at the same moment for everyone.',
  'Tu as quitté le groupe': 'You left the group',
  'Revenir dans le groupe': 'Back to the group',
  // views/settings.js
  'Regarder ensemble avec quelqu’un sans compte ni Tailscale.': 'Watch together with someone who has no account or Tailscale.',
};
