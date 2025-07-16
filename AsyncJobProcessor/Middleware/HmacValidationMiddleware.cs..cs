using System.Security.Cryptography;
using System.Text;

namespace AsyncJobProcessor.Middleware
{
    public class HmacValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<HmacValidationMiddleware> _logger;
        private readonly byte[] _callbackSecretKeyBytes;

        public HmacValidationMiddleware(RequestDelegate next, ILogger<HmacValidationMiddleware> logger, IConfiguration configuration)
        {
            _next = next;
            _logger = logger;

            var secretKey = configuration["Security:CallbackSecretKey"] 
                            ?? throw new InvalidOperationException("CallbackSecretKey is not configured.");
            _callbackSecretKeyBytes = Encoding.UTF8.GetBytes(secretKey);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // [1] Перевіряємо, чи поточний запит призначений для нашого ендпоінту колбеку
            // Це важливо, щоб middleware не спрацьовував для всіх запитів (наприклад, Swagger UI).
            // Припускаємо, що всі callback-запити починаються з "/api/callbacks/jobs"
            if (!context.Request.Path.StartsWithSegments("/api/callbacks/jobs", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context); // Передаємо запит далі по конвеєру, якщо це не колбек
                return;
            }

            _logger.LogInformation("Attempting HMAC validation for callback request: {Path}", context.Request.Path);

            // [2] Дозволяємо перечитування тіла запиту
            // Це потрібно, щоб ми могли зчитати сирі байти для HMAC,
            // а потім контролер міг знову прочитати їх для десеріалізації.
            context.Request.EnableBuffering();
            
            // [3] Читаємо сирі байти тіла запиту
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, true, 1024, true);
            var rawBody = await reader.ReadToEndAsync();
            
            // [4] Скидаємо позицію потоку на початок, щоб контролер міг десеріалізувати тіло
            context.Request.Body.Position = 0;

            // [5] Очікуваний HMAC-підпис з HTTP-заголовка
            var expectedHmacHeader = context.Request.Headers["X-Simulated-Hmac-Signature"].FirstOrDefault();

            if (string.IsNullOrEmpty(expectedHmacHeader))
            {
                _logger.LogWarning("Callback for {Path} received without X-Simulated-Hmac-Signature header.", context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Missing HMAC signature.");
                return;
            }

            // [6] Обчислюємо власний HMAC на основі сирого тіла запиту
            var calculatedHmac = GenerateHmacSha256(rawBody, _callbackSecretKeyBytes);

            // [7] Порівнюємо отриманий HMAC з нашим обчисленим
            if (!calculatedHmac.Equals(expectedHmacHeader, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Invalid HMAC signature for {Path}. Expected: {Expected}, Calculated: {Calculated}", 
                                   context.Request.Path, expectedHmacHeader, calculatedHmac);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid HMAC signature.");
                return;
            }

            _logger.LogInformation("HMAC signature successfully validated for {Path}.", context.Request.Path);

            // [8] Якщо все гаразд, передаємо запит наступному Middleware або до контролера
            await _next(context);
        }

        // Допоміжний метод для обчислення HMAC (такий самий, як у контролері раніше)
        private string GenerateHmacSha256(string data, byte[] key)
        {
            using (var hmac = new HMACSHA256(key))
            {
                var dataBytes = Encoding.UTF8.GetBytes(data);
                var hashBytes = hmac.ComputeHash(dataBytes);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}