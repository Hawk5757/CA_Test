using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;


public enum JobStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
    Timeout
}

public class JobResult
{
    public string JobId { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobStatus Status { get; set; }
    public string Message { get; set; }
    public object Payload { get; set; }
    public string ErrorMessage { get; set; } 
}

public class ExternalServiceRequest
{
    public string JobId { get; set; }
    public string CallbackUrl { get; set; }
    public object Data { get; set; }
    public JobResult SimulatedCallbackPayload { get; set; }
    public string SimulatedHmacSignature { get; set; }
}

public class Program
{
    private static readonly HttpClient _callbackHttpClient = new HttpClient();
    
    private const string SharedSecretKey = "YourSuperSecretKeyForCallbacks1234567890ABCDEF";
    private static readonly byte[] _callbackSecretKeyBytes = Encoding.UTF8.GetBytes(SharedSecretKey);

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        
        builder.WebHost.UseUrls("http://localhost:5000"); // Можна змінити, якщо 5000 зайнятий

        var app = builder.Build();

        app.UseRouting();

        // --- Ендпоінт для отримання запитів на старт завдання ---
        // Це ендпоінт, на який AsyncJobProcessor буде відправляти запити
        app.MapPost("/startjob", async context =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Received /startjob request.");
            try
            {
                // Зчитування тіла запиту
                var requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
                var externalServiceRequest = JsonSerializer.Deserialize<ExternalServiceRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (externalServiceRequest == null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Invalid request body.");
                    return;
                }

                Console.WriteLine($"  Job ID: {externalServiceRequest.JobId}");
                Console.WriteLine($"  Callback URL: {externalServiceRequest.CallbackUrl}");
                Console.WriteLine($"  Data: {JsonSerializer.Serialize(externalServiceRequest.Data)}");

                // Імітація довгої обробки завдання
                int processingTimeSeconds = new Random().Next(2, 8); // Випадковий час від 2 до 8 секунд
                Console.WriteLine($"  Simulating job processing for {processingTimeSeconds} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(processingTimeSeconds));

                // --- Підготовка результату для колбеку ---
                JobResult jobResultToCallback;
                // Використовуємо симульований Payload, якщо він був надісланий (для внутрішніх тестів AsyncJobProcessor)
                if (externalServiceRequest.SimulatedCallbackPayload != null)
                {
                    jobResultToCallback = externalServiceRequest.SimulatedCallbackPayload;
                    // Переконаємося, що JobId також відповідає
                    jobResultToCallback.JobId = externalServiceRequest.JobId;
                }
                else
                {
                    // Або генеруємо звичайний успішний результат
                    jobResultToCallback = new JobResult
                    {
                        JobId = externalServiceRequest.JobId,
                        Status = JobStatus.Completed,
                        Message = $"Mock job {externalServiceRequest.JobId} processed successfully by mock service!",
                        Payload = new { processedData = $"MockProcessedData_{Guid.NewGuid()}" }
                    };
                }

                // Серіалізуємо об'єкт результату в JSON для відправки
                var jsonResult = JsonSerializer.Serialize(jobResultToCallback, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                // Генеруємо HMAC-підпис для безпеки колбеку
                var hmacSignature = GenerateHmacSha256(jsonResult, _callbackSecretKeyBytes);

                Console.WriteLine($"  Sending callback to: {externalServiceRequest.CallbackUrl}");
                Console.WriteLine($"  Callback Payload: {jsonResult}");
                Console.WriteLine($"  HMAC Signature: {hmacSignature}");

                // --- Відправка колбеку ---
                var callbackContent = new StringContent(jsonResult, Encoding.UTF8, "application/json");
                callbackContent.Headers.Add("X-HMAC-SHA256", hmacSignature); // Додаємо HMAC-заголовок

                var callbackResponse = await _callbackHttpClient.PostAsync(externalServiceRequest.CallbackUrl, callbackContent);

                Console.WriteLine($"  Callback response status: {callbackResponse.StatusCode}");
                if (!callbackResponse.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  Callback failed: {await callbackResponse.Content.ReadAsStringAsync()}");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  Callback sent successfully for Job ID: {externalServiceRequest.JobId}");
                    Console.ResetColor();
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync($"Job {externalServiceRequest.JobId} received and callback initiated by mock service.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error processing /startjob request in MockThirdPartyService: {ex.Message}");
                Console.ResetColor();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync($"Error: {ex.Message}");
            }
        });

        app.Run(); // Запуск веб-сервера
    }

    // --- Допоміжний метод для генерації HMAC-SHA256 ---
    private static string GenerateHmacSha256(string data, byte[] key)
    {
        using (var hmac = new HMACSHA256(key))
        {
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var hashBytes = hmac.ComputeHash(dataBytes);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}