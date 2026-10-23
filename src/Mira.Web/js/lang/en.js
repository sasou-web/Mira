// English of Mira web, keyed by the French text of the code (see ../i18n.js), one file per part of the app.
import shell from './en/shell.js';
import browse from './en/browse.js';
import player from './en/player.js';
import torlink from './en/torlink.js';
import guests from './en/guests.js';

export default { ...shell, ...browse, ...player, ...torlink, ...guests };
