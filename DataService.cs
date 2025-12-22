using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Npgsql;

namespace TaskManagerBot
{
    public class DataService
    {
        private static DataService _instance;
        public static DataService Instance => _instance ??= new DataService();

        // TODO: лучше вынести в appsettings.json/ENV
        private readonly string _cs =
            "Host=localhost;Port=5432;Database=taskmanagerbot;Username=postgres;Password=12345;";

        // Кэш пользователей нужен BotService: он ищет текущего пользователя по TelegramId (chatId)
        public Dictionary<long, User> Users { get; private set; } = new Dictionary<long, User>();

        // ⚠️ Раньше Tasks жили в памяти — теперь задачи в Postgres, этот кэш больше не используем.
        // Оставляем поле, чтобы не ломать проект, но бот его больше не читает.
        public Dictionary<string, TaskModel> Tasks { get; private set; } = new Dictionary<string, TaskModel>();

        private DataService()
        {
            DebugDb();
        }

        // ======== DB rows ========
        private sealed class DbUserRow
        {
            public int Peple_Id { get; set; }
            public string FullName { get; set; } = "";
            public string TelegramTag { get; set; } = "";
            public string Bio { get; set; } = "";
            public string RoleName { get; set; } = "";
            public DateTime BirthDate { get; set; }
        }

        private sealed class DbEmployeeRow
        {
            public int Peple_Id { get; set; }
            public string FullName { get; set; } = "";
            public string TelegramTag { get; set; } = "";
            public string Bio { get; set; } = "";
            public string RoleName { get; set; } = "";
            public DateTime BirthDate { get; set; }
        }

        private sealed class DbTaskRow
        {
            public int St_Ip { get; set; }
            public string Desc { get; set; } = "";
        }

        // ======== helpers ========
        private static string NormalizeTagNoAt(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            var s = tag.Trim();
            if (s.StartsWith("@")) s = s.Substring(1);
            return s.ToLowerInvariant();
        }

        private int? GetPeopleIdByTag(string telegramTag, NpgsqlConnection conn)
        {
            var tg = NormalizeTagNoAt(telegramTag);
            if (string.IsNullOrWhiteSpace(tg)) return null;

            return conn.ExecuteScalar<int?>(@"
                SELECT peple_id
                FROM virt_assist.people
                WHERE lower(regexp_replace(tg, '^@', '')) = @tg
                LIMIT 1;", new { tg });
        }

        private TaskStatus MapStatus(string? s)
        {
            var x = (s ?? "").Trim().ToLowerInvariant();

            // поддержка как англ, так и рус/смешанных вариантов
            if (x.Contains("inprogress") || x.Contains("in progress") || x.Contains("процесс") || x.Contains("work"))
                return TaskStatus.InProgress;

            if (x.Contains("complete") || x.Contains("done") || x.Contains("заверш"))
                return TaskStatus.Completed;

            if (x.Contains("cancel") || x.Contains("отмен"))
                return TaskStatus.Cancelled;

            return TaskStatus.Waiting;
        }

        private void DebugDb()
        {
            try
            {
                using var conn = new NpgsqlConnection(_cs);
                conn.Open();

                var info = conn.QuerySingle<(string db, string usr)>(
                    "SELECT current_database() AS db, current_user AS usr;"
                );
                Console.WriteLine($"[DB] Connected. database={info.db}, user={info.usr}");

                var schemaExists = conn.ExecuteScalar<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname='virt_assist');"
                );
                Console.WriteLine($"[DB] Schema virt_assist exists: {schemaExists}");

                var peopleExists = conn.ExecuteScalar<bool>(
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='virt_assist' AND table_name='people');"
                );
                Console.WriteLine($"[DB] Table virt_assist.people exists: {peopleExists}");

                var tasksExists = conn.ExecuteScalar<bool>(
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='virt_assist' AND table_name='sub_tasks');"
                );
                Console.WriteLine($"[DB] Table virt_assist.sub_tasks exists: {tasksExists}");

