using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Enums;

namespace TaskManagerBot
{
    public class BotService
    {
        private readonly TelegramBotClient _botClient;
        private readonly DataService _dataService;

        // ✅ AI (Ollama phi3:mini)
        private readonly AiService _aiService = new AiService();

        private readonly Dictionary<long, string> _userStates = new();
        private readonly Dictionary<long, TaskModel> _tempTasks = new();

        // === Clean chat (delete old messages) ===
        private readonly Dictionary<long, List<int>> _botMsgIds = new();
        private readonly Dictionary<long, List<int>> _userMsgIds = new();

        // 0 = удаляем всё старое, оставляем только новое сообщение бота
        private const int KeepLastBotMessages = 0;
        private const int KeepLastUserMessages = 0;

        // === States ===
        private const string ST_AWAITING_TAG = "awaiting_tag";
        private const string ST_MAIN_MENU = "main_menu";

        private const string ST_ADD_TITLE = "add_task_title";
        private const string ST_ADD_TASKDESC = "add_task_taskdesc";
        private const string ST_ADD_PICK = "add_task_pick_emps";
        private const string ST_ADD_DEADLINE = "add_task_deadline";
        private const string ST_ADD_DEADLINE_TIME_TEXT = "add_task_deadline_time_text";

        // Old legacy state (оставляем строку, но сценарий больше не использует)
        private const string ST_ADD_EMPLOYEES_LEGACY = "add_task_employees";

        // for AI standalone pick
        private const string ST_AI_PICK_WAIT_DESC = "ai_pick_wait_desc";

        // ✅ deadline picker draft: выбранная дата (потом дополняем временем)
        private readonly Dictionary<long, DateTime> _deadlineDraft = new();

        // cache last AI list per chat
        private readonly Dictionary<long, List<string>> _lastAiSuggested = new();

