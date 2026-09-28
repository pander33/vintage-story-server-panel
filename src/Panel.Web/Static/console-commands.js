'use strict';

// Встроенные команды сервера Vintage Story — источник подсказок для автодополнения произвольной
// консоли (интерфейс 2.1 §2.1). Список составлен по документации команд игры и требует сверки
// с «/help» на целевом сервере (открытый вопрос плана); демон и панель его не проверяют — это
// только текст подсказки, само выполнение проверяют RawConsoleLine и демон.
const commandText = key => I18N.t(`commands.${key}`);
window.VS_CONSOLE_COMMANDS = [
	{command: '/time', syntax: commandText('timeSetSyntax'), description: commandText('timeSet'), args: ['choice:morning,noon,evening,midnight']},
	{command: '/time', syntax: commandText('timeSpeedSyntax'), description: commandText('timeSpeed'), args: ['number']},
	{command: '/weather', syntax: commandText('weatherSyntax'), description: commandText('weather'), args: ['choice:clear,cloudy,rain,storm']},
	{command: '/tp', syntax: commandText('tpSyntax'), description: commandText('tp'), args: ['player', 'number', 'number', 'number']},
	{command: '/tpto', syntax: commandText('tptoSyntax'), description: commandText('tpto'), args: ['player']},
	{command: '/gamemode', syntax: commandText('gamemodeSyntax'), description: commandText('gamemode'), args: ['choice:survival,creative,spectator,guest', 'player']},
	{command: '/whitelist', syntax: commandText('whitelistAddSyntax'), description: commandText('whitelistAdd'), args: ['player']},
	{command: '/whitelist', syntax: commandText('whitelistRemoveSyntax'), description: commandText('whitelistRemove'), args: ['player']},
	{command: '/whitelist', syntax: '/whitelist on|off', description: commandText('whitelistToggle'), args: ['choice:on,off']},
	{command: '/ban', syntax: commandText('banSyntax'), description: commandText('ban'), args: ['player', 'number', 'text']},
	{command: '/unban', syntax: commandText('unbanSyntax'), description: commandText('unban'), args: ['player']},
	{command: '/kick', syntax: commandText('kickSyntax'), description: commandText('kick'), args: ['player', 'text']},
	{command: '/op', syntax: commandText('opSyntax'), description: commandText('op'), args: ['player']},
	{command: '/deop', syntax: commandText('deopSyntax'), description: commandText('deop'), args: ['player']},
	{command: '/list', syntax: '/list players|banned|whitelisted|ops', description: commandText('list'), args: ['choice:players,banned,whitelisted,ops']},
	{command: '/announce', syntax: commandText('announceSyntax'), description: commandText('announce'), args: ['text']},
	{command: '/say', syntax: commandText('saySyntax'), description: commandText('say'), args: ['text']},
	{command: '/stop', syntax: commandText('stopSyntax'), description: commandText('stop'), args: ['number']},
	{command: '/genstructure', syntax: commandText('genstructureSyntax'), description: commandText('genstructure'), args: ['text', 'number', 'number', 'number']},
	{command: '/worldconfig', syntax: commandText('worldconfigSyntax'), description: commandText('worldconfig'), args: ['text', 'text']},
	{command: '/clear', syntax: commandText('clearSyntax'), description: commandText('clear'), args: ['player']},
	{command: '/give', syntax: commandText('giveSyntax'), description: commandText('give'), args: ['player', 'text', 'number']},
	{command: '/health', syntax: commandText('healthSyntax'), description: commandText('health'), args: ['player', 'number']},
	{command: '/entitystats', syntax: commandText('entitystatsSyntax'), description: commandText('entitystats'), args: ['player', 'text', 'number']},
	{command: '/killnpc', syntax: commandText('killnpcSyntax'), description: commandText('killnpc'), args: ['number']},
	{command: '/spawnpoint', syntax: commandText('spawnpointSyntax'), description: commandText('spawnpoint'), args: ['player']},
	{command: '/waypoint', syntax: commandText('waypointSyntax'), description: commandText('waypoint'), args: ['text', 'text', 'text']},
	{command: '/playerdata', syntax: commandText('playerdataSyntax'), description: commandText('playerdata'), args: ['player']},
	{command: '/roleedit', syntax: commandText('roleeditSyntax'), description: commandText('roleedit'), args: ['text', 'text', 'choice:true,false']},
	{command: '/serverconfig', syntax: commandText('serverconfigSyntax'), description: commandText('serverconfig'), args: ['text', 'text']},
];
