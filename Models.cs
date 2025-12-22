using System;
using System.Collections.Generic;

namespace TaskManagerBot
{
    // Роли пользователей
    public enum Role
    {
        Manager,
        Employee
    }

    // Статус задачи
    public enum TaskStatus
    {
        InProgress,
        Completed,
        Waiting,
        Cancelled
    }

    // Модель пользователя
    public class User
    {
        public long TelegramId { get; set; }
        public string TelegramTag { get; set; } = "";
        public string FullName { get; set; } = "";
        public Role Role { get; set; } = Role.Employee;
        public int Age { get; set; }
        public string Bio { get; set; } = "";
        public List<string> AssignedTasks { get; set; } = new List<string>();
    }

    // Модель задачи (именуем TaskModel чтобы не конфликтовать с System.Threading.Tasks.Task)
    public class TaskModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime Deadline { get; set; } = DateTime.Now;
        public TaskStatus Status { get; set; } = TaskStatus.Waiting;
        public List<string> AssignedEmployeeTags { get; set; } = new List<string>();
    }
}
