using System; 
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManagerBot
{
    public class AiService
    {
        private readonly HttpClient _http;

        public AiService()
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri("http://localhost:11434"),
                Timeout = TimeSpan.FromSeconds(120)
            };
        }

        public async Task<string> GenerateAsync(string prompt, CancellationToken ct)
        {
            var payload = new
            {
                model = "phi3:mini",
                prompt = prompt,
                stream = false
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await _http.PostAsync("/api/generate", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return $"[AI ERROR] {resp.StatusCode}: {body}";

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("response", out var r))
                return r.GetString() ?? "";

            return body;
        }
    }
}
