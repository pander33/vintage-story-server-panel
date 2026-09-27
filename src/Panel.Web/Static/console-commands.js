'use strict';

// Встроенные команды сервера Vintage Story — источник подсказок для автодополнения произвольной
// консоли (интерфейс 2.1 §2.1). Список составлен по документации команд игры и требует сверки
// с «/help» на целевом сервере (открытый вопрос плана); демон и панель его не проверяют — это
// только текст подсказки, само выполнение проверяют RawConsoleLine и демон.
window.VS_CONSOLE_COMMANDS = [
	{command: '/time', syntax: '/time set <час|morning|noon|evening|midnight>', description: 'Установить время суток', args: ['choice:morning,noon,evening,midnight']},
	{command: '/time', syntax: '/time speed <множитель>', description: 'Скорость течения времени', args: ['number']},
	{command: '/weather', syntax: '/weather set <ясно|облачно|дождь|шторм>', description: 'Установить погоду', args: ['choice:clear,cloudy,rain,storm']},
	{command: '/tp', syntax: '/tp <игрок> <x> <y> <z>', description: 'Телепортировать игрока в точку', args: ['player', 'number', 'number', 'number']},
	{command: '/tpto', syntax: '/tpto <игрок>', description: 'Телепортироваться к игроку', args: ['player']},
	{command: '/gamemode', syntax: '/gamemode <выживание|творческий|наблюдатель|гость> [игрок]', description: 'Сменить игровой режим', args: ['choice:survival,creative,spectator,guest', 'player']},
	{command: '/whitelist', syntax: '/whitelist add <игрок>', description: 'Добавить игрока в белый список', args: ['player']},
	{command: '/whitelist', syntax: '/whitelist remove <игрок>', description: 'Убрать игрока из белого списка', args: ['player']},
	{command: '/whitelist', syntax: '/whitelist on|off', description: 'Включить или выключить проверку по белому списку', args: ['choice:on,off']},
	{command: '/ban', syntax: '/ban <игрок> [минуты] [причина]', description: 'Заблокировать игрока', args: ['player', 'number', 'text']},
	{command: '/unban', syntax: '/unban <игрок>', description: 'Снять блокировку', args: ['player']},
	{command: '/kick', syntax: '/kick <игрок> [причина]', description: 'Отключить игрока от сервера', args: ['player', 'text']},
	{command: '/op', syntax: '/op <игрок>', description: 'Выдать права оператора', args: ['player']},
	{command: '/deop', syntax: '/deop <игрок>', description: 'Снять права оператора', args: ['player']},
	{command: '/list', syntax: '/list players|banned|whitelisted|ops', description: 'Показать список игроков или записей', args: ['choice:players,banned,whitelisted,ops']},
	{command: '/announce', syntax: '/announce <текст>', description: 'Объявление всем игрокам', args: ['text']},
	{command: '/say', syntax: '/say <текст>', description: 'Сообщение в чат от имени сервера', args: ['text']},
	{command: '/stop', syntax: '/stop [секунды]', description: 'Остановить сервер с задержкой', args: ['number']},
	{command: '/genstructure', syntax: '/genstructure <код> <x> <y> <z>', description: 'Сгенерировать структуру в точке', args: ['text', 'number', 'number', 'number']},
	{command: '/worldconfig', syntax: '/worldconfig <ключ> <значение>', description: 'Изменить параметр живой конфигурации мира', args: ['text', 'text']},
	{command: '/clear', syntax: '/clear [игрок]', description: 'Очистить инвентарь игрока', args: ['player']},
	{command: '/give', syntax: '/give <игрок> <код предмета> [количество]', description: 'Выдать предмет игроку', args: ['player', 'text', 'number']},
	{command: '/health', syntax: '/health <игрок> <значение>', description: 'Задать здоровье игрока', args: ['player', 'number']},
	{command: '/entitystats', syntax: '/entitystats <игрок> <характеристика> <значение>', description: 'Изменить характеристику игрока (скорость, урон и т. п.)', args: ['player', 'text', 'number']},
	{command: '/killnpc', syntax: '/killnpc <радиус>', description: 'Убить всех существ в радиусе', args: ['number']},
	{command: '/spawnpoint', syntax: '/spawnpoint [игрок]', description: 'Установить точку возрождения в текущей позиции', args: ['player']},
	{command: '/waypoint', syntax: '/waypoint addati <иконка> <цвет> <заголовок>', description: 'Добавить путевую точку', args: ['text', 'text', 'text']},
	{command: '/playerdata', syntax: '/playerdata <игрок>', description: 'Показать сведения об игроке', args: ['player']},
	{command: '/roleedit', syntax: '/roleedit <роль> <право> <true|false>', description: 'Изменить права роли', args: ['text', 'text', 'choice:true,false']},
	{command: '/serverconfig', syntax: '/serverconfig <ключ> <значение>', description: 'Изменить параметр serverconfig.json на лету', args: ['text', 'text']},
];