                var inProgExists = conn.ExecuteScalar<bool>(
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='virt_assist' AND table_name='in_progress');"
                );
                Console.WriteLine($"[DB] Table virt_assist.in_progress exists: {inProgExists}");

                if (peopleExists)
                {
                    var sample = conn.Query<string>(
                        "SELECT tg FROM virt_assist.people ORDER BY peple_id LIMIT 10;"
                    ).ToList();

                    Console.WriteLine("[DB] Sample tg (first 10): " + string.Join(", ", sample));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DB] DebugDb error: " + ex);
            }
        }

        // ======== users ========
        public void AddUser(User user)
        {
            Users[user.TelegramId] = user;
        }

        public User GetUserByTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            tag = tag.Trim();
            var tagNoAt = tag.StartsWith("@") ? tag.Substring(1) : tag;

            // варианты поиска (и с @ и без @)
            var variants = new[] { tagNoAt, tag }.Distinct().ToArray();

            Console.WriteLine($"[DB] GetUserByTag input='{tag}', normalized='{tagNoAt}'");

            const string sql = @"
            SELECT 
              p.peple_id                AS ""Peple_Id"",
              p.fio                     AS ""FullName"",
              p.tg                      AS ""TelegramTag"",
              COALESCE(p.""desc"", '')  AS ""Bio"",
              r.name                    AS ""RoleName"",
              (p.date_b::timestamp)     AS ""BirthDate""
            FROM virt_assist.people p
            JOIN virt_assist.roles r ON r.r_id = p.r_id
            WHERE lower(regexp_replace(p.tg, '^@', '')) = ANY(@variants)
               OR lower(p.tg) = ANY(@variants)
            LIMIT 1;";

            using var conn = new NpgsqlConnection(_cs);

            DbUserRow row;
            try
            {
                row = conn.QueryFirstOrDefault<DbUserRow>(sql, new
                {
                    variants = variants.Select(v => v.ToLower()).ToArray()
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] GetUserByTag error: {ex}");
                return null;
            }

            if (row == null)
            {
                Console.WriteLine("[DB] Not found in virt_assist.people");
                return null;
            }

            var roleLower = (row.RoleName ?? "").Trim().ToLower();
            var role = roleLower.Contains("руковод") ? Role.Manager : Role.Employee;

            var today = DateTime.Today;
            var age = today.Year - row.BirthDate.Year;
            if (row.BirthDate.Date > today.AddYears(-age)) age--;

            return new User
            {
                FullName = row.FullName,
                TelegramTag = row.TelegramTag.StartsWith("@") ? row.TelegramTag : ("@" + row.TelegramTag),
                Bio = row.Bio,
                Role = role,
                Age = age
            };
        }

        // ======== employees ========
        public List<User> GetAllEmployees()
        {
            const string sql = @"
            SELECT
              p.peple_id               AS ""Peple_Id"",
              p.fio                    AS ""FullName"",
              p.tg                     AS ""TelegramTag"",
              COALESCE(p.""desc"", '') AS ""Bio"",
              r.name                   AS ""RoleName"",
              (p.date_b::timestamp)    AS ""BirthDate""
            FROM virt_assist.people p
            JOIN virt_assist.roles r ON r.r_id = p.r_id
            WHERE lower(r.name) LIKE '%сотруд%'
            ORDER BY p.fio;";

            using var conn = new NpgsqlConnection(_cs);

            List<DbEmployeeRow> rows;
            try
            {
                rows = conn.Query<DbEmployeeRow>(sql).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] GetAllEmployees error: {ex}");
                return new List<User>();
            }

            var today = DateTime.Today;

            return rows.Select(r =>
            {
                var age = today.Year - r.BirthDate.Year;
                if (r.BirthDate.Date > today.AddYears(-age)) age--;

                return new User
                {
                    FullName = r.FullName,
                    TelegramTag = r.TelegramTag.StartsWith("@") ? r.TelegramTag : ("@" + r.TelegramTag),
                    Bio = r.Bio,
                    Role = Role.Employee,
                    Age = age
                };
            }).ToList();
        }

        // ✅ загрузка активных задач по tg (ключ: tg без '@' lower-case)
        public Dictionary<string, int> GetEmployeesLoadByTag()
        {
            const string sql = @"
            SELECT
              lower(regexp_replace(p.tg, '^@', '')) AS tg_no_at,
              COUNT(ip.inp_id) AS active_count
            FROM virt_assist.people p
            JOIN virt_assist.roles r ON r.r_id = p.r_id
            LEFT JOIN virt_assist.in_progress ip
              ON ip.peple_id = p.peple_id
             AND (
                  lower(coalesce(ip.status,'')) LIKE '%процесс%'
               OR lower(coalesce(ip.status,'')) LIKE '%in progress%'
               OR lower(coalesce(ip.status,'')) LIKE '%inprogress%'
               OR lower(coalesce(ip.status,'')) LIKE '%todo%'
               OR lower(coalesce(ip.status,'')) LIKE '%waiting%'
             )
            WHERE lower(r.name) LIKE '%сотруд%'
            GROUP BY lower(regexp_replace(p.tg, '^@', ''))
            ORDER BY tg_no_at;";

            using var conn = new NpgsqlConnection(_cs);

            try
            {
                return conn.Query<(string tg_no_at, int active_count)>(sql)
                    .ToDictionary(x => x.tg_no_at, x => x.active_count);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] GetEmployeesLoadByTag error: {ex}");
                return new Dictionary<string, int>();
            }
        }

        // ✅ навыки (workers_exp + proffesions) + стаж (годы)
        public Dictionary<string, List<(string skill, int years)>> GetEmployeesSkills()
        {
            const string sql = @"
            SELECT
              lower(regexp_replace(p.tg, '^@', '')) AS tg_no_at,
              pr.name AS skill,
              EXTRACT(YEAR FROM AGE(
                COALESCE(we.date_end, CURRENT_DATE),
                we.date_start
              ))::int AS years
            FROM virt_assist.workers_exp we
            JOIN virt_assist.people p ON p.peple_id = we.peple_id
            JOIN virt_assist.proffesions pr ON pr.prof_id = we.prof_id
            ORDER BY tg_no_at, years DESC;";

            using var conn = new NpgsqlConnection(_cs);

            try
            {
                var rows = conn.Query<(string tg_no_at, string skill, int years)>(sql);

                return rows
                    .GroupBy(r => r.tg_no_at)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(x => (x.skill, x.years)).ToList()
                    );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] GetEmployeesSkills error: {ex}");
                return new Dictionary<string, List<(string skill, int years)>>();
            }
        }

        // ======== tasks (SQL) ========

        // Возвращает теги назначенных сотрудников для задачи
        private List<string> GetAssignedTagsForTask(int stIp, NpgsqlConnection conn)
        {
            var sql = @"
                SELECT '@' || lower(regexp_replace(p.tg, '^@', '')) AS tag
                FROM virt_assist.in_progress ip
                JOIN virt_assist.people p ON p.peple_id = ip.peple_id
                WHERE ip.st_ip = @st
                ORDER BY p.peple_id;";

            return conn.Query<string>(sql, new { st = stIp }).ToList();
        }

        // Агрегирует статус по всем исполнителям (для менеджера)
        private TaskStatus GetAggregatedStatusForTask(int stIp, NpgsqlConnection conn)
        {
            var statuses = conn.Query<string>(@"
                SELECT COALESCE(status, 'Waiting') AS status
                FROM virt_assist.in_progress
                WHERE st_ip = @st;",
                new { st = stIp })
                .Select(x => x ?? "Waiting")
                .ToList();

            if (statuses.Count == 0) return TaskStatus.Waiting;

            // если кто-то "в работе" — задача в работе
            if (statuses.Any(s => MapStatus(s) == TaskStatus.InProgress)) return TaskStatus.InProgress;

            // если все завершены — завершена
            if (statuses.All(s => MapStatus(s) == TaskStatus.Completed)) return TaskStatus.Completed;

            // если кто-то отменил — считаем отменённой (можно переиграть логику)
            if (statuses.Any(s => MapStatus(s) == TaskStatus.Cancelled)) return TaskStatus.Cancelled;

            return TaskStatus.Waiting;
        }

        // ✅ Менеджер: все задачи из sub_tasks
        public List<TaskModel> GetAllTasks()
        {
            try
            {
                using var conn = new NpgsqlConnection(_cs);
                conn.Open();

                var rows = conn.Query<DbTaskRow>(@"
                    SELECT st_ip AS ""St_Ip"", COALESCE(""desc"", '') AS ""Desc""
                    FROM virt_assist.sub_tasks
                    ORDER BY st_ip;").ToList();

                var result = new List<TaskModel>();

                foreach (var r in rows)
                {
                    var title = string.IsNullOrWhiteSpace(r.Desc)
                        ? $"Задача #{r.St_Ip}"
                        : (r.Desc.Length > 40 ? r.Desc.Substring(0, 40) + "…" : r.Desc);

                    var t = new TaskModel
                    {
                        Id = r.St_Ip.ToString(),
                        Title = title,
                        Description = r.Desc,
                        Deadline = DateTime.Now.AddDays(7), // если нет поля deadline в БД — ставим дефолт
                        AssignedEmployeeTags = GetAssignedTagsForTask(r.St_Ip, conn),
                        Status = GetAggregatedStatusForTask(r.St_Ip, conn)
                    };

                    result.Add(t);
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] GetAllTasks error: {ex}");
                return new List<TaskModel>();
            }
        }

        // ✅ Сотрудник: только его задачи (через in_progress)
        public List<TaskModel> GetUserTasks(string telegramTag)
        {
            try
            {
                using var conn = new NpgsqlConnection(_cs);
                conn.Open();

                var pid = GetPeopleIdByTag(telegramTag, conn);
                if (pid == null) return new List<TaskModel>();

                var sql = @"
                    SELECT
                      st.st_ip AS st_ip,
                      COALESCE(st.""desc"", '') AS desc,
                      COALESCE(ip.status, 'Waiting') AS status
                    FROM virt_assist.in_progress ip
                    JOIN virt_assist.sub_tasks st ON st.st_ip = ip.st_ip
                    WHERE ip.peple_id = @pid
                    ORDER BY st.st_ip;";

                var rows = conn.Query<(int st_ip, string desc, string status)>(sql, new { pid }).ToList();

                var result = new List<TaskModel>();
                foreach (var r in rows)
                {
                    var title = string.IsNullOrWhiteSpace(r.desc)
                        ? $"Задача #{r.st_ip}"
                        : (r.desc.Length > 40 ? r.desc.Substring(0, 40) + "…" : r.desc);

                    result.Add(new TaskModel
                    {
                        Id = r.st_ip.ToString(),
                        Title = title,
                        Description = r.desc,
                        Deadline = DateTime.Now.AddDays(7),
                        AssignedEmployeeTags = GetAssignedTagsForTask(r.st_ip, conn),
                        Status = MapStatus(r.status)
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] GetUserTasks error: {ex}");
                return new List<TaskModel>();
            }
        }

        // ✅ Возвращает пользователей по тегам (как раньше)
        public List<User> GetUsersByTags(List<string> tags)
        {
            return (tags ?? new List<string>())
                .Select(t => GetUserByTag(t))
                .Where(u => u != null)
                .ToList();
        }

        // ✅ Создание задачи: пишем в sub_tasks и создаём назначения в in_progress
        public void AddTask(TaskModel task)
        {
            if (task == null) return;

            try
            {
                using var conn = new NpgsqlConnection(_cs);
                conn.Open();

                using var tx = conn.BeginTransaction();

                // 1) вставка в sub_tasks (минимально: desc)
                // st_ip — предположим, SERIAL/IDENTITY
                var stIp = conn.ExecuteScalar<int>(@"
                    INSERT INTO virt_assist.sub_tasks(""desc"")
                    VALUES (@d)
                    RETURNING st_ip;",
                    new { d = task.Description ?? "" }, tx);

                task.Id = stIp.ToString();

                // 2) назначения в in_progress
                var tags = task.AssignedEmployeeTags ?? new List<string>();
                foreach (var tg in tags)
                {
                    var pid = GetPeopleIdByTag(tg, conn);
                    if (pid == null) continue;

                    conn.Execute(@"
                        INSERT INTO virt_assist.in_progress(peple_id, st_ip, status)
                        VALUES (@pid, @st, @status);",
                        new
                        {
                            pid,
                            st = stIp,
                            status = task.Status == TaskStatus.InProgress ? "InProgress" :
                                     task.Status == TaskStatus.Completed ? "Completed" :
                                     task.Status == TaskStatus.Cancelled ? "Cancelled" :
                                     "Waiting"
                        }, tx);
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] AddTask error: {ex}");
            }
        }

        // ✅ Обновление статуса задачи для конкретного сотрудника (кнопки "в работу" / "завершить")
        public bool UpdateTaskStatusForUser(string taskId, string telegramTag, string newStatus)
        {
            try
            {
                if (!int.TryParse(taskId, out var stIp)) return false;

                using var conn = new NpgsqlConnection(_cs);
                conn.Open();

                var pid = GetPeopleIdByTag(telegramTag, conn);
                if (pid == null) return false;

                var updated = conn.Execute(@"
                    UPDATE virt_assist.in_progress
                    SET status = @s
                    WHERE st_ip = @st AND peple_id = @pid;",
                    new { s = newStatus, st = stIp, pid });

                return updated > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] UpdateTaskStatusForUser error: {ex}");
                return false;
            }
        }
    }
}