        public BotService(string token)
        {
            _botClient = new TelegramBotClient(token);
            _dataService = DataService.Instance;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var me = await _botClient.GetMeAsync(cancellationToken);
            Console.WriteLine($"Бот запущен: {me.Username}");

            _botClient.StartReceiving(
                HandleUpdateAsync,
                HandleErrorAsync,
                cancellationToken: cancellationToken
            );
        }

        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Message != null)
                    await HandleMessageAsync(update.Message, cancellationToken);
                else if (update.CallbackQuery != null)
                    await HandleCallbackQueryAsync(update.CallbackQuery, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex}");
            }
        }

        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Ошибка: {exception}");
            return Task.CompletedTask;
        }

        // =========================
        // Clean chat helpers
        // =========================
        private void Track(Dictionary<long, List<int>> store, long chatId, int messageId)
        {
            if (messageId == 0) return;

            if (!store.TryGetValue(chatId, out var list))
            {
                list = new List<int>();
                store[chatId] = list;
            }

            list.Add(messageId);

            if (list.Count > 50)
                list.RemoveRange(0, list.Count - 50);
        }

        private async Task CleanupAsync(long chatId, CancellationToken ct)
        {
            if (_botMsgIds.TryGetValue(chatId, out var botList))
            {
                var toDelete = botList.Take(Math.Max(0, botList.Count - KeepLastBotMessages)).ToList();
                foreach (var id in toDelete)
                {
                    try { await _botClient.DeleteMessageAsync(chatId, id, ct); } catch { }
                }
                botList.RemoveAll(id => toDelete.Contains(id));
            }

            if (_userMsgIds.TryGetValue(chatId, out var userList))
            {
                var toDelete = userList.Take(Math.Max(0, userList.Count - KeepLastUserMessages)).ToList();
                foreach (var id in toDelete)
                {
                    try { await _botClient.DeleteMessageAsync(chatId, id, ct); } catch { }
                }
                userList.RemoveAll(id => toDelete.Contains(id));
            }
        }

        private async Task<Message> SendCleanAsync(
            long chatId,
            string text,
            CancellationToken ct,
            IReplyMarkup? replyMarkup = null,
            ParseMode? parseMode = null
        )
        {
            await CleanupAsync(chatId, ct);

            var sent = await _botClient.SendTextMessageAsync(
                chatId,
                text,
                replyMarkup: replyMarkup,
                parseMode: parseMode,
                cancellationToken: ct
            );

            Track(_botMsgIds, chatId, sent.MessageId);
            return sent;
        }

        // =========================
        // Navigation buttons
        // =========================
        private InlineKeyboardMarkup GetHomeBackButtons()
        {
            return new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") },
                new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "back") }
            });
        }

        private InlineKeyboardMarkup GetHomeOnlyButton()
        {
            return new InlineKeyboardMarkup(
                InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu")
            );
        }

        // сбрасываем режим "добавление задачи", когда уходим в меню
        private void CancelAddTaskFlow(long chatId)
        {
            if (_userStates.TryGetValue(chatId, out var st))
            {
                if (st.StartsWith("add_task_", StringComparison.Ordinal))
                {
                    _tempTasks.Remove(chatId);
                    _deadlineDraft.Remove(chatId);
                    _userStates[chatId] = ST_MAIN_MENU;
                }
            }
        }

        private void ResetToMainMenuState(long chatId)
        {
            _tempTasks.Remove(chatId);
            _deadlineDraft.Remove(chatId);
            _userStates[chatId] = ST_MAIN_MENU;
        }

        private static string NormalizeTagNoAt(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            var s = tag.Trim();
            if (s.StartsWith("@")) s = s.Substring(1);
            return s.ToLowerInvariant();
        }

        private static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");

        private bool IsUserAssignedToTask(User user, TaskModel task)
        {
            if (task.AssignedEmployeeTags == null || task.AssignedEmployeeTags.Count == 0) return false;
            var u = NormalizeTagNoAt(user.TelegramTag);
            return task.AssignedEmployeeTags.Any(t => NormalizeTagNoAt(t) == u);
        }

        // =========================
        // Message handling
        // =========================
        private async Task HandleMessageAsync(Message message, CancellationToken cancellationToken)
        {
            var chatId = message.Chat.Id;
            var text = message.Text ?? string.Empty;

            Track(_userMsgIds, chatId, message.MessageId);

            // аварийный выход в меню текстом
            var t = text.Trim().ToLower();
            if (t == "/menu" || t == "меню")
            {
                var userMenu = _dataService.Users.Values.FirstOrDefault(u => u.TelegramId == chatId);
                if (userMenu != null)
                {
                    ResetToMainMenuState(chatId);
                    await ShowMainMenu(chatId, "Главное меню:", userMenu.Role, cancellationToken);
                }
                return;
            }

            if (text == "/start")
            {
                _userStates.Remove(chatId);
                _tempTasks.Remove(chatId);
                _deadlineDraft.Remove(chatId);

                await SendCleanAsync(
                    chatId,
                    "Добро пожаловать в Task Manager Bot!\n\nПожалуйста, введите ваш Telegram тег (например, @username):",
                    cancellationToken,
                    replyMarkup: null
                );

                _userStates[chatId] = ST_AWAITING_TAG;
                return;
            }

            if (!_userStates.ContainsKey(chatId))
                return;

            var state = _userStates[chatId];

            // AI: ждём описание задачи (standalone)
            if (state == ST_AI_PICK_WAIT_DESC)
            {
                ResetToMainMenuState(chatId);
                await HandleAiPick(chatId, text, cancellationToken);
                return;
            }

            // ручной ввод времени для дедлайна
            if (state == ST_ADD_DEADLINE_TIME_TEXT)
            {
                if (!_deadlineDraft.TryGetValue(chatId, out var date))
                    date = DateTime.Today;

                if (!TimeSpan.TryParseExact(text.Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out var ts))
                {
                    await SendCleanAsync(chatId, "Неверный формат. Введи время как ЧЧ:ММ (например, 09:30):", cancellationToken, replyMarkup: GetHomeOnlyButton());
                    return;
                }

                var deadline = new DateTime(date.Year, date.Month, date.Day, ts.Hours, ts.Minutes, 0);
                await FinalizeTaskWithDeadline(chatId, deadline, cancellationToken);
                return;
            }

            if (state == ST_AWAITING_TAG)
                await HandleTagInput(chatId, text, cancellationToken);
            else if (state == ST_ADD_TITLE)
                await HandleAddTaskTitle(chatId, text, cancellationToken);
            else if (state == ST_ADD_TASKDESC)
                await HandleAddTaskTaskDesc(chatId, text, cancellationToken);
            else if (state == ST_ADD_DEADLINE)
                await HandleAddTaskDeadline(chatId, text, cancellationToken);
            else if (state == ST_ADD_EMPLOYEES_LEGACY)
                await HandleAddTaskEmployees(chatId, text, cancellationToken);
        }

        private async Task HandleTagInput(long chatId, string tag, CancellationToken cancellationToken)
        {
            var user = _dataService.GetUserByTag(tag);

            if (user == null)
            {
                await SendCleanAsync(
                    chatId,
                    "Пользователь не найден. Пожалуйста, проверьте правильность тега и попробуйте снова.",
                    cancellationToken,
                    replyMarkup: null
                );
                return;
            }

            user.TelegramId = chatId;
            _dataService.AddUser(user);

            _userStates[chatId] = ST_MAIN_MENU;

            var welcomeText = $"Добро пожаловать, {user.FullName}!\nРоль: {(user.Role == Role.Manager ? "Начальник" : "Сотрудник")}";
            await ShowMainMenu(chatId, welcomeText, user.Role, cancellationToken);
        }

        private async Task ShowMainMenu(long chatId, string text, Role role, CancellationToken cancellationToken)
        {
            var buttons = new List<InlineKeyboardButton[]>();

            if (role == Role.Manager)
            {
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("👥 Работники", "employees") });
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("📋 Задачи", "tasks") });
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("👤 Мой профиль", "my_profile") });
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("➕ Добавить задачу", "add_task") });
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🤖 Подобрать исполнителя", "ai_pick") });
            }
            else
            {
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("📋 Задачи", "tasks") });
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("👤 Мой профиль", "my_profile") });
            }

            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 На главную", "main_menu") });

            await SendCleanAsync(
                chatId,
                text,
                cancellationToken,
                replyMarkup: new InlineKeyboardMarkup(buttons)
            );
        }

        // =========================
        // Callback handling
        // =========================
        private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var data = callbackQuery.Data ?? "";

            Track(_botMsgIds, chatId, callbackQuery.Message.MessageId);

            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

            var user = _dataService.Users.Values.FirstOrDefault(u => u.TelegramId == chatId);
            if (user == null) return;

            Console.WriteLine($"[CB] chat={chatId} data={data}");

            if (data == "noop")
                return;

            // ✅ КНОПКИ СТАТУСОВ ЗАДАЧ (только назначенный сотрудник)
            if (data.StartsWith("task_take:", StringComparison.Ordinal) || data.StartsWith("task_done:", StringComparison.Ordinal))
            {
                var isTake = data.StartsWith("task_take:", StringComparison.Ordinal);
                var taskId = data.Substring(isTake ? "task_take:".Length : "task_done:".Length).Trim();

                // грузим задачу через SQL (для проверки назначений)
                var all = _dataService.GetAllTasks();
                var task = all.FirstOrDefault(t => t.Id == taskId);

                if (task == null)
                {
                    await SendCleanAsync(chatId, "Задача не найдена.", cancellationToken, replyMarkup: GetHomeOnlyButton());
                    return;
                }

                if (!IsUserAssignedToTask(user, task))
                {
                    await SendCleanAsync(chatId, "❗ Ты не назначен на эту задачу — действие запрещено.", cancellationToken, replyMarkup: GetHomeOnlyButton());
                    return;
                }

                if (isTake)
                {
                    if (task.Status != TaskStatus.Waiting)
                    {
                        await SendCleanAsync(chatId, "Эту задачу нельзя взять в работу (статус уже не «В ожидании»).", cancellationToken, replyMarkup: GetHomeOnlyButton());
                    }
                    else
                    {
                        _dataService.UpdateTaskStatusForUser(taskId, user.TelegramTag, "InProgress");
                        await SendCleanAsync(chatId, $"✅ Ты взял задачу «{task.Title}» в работу.", cancellationToken, replyMarkup: GetHomeOnlyButton());
                    }
                }
                else
                {
                    if (task.Status != TaskStatus.InProgress)
                    {
                        await SendCleanAsync(chatId, "Эту задачу нельзя завершить (она не в статусе «В работе»).", cancellationToken, replyMarkup: GetHomeOnlyButton());
                    }
                    else
                    {
                        _dataService.UpdateTaskStatusForUser(taskId, user.TelegramTag, "Completed");
                        await SendCleanAsync(chatId, $"🎉 Задача «{task.Title}» завершена.", cancellationToken, replyMarkup: GetHomeOnlyButton());
                    }
                }

                await ShowTasks(chatId, user.Role == Role.Manager, cancellationToken);
                return;
            }

            // skills:<tg_no_at>
            if (data.StartsWith("skills:", StringComparison.Ordinal))
            {
                ResetToMainMenuState(chatId);
                var tgNoAt = data.Substring("skills:".Length).Trim();
                await ShowEmployeeSkills(chatId, tgNoAt, cancellationToken);
                return;
            }

            // ===== Calendar callbacks =====
            if (data.StartsWith("cal:", StringComparison.Ordinal))
            {
                var ym = data.Substring(4);
                var parts = ym.Split('-');
                var y = int.Parse(parts[0]);
                var m = int.Parse(parts[1]);
                await ShowCalendar(chatId, new DateTime(y, m, 1), cancellationToken);
                return;
            }

            if (data.StartsWith("cald:", StringComparison.Ordinal))
            {
                var s = data.Substring(5);
                var d = DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

                _deadlineDraft[chatId] = d.Date;
                await ShowTimePicker(chatId, d.Date, cancellationToken);
                return;
            }

            if (data.StartsWith("calt:", StringComparison.Ordinal))
            {
                if (!_deadlineDraft.TryGetValue(chatId, out var date))
                    date = DateTime.Today;

                var tm = data.Substring(5);
                var tparts = tm.Split(':');
                var hh = int.Parse(tparts[0]);
                var mm = int.Parse(tparts[1]);

                var deadline = new DateTime(date.Year, date.Month, date.Day, hh, mm, 0);
                await FinalizeTaskWithDeadline(chatId, deadline, cancellationToken);
                return;
            }

            if (data == "caltime_manual")
            {
                _userStates[chatId] = ST_ADD_DEADLINE_TIME_TEXT;
                await SendCleanAsync(chatId, "Введи время в формате ЧЧ:ММ (например, 18:30):", cancellationToken, replyMarkup: GetHomeOnlyButton());
                return;
            }
            // ===== end calendar callbacks =====

            // choose mode after task description
            if (data == "pick_choose_mode_ai")
            {
                await ShowPickEmployeesScreen(chatId, useAi: true, cancellationToken: cancellationToken, forceAi: true);
                return;
            }

            if (data == "pick_choose_mode_manual")
            {
                await ShowPickEmployeesScreen(chatId, useAi: false, cancellationToken: cancellationToken);
                return;
            }

            // add_task multi-pick callbacks
            if (data.StartsWith("pick_toggle:", StringComparison.Ordinal))
            {
                var tgNoAt = data.Substring("pick_toggle:".Length).Trim().ToLower();
                TogglePickedEmployee(chatId, tgNoAt);
                await ShowPickEmployeesScreen(chatId, useAi: true, cancellationToken: cancellationToken);
                return;
            }

            if (data == "pick_manual")
            {
                await ShowPickEmployeesScreen(chatId, useAi: false, cancellationToken: cancellationToken);
                return;
            }

            if (data == "pick_ai_refresh")
            {
                await ShowPickEmployeesScreen(chatId, useAi: true, cancellationToken: cancellationToken, forceAi: true);
                return;
            }

            if (data == "pick_done")
            {
                if (!_tempTasks.TryGetValue(chatId, out var task) || task.AssignedEmployeeTags == null || task.AssignedEmployeeTags.Count == 0)
                {
                    await SendCleanAsync(chatId,
                        "❗ Сначала выбери хотя бы одного исполнителя (можно несколько).",
                        cancellationToken,
                        replyMarkup: GetHomeOnlyButton());
                    return;
                }

                _userStates[chatId] = ST_ADD_DEADLINE;
                await ShowCalendar(chatId, DateTime.Now, cancellationToken);
                return;
            }

            if (data is "main_menu" or "employees" or "tasks" or "my_profile" or "back" or "ai_pick")
                CancelAddTaskFlow(chatId);

            switch (data)
            {
                case "main_menu":
                    ResetToMainMenuState(chatId);
                    await ShowMainMenu(chatId, "Главное меню:", user.Role, cancellationToken);
                    break;

                case "employees":
                    ResetToMainMenuState(chatId);
                    await ShowEmployees(chatId, cancellationToken);
                    break;

                case "tasks":
                    ResetToMainMenuState(chatId);
                    await ShowTasks(chatId, user.Role == Role.Manager, cancellationToken);
                    break;

                case "my_profile":
                    ResetToMainMenuState(chatId);
                    await ShowMyProfile(chatId, user, cancellationToken);
                    break;

                case "add_task":
                    await StartAddTask(chatId, cancellationToken);
                    break;

                case "ai_pick":
                    _tempTasks.Remove(chatId);
                    _deadlineDraft.Remove(chatId);
                    _userStates[chatId] = ST_AI_PICK_WAIT_DESC;
                    await SendCleanAsync(chatId,
                        "📝 Опиши задачу (1–3 предложения). Я подберу топ-3 исполнителей.",
                        cancellationToken,
                        replyMarkup: GetHomeOnlyButton());
                    break;

                case "back":
                    ResetToMainMenuState(chatId);
                    await ShowMainMenu(chatId, "Главное меню:", user.Role, cancellationToken);
                    break;
            }
        }

        // =========================
        // Employees + skills
        // =========================
        private async Task ShowEmployees(long chatId, CancellationToken cancellationToken)
        {
            var employees = _dataService.GetAllEmployees();
            if (!employees.Any())
            {
                await SendCleanAsync(chatId, "Сотрудники не найдены.", cancellationToken, replyMarkup: GetHomeOnlyButton());
                return;
            }

            var loads = _dataService.GetEmployeesLoadByTag();

            var text = "👥 Список сотрудников (нажми «📌 Навыки»):\n\n";
            var rows = new List<InlineKeyboardButton[]>();

            foreach (var e in employees)
            {
                var tagNoAt = (e.TelegramTag ?? "").Trim().TrimStart('@').ToLower();
                loads.TryGetValue(tagNoAt, out var cnt);

                text += $"• {e.FullName} — {cnt} активн. задач(и) ({e.TelegramTag})\n";

                rows.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData($"📌 Навыки: {e.FullName}", $"skills:{tagNoAt}")
                });
            }

            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "back") });

            await SendCleanAsync(
                chatId,
                text,
                cancellationToken,
                replyMarkup: new InlineKeyboardMarkup(rows)
            );
        }

        private async Task ShowEmployeeSkills(long chatId, string tgNoAt, CancellationToken cancellationToken)
        {
            var skillsMap = _dataService.GetEmployeesSkills();
            skillsMap.TryGetValue((tgNoAt ?? "").Trim().ToLower(), out var list);

            var text = $"📌 Навыки сотрудника @{tgNoAt}\n\n";

            if (list == null || list.Count == 0)
            {
                text += "Нет данных о навыках.\n";
            }
            else
            {
                foreach (var s in list)
                    text += $"• {s.skill} — {s.years} лет\n";
            }

            var kb = new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") },
                new [] { InlineKeyboardButton.WithCallbackData("👥 К сотрудникам", "employees") },
                new [] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "back") }
            });

            await SendCleanAsync(chatId, text, cancellationToken, replyMarkup: kb);
        }

        // =========================
        // Tasks (SQL) + buttons for employees
        // =========================
        private async Task ShowTasks(long chatId, bool isManager, CancellationToken cancellationToken)
        {
            var user = _dataService.Users.Values.FirstOrDefault(u => u.TelegramId == chatId);
            if (user == null)
            {
                await SendCleanAsync(chatId, "Пользователь не найден. Нажми /start.", cancellationToken, replyMarkup: GetHomeOnlyButton());
                return;
            }

            var tasks = isManager
                ? _dataService.GetAllTasks()
                : _dataService.GetUserTasks(user.TelegramTag);

            if (tasks == null || tasks.Count == 0)
            {
                await SendCleanAsync(chatId, "Задачи не найдены.", cancellationToken, replyMarkup: GetHomeBackButtons());
                return;
            }

            var rows = new List<InlineKeyboardButton[]>();
            var sb = new StringBuilder();
            sb.AppendLine("<b>📋 Список задач</b>\n");

            foreach (var task in tasks)
            {
                sb.AppendLine($"<b>{Html(task.Title)}</b>");

                var assigned = _dataService.GetUsersByTags(task.AssignedEmployeeTags ?? new List<string>());
                if (assigned.Any())
                {
                    sb.AppendLine("👥 " + Html(string.Join(", ", assigned.Select(e => e.FullName))));
                }

                var timeLeft = task.Deadline - DateTime.Now;
                if (timeLeft.TotalSeconds > 0)
                    sb.AppendLine($"⏳ Осталось: {timeLeft.Days} дн. {timeLeft.Hours} ч.");
                else
                    sb.AppendLine("🔴 <b>ПРОСРОЧЕНО!</b>");

                var statusText = task.Status switch
                {
                    TaskStatus.InProgress => "🟡 В работе",
                    TaskStatus.Completed => "🟢 Завершена",
                    TaskStatus.Waiting => "⚪ В ожидании",
                    TaskStatus.Cancelled => "🔴 Отменена",
                    _ => "Неизвестно"
                };
                sb.AppendLine($"📊 Статус: {Html(statusText)}");

                if (!string.IsNullOrWhiteSpace(task.Description))
                    sb.AppendLine("📝 " + Html(task.Description));

                // кнопки статуса: только сотруднику и только если он назначен
                if (!isManager && IsUserAssignedToTask(user, task))
                {
                    if (task.Status == TaskStatus.Waiting)
                    {
                        rows.Add(new[]
                        {
                            InlineKeyboardButton.WithCallbackData("▶️ Взять в работу", $"task_take:{task.Id}")
                        });
                    }
                    else if (task.Status == TaskStatus.InProgress)
                    {
                        rows.Add(new[]
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Завершить", $"task_done:{task.Id}")
                        });
                    }
                }

                sb.AppendLine("────────────");
            }

            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "back") });

            await SendCleanAsync(
                chatId,
                sb.ToString(),
                cancellationToken,
                replyMarkup: new InlineKeyboardMarkup(rows),
                parseMode: ParseMode.Html
            );
        }

        private async Task ShowMyProfile(long chatId, User user, CancellationToken cancellationToken)
        {
            var profileText =
                "👤 Мой профиль\n\n" +
                $"📝 ФИО: {user.FullName}\n" +
                $"🎂 Возраст: {user.Age}\n" +
                $"📱 Telegram: {user.TelegramTag}\n" +
                $"💼 Роль: {(user.Role == Role.Manager ? "Начальник" : "Сотрудник")}\n" +
                $"📋 Обо мне: {user.Bio}\n\n";

            var userTasks = _dataService.GetUserTasks(user.TelegramTag);
            if (userTasks.Any())
            {
                profileText += "📋 Мои задачи:\n";
                foreach (var tsk in userTasks)
                {
                    var icon = tsk.Status switch
                    {
                        TaskStatus.InProgress => "🟡",
                        TaskStatus.Completed => "🟢",
                        TaskStatus.Waiting => "⚪",
                        TaskStatus.Cancelled => "🔴",
                        _ => ""
                    };
                    profileText += $"{icon} {tsk.Title}\n";
                }
            }
            else
            {
                profileText += "📋 У вас пока нет задач.\n";
            }

            await SendCleanAsync(
                chatId,
                profileText,
                cancellationToken,
                replyMarkup: GetHomeBackButtons()
            );
        }

        // =========================
        // NEW ADD TASK FLOW (multi pick)
        // =========================
        private async Task StartAddTask(long chatId, CancellationToken cancellationToken)
        {
            _userStates[chatId] = ST_ADD_TITLE;

            // важно: задачу сразу создаём в Waiting, чтобы затем “взять в работу” имело смысл
            _tempTasks[chatId] = new TaskModel { Status = TaskStatus.Waiting };

            _deadlineDraft.Remove(chatId);
            await SendCleanAsync(chatId, "Введите название задачи:", cancellationToken, replyMarkup: GetHomeOnlyButton());
        }

        private async Task HandleAddTaskTitle(long chatId, string title, CancellationToken cancellationToken)
        {
            _tempTasks[chatId].Title = title;
            _userStates[chatId] = ST_ADD_TASKDESC;

            await SendCleanAsync(
                chatId,
                "Введите описание задачи (что нужно сделать, результат, требования):",
                cancellationToken,
                replyMarkup: GetHomeOnlyButton()
            );
        }

        private async Task HandleAddTaskTaskDesc(long chatId, string description, CancellationToken cancellationToken)
        {
            _tempTasks[chatId].Description = description;

            if (_tempTasks[chatId].AssignedEmployeeTags == null)
                _tempTasks[chatId].AssignedEmployeeTags = new List<string>();
            else
                _tempTasks[chatId].AssignedEmployeeTags.Clear();

            _userStates[chatId] = ST_ADD_PICK;

            var kb = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🤖 Помоги подобрать (AI)", "pick_choose_mode_ai") },
                new[] { InlineKeyboardButton.WithCallbackData("👤 Я сам выберу", "pick_choose_mode_manual") },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") }
            });

            await SendCleanAsync(
                chatId,
                "Выбери способ назначения исполнителей:\n\n" +
                "🤖 AI предложит кандидатов (ты выбираешь кнопками)\n" +
                "👤 Или выбери сам из полного списка.",
                cancellationToken,
                replyMarkup: kb
            );
        }

        private void TogglePickedEmployee(long chatId, string tgNoAt)
        {
            if (!_tempTasks.TryGetValue(chatId, out var task))
                return;

            if (task.AssignedEmployeeTags == null)
                task.AssignedEmployeeTags = new List<string>();

            var tag = "@" + (tgNoAt ?? "").Trim().TrimStart('@').ToLower();

            if (task.AssignedEmployeeTags.Contains(tag))
                task.AssignedEmployeeTags.Remove(tag);
            else
                task.AssignedEmployeeTags.Add(tag);
        }

        private async Task ShowPickEmployeesScreen(long chatId, bool useAi, CancellationToken cancellationToken, bool forceAi = false)
        {
            if (!_tempTasks.TryGetValue(chatId, out var task))
            {
                await SendCleanAsync(chatId, "Ошибка: задача не найдена.", cancellationToken, replyMarkup: GetHomeOnlyButton());
                return;
            }

            var employees = _dataService.GetAllEmployees();
            var load = _dataService.GetEmployeesLoadByTag();
            var skills = _dataService.GetEmployeesSkills();

            if (employees.Count == 0)
            {
                await SendCleanAsync(chatId, "Сотрудников нет в БД.", cancellationToken, replyMarkup: GetHomeOnlyButton());
                return;
            }

            List<string> listToShow;

            if (!useAi)
            {
                listToShow = employees
                    .Select(e => (e.TelegramTag ?? "").Trim().TrimStart('@').ToLower())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }
            else
            {
                if (!forceAi && _lastAiSuggested.TryGetValue(chatId, out var cached) && cached.Count > 0)
                {
                    listToShow = cached;
                }
                else
                {
                    await SendCleanAsync(chatId, "🤖 Подбираю исполнителей…", cancellationToken, replyMarkup: GetHomeOnlyButton());

                    var sb = new StringBuilder();
                    sb.AppendLine("Подбери 5 наиболее подходящих исполнителей задачи.");
                    sb.AppendLine("Учитывай навыки и меньшую загрузку (активных задач).");
                    sb.AppendLine("Ответ верни ТОЛЬКО списком тегов, например:");
                    sb.AppendLine("@ivan");
                    sb.AppendLine("@petr");
                    sb.AppendLine();
                    sb.AppendLine("Название: " + task.Title);
                    sb.AppendLine("Описание: " + task.Description);
                    sb.AppendLine();
                    sb.AppendLine("Сотрудники:");

                    foreach (var e in employees)
                    {
                        var tg = (e.TelegramTag ?? "").Trim().TrimStart('@').ToLower();
                        if (string.IsNullOrWhiteSpace(tg)) continue;

                        load.TryGetValue(tg, out var cnt);
                        skills.TryGetValue(tg, out var sk);

                        sb.Append($"@{tg} load={cnt} skills=");
                        if (sk != null && sk.Count > 0)
                            sb.Append(string.Join(", ", sk.Take(5).Select(x => $"{x.skill}({x.years}y)")));
                        else
                            sb.Append("none");
                        sb.AppendLine();
                    }

                    string answer;
                    try
                    {
                        answer = await _aiService.GenerateAsync(sb.ToString(), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        answer = "[AI ERROR] " + ex.Message;
                    }

                    var picked = new List<string>();
                    foreach (Match m in Regex.Matches(answer ?? "", @"@([a-zA-Z0-9_]+)"))
                    {
                        var tg = m.Groups[1].Value.Trim().ToLower();
                        if (!picked.Contains(tg)) picked.Add(tg);
                        if (picked.Count == 5) break;
                    }

                    if (picked.Count == 0)
                    {
                        picked = employees
                            .Select(e => (e.TelegramTag ?? "").Trim().TrimStart('@').ToLower())
                            .Where(tg => !string.IsNullOrWhiteSpace(tg))
                            .Select(tg => new { Tg = tg, Cnt = load.TryGetValue(tg, out var c) ? c : 0 })
                            .OrderBy(x => x.Cnt)
                            .Take(5)
                            .Select(x => x.Tg)
                            .ToList();
                    }

                    _lastAiSuggested[chatId] = picked;
                    listToShow = picked;
                }
            }

            var rows = new List<InlineKeyboardButton[]>();
            var header = useAi
                ? "✅ Выбери исполнителей (можно несколько). Рекомендации AI:\n"
                : "✅ Выбери исполнителей (можно несколько). Список всех сотрудников:\n";

            var text = header + "\n";

            foreach (var tg in listToShow)
            {
                var tag = "@" + tg;
                var emp = employees.FirstOrDefault(e => ((e.TelegramTag ?? "").Trim().TrimStart('@').ToLower() == tg));
                var name = emp?.FullName ?? tg;

                load.TryGetValue(tg, out var cnt);

                var selected = task.AssignedEmployeeTags != null && task.AssignedEmployeeTags.Contains(tag);
                var mark = selected ? "✅" : "➕";

                text += $"{mark} {name} ({tag}) — активных задач: {cnt}\n";

                rows.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData($"{mark} {name}", $"pick_toggle:{tg}")
                });
            }

            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("👤 Выбрать вручную", "pick_manual") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔄 Переподобрать", "pick_ai_refresh") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("➡️ Далее (дедлайн)", "pick_done") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") });

            await SendCleanAsync(chatId, text, cancellationToken, replyMarkup: new InlineKeyboardMarkup(rows));
        }

        // =========================
        // Deadline: calendar flow + fallback
        // =========================
        private async Task HandleAddTaskDeadline(long chatId, string deadlineText, CancellationToken cancellationToken)
        {
            if (!_tempTasks.TryGetValue(chatId, out var task))
            {
                await SendCleanAsync(chatId, "Ошибка: задача не найдена.", cancellationToken, replyMarkup: GetHomeOnlyButton());
                return;
            }

            if (DateTime.TryParseExact(deadlineText, "dd.MM.yyyy HH:mm",
                    CultureInfo.GetCultureInfo("ru-RU"),
                    DateTimeStyles.None, out DateTime deadline))
            {
                await FinalizeTaskWithDeadline(chatId, deadline, cancellationToken);
                return;
            }

            await SendCleanAsync(chatId,
                "Выбери дедлайн кнопками в календаре.\nЕсли хочешь ввести вручную — формат ДД.ММ.ГГГГ ЧЧ:ММ",
                cancellationToken,
                replyMarkup: GetHomeOnlyButton());
        }

        private async Task FinalizeTaskWithDeadline(long chatId, DateTime deadline, CancellationToken ct)
        {
            if (!_tempTasks.TryGetValue(chatId, out var task))
            {
                await SendCleanAsync(chatId, "Ошибка: задача не найдена.", ct, replyMarkup: GetHomeOnlyButton());
                return;
            }

            if (deadline < DateTime.Now)
            {
                await SendCleanAsync(chatId, "Дедлайн не может быть в прошлом. Выбери другую дату/время.", ct, replyMarkup: GetHomeOnlyButton());
                await ShowCalendar(chatId, DateTime.Now, ct);
                return;
            }

            task.Deadline = deadline;

            // ✅ Теперь AddTask пишет в Postgres
            _dataService.AddTask(task);

            _tempTasks.Remove(chatId);
            _deadlineDraft.Remove(chatId);
            _userStates[chatId] = ST_MAIN_MENU;

            var user = _dataService.Users.Values.FirstOrDefault(u => u.TelegramId == chatId);

            await SendCleanAsync(chatId, $"✅ Задача \"{task.Title}\" успешно добавлена!\n⏳ Дедлайн: {deadline:dd.MM.yyyy HH:mm}", ct, replyMarkup: GetHomeOnlyButton());
            await ShowMainMenu(chatId, "Главное меню:", user?.Role ?? Role.Employee, ct);
        }

        private async Task ShowCalendar(long chatId, DateTime month, CancellationToken ct)
        {
            var first = new DateTime(month.Year, month.Month, 1);
            var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);

            int firstDow = (int)first.DayOfWeek;
            if (firstDow == 0) firstDow = 7;

            var rows = new List<InlineKeyboardButton[]>();

            var prev = first.AddMonths(-1);
            var next = first.AddMonths(1);

            var monthTitle = first.ToString("MMMM yyyy", new CultureInfo("ru-RU"));

            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("⬅️", $"cal:{prev:yyyy-MM}"),
                InlineKeyboardButton.WithCallbackData(monthTitle, "noop"),
                InlineKeyboardButton.WithCallbackData("➡️", $"cal:{next:yyyy-MM}")
            });

            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("Пн","noop"),
                InlineKeyboardButton.WithCallbackData("Вт","noop"),
                InlineKeyboardButton.WithCallbackData("Ср","noop"),
                InlineKeyboardButton.WithCallbackData("Чт","noop"),
                InlineKeyboardButton.WithCallbackData("Пт","noop"),
                InlineKeyboardButton.WithCallbackData("Сб","noop"),
                InlineKeyboardButton.WithCallbackData("Вс","noop")
            });

            var week = new List<InlineKeyboardButton>();

            for (int i = 1; i < firstDow; i++)
                week.Add(InlineKeyboardButton.WithCallbackData(" ", "noop"));

            for (int day = 1; day <= daysInMonth; day++)
            {
                var d = new DateTime(month.Year, month.Month, day);
                var label = day.ToString();

                if (d.Date == DateTime.Today) label = "[" + label + "]";

                week.Add(InlineKeyboardButton.WithCallbackData(label, $"cald:{d:yyyy-MM-dd}"));

                if (week.Count == 7)
                {
                    rows.Add(week.ToArray());
                    week = new List<InlineKeyboardButton>();
                }
            }

            if (week.Count > 0)
            {
                while (week.Count < 7)
                    week.Add(InlineKeyboardButton.WithCallbackData(" ", "noop"));

                rows.Add(week.ToArray());
            }

            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") });

            await SendCleanAsync(chatId, "📅 Выбери дату дедлайна:", ct, replyMarkup: new InlineKeyboardMarkup(rows));
        }

        private async Task ShowTimePicker(long chatId, DateTime date, CancellationToken ct)
        {
            var rows = new List<InlineKeyboardButton[]>
            {
                new[] { InlineKeyboardButton.WithCallbackData("09:00", "calt:09:00"), InlineKeyboardButton.WithCallbackData("12:00", "calt:12:00") },
                new[] { InlineKeyboardButton.WithCallbackData("15:00", "calt:15:00"), InlineKeyboardButton.WithCallbackData("18:00", "calt:18:00") },
                new[] { InlineKeyboardButton.WithCallbackData("🕒 Ввести время вручную", "caltime_manual") },
                new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад к календарю", $"cal:{date:yyyy-MM}") },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") }
            };

            await SendCleanAsync(chatId, $"🕒 Выбери время дедлайна\nДата: {date:dd.MM.yyyy}", ct, replyMarkup: new InlineKeyboardMarkup(rows));
        }

        // =========================
        // Legacy add_task_employees
        // =========================
        private async Task HandleAddTaskEmployees(long chatId, string employeesText, CancellationToken cancellationToken)
        {
            var tags = employeesText.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.StartsWith("@"))
                .ToList();

            var invalid = new List<string>();
            foreach (var tag in tags)
                if (_dataService.GetUserByTag(tag) == null) invalid.Add(tag);

            if (invalid.Any())
            {
                await SendCleanAsync(
                    chatId,
                    $"Следующие пользователи не найдены: {string.Join(", ", invalid)}\nПожалуйста, введите теги снова:",
                    cancellationToken,
                    replyMarkup: GetHomeOnlyButton()
                );
                return;
            }

            _tempTasks[chatId].AssignedEmployeeTags = tags;
            _userStates[chatId] = ST_ADD_DEADLINE;

            await SendCleanAsync(
                chatId,
                "Введите дедлайн задачи в формате ДД.ММ.ГГГГ ЧЧ:ММ (например, 31.12.2025 18:00):",
                cancellationToken,
                replyMarkup: GetHomeOnlyButton()
            );
        }

        // =========================
        // AI подбор исполнителя (standalone)
        // =========================
        private async Task HandleAiPick(long chatId, string taskDescription, CancellationToken ct)
        {
            var employees = _dataService.GetAllEmployees();
            var load = _dataService.GetEmployeesLoadByTag();
            var skills = _dataService.GetEmployeesSkills();

            if (employees.Count == 0)
            {
                await SendCleanAsync(chatId, "Сотрудников нет в БД.", ct, replyMarkup: GetHomeOnlyButton());
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Ты — ассистент руководителя. Подбери исполнителей задачи.");
            sb.AppendLine("Критерии: 1) навыки/опыт по задаче, 2) меньшая текущая загрузка лучше.");
            sb.AppendLine("Верни ТОЛЬКО топ-3 кандидата строго в формате:");
            sb.AppendLine("1) @tag — причина");
            sb.AppendLine("2) @tag — причина");
            sb.AppendLine("3) @tag — причина");
            sb.AppendLine();
            sb.AppendLine("Задача:");
            sb.AppendLine(taskDescription);
            sb.AppendLine();
            sb.AppendLine("Сотрудники:");

            foreach (var e in employees)
            {
                var tgNoAt = (e.TelegramTag ?? "").Trim().TrimStart('@').ToLower();
                load.TryGetValue(tgNoAt, out var cnt);
                skills.TryGetValue(tgNoAt, out var sk);

                sb.AppendLine($"- {e.FullName} (@{tgNoAt})");
                sb.AppendLine($"  Активных задач: {cnt}");

                if (sk != null && sk.Count > 0)
                {
                    sb.AppendLine("  Навыки:");
                    foreach (var s in sk.Take(10))
                        sb.AppendLine($"   • {s.skill} ({s.years} лет)");
                }
                else
                {
                    sb.AppendLine("  Навыки: нет данных");
                }

                sb.AppendLine();
            }

            await SendCleanAsync(chatId, "🤖 Подбираю исполнителей…", ct, replyMarkup: GetHomeOnlyButton());

            string answer;
            try
            {
                answer = await _aiService.GenerateAsync(sb.ToString(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                answer = "[AI ERROR] " + ex.Message;
            }

            if (string.IsNullOrWhiteSpace(answer))
                answer = "Пустой ответ от модели. Проверь, что Ollama запущена и модель phi3:mini работает.";

            if (answer.Length > 3500) answer = answer.Substring(0, 3500);

            await SendCleanAsync(chatId, $"✅ Рекомендации:\n\n{answer}", ct, replyMarkup: GetHomeBackButtons());
        }
    }
}
