using System;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManagerBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Запуск Task Manager Bot...");

            // Рекомендуется хранить токен в переменной окружения TELEGRAM_BOT_TOKEN
            // В локальной разработке можно заменить "PUT_YOUR_TOKEN_HERE" на реальный токен,
            // но не коммить его в репозиторий.
            string botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "8092871629:AAGCBsXfIGC89fM5FsVvh_FocmPbRmOFRvI";

            if (botToken == "    ")
            {
                Console.WriteLine("Пожалуйста, установите ваш токен бота.");
                Console.WriteLine("1) Поместите токен в переменную окружения TELEGRAM_BOT_TOKEN");
                Console.WriteLine("или");
                Console.WriteLine("2) Временно замените значение botToken в Program.cs на токен от @BotFather (не рекомендуется для репозитория).");
                return;
            }

            var botService = new BotService(botToken);

            using var cts = new CancellationTokenSource();

            // Запуск бота
            await botService.StartAsync(cts.Token);

            Console.WriteLine("Бот запущен. Нажмите любую клавишу для остановки...");
            Console.ReadKey();

            cts.Cancel();
            Console.WriteLine("Бот остановлен.");
        }
    }
}
