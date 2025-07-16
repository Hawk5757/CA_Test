using System.Text;
using System.Text.Json;
using AsyncJobProcessor.Models;

namespace AsyncJobProcessor.Services
{
    public class TestClientService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<TestClientService> _logger;
        private readonly string _mockServiceBaseUrl;

        public TestClientService(HttpClient httpClient, ILogger<TestClientService> logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _mockServiceBaseUrl = configuration["ThirdPartyService:BaseUrl"] ?? "http://localhost:5000";
            _httpClient.BaseAddress = new Uri(_mockServiceBaseUrl); 
            _logger.LogInformation("TestClientService initialized. Mock Service Base URL: {MockServiceBaseUrl}", _mockServiceBaseUrl);
        }

        /// <summary>
        /// Ініціює тестове завдання, надсилаючи запит до MockThirdPartyService.
        /// </summary>
        /// <param name="data">Дані для тестового завдання.</param>
        /// <param name="callbackUrl">URL, куди MockThirdPartyService повинен відправити колбек (це URL вашого AsyncJobProcessor).</param>
        public async Task RunTestJobAsync(string data, string callbackUrl)
        {
            _logger.LogInformation("TestClientService: Simulating request to Mock Third-Party Service with data: '{Data}'", data);

            // Генеруємо новий JobId для цього тестового запиту
            // Цей JobId буде використовуватися MockThirdPartyService і повернений у колбеку
            var jobIdFromCallbackUrl = callbackUrl.Split('/').Last();

            // Створюємо об'єкт запиту, який MockThirdPartyService очікує.
            // JobId, CallbackUrl та Data - це основні поля, які використовує мок.
            // SimulatedCallbackPayload використовується моком для симуляції відповіді.
            var externalServiceRequest = new ExternalServiceRequest
            {
                JobId = jobIdFromCallbackUrl,
                CallbackUrl = callbackUrl, // CallbackUrl - це ендпоінт AsyncJobProcessor, куди MockService надішле результат.
                Data = new { originalData = data, timestamp = DateTimeOffset.UtcNow },
                // Ці поля використовуються MockThirdPartyService для симуляції конкретного результату
                SimulatedCallbackPayload = new JobResult
                {
                    JobId = jobIdFromCallbackUrl, // JobId в payload має відповідати оригінальному JobId
                    Status = JobStatus.Completed, // Вказуємо, що симулюємо успішне завершення
                    Message = $"Simulated completion for job {jobIdFromCallbackUrl} with data '{data}'",
                    Payload = new { processedValue = Guid.NewGuid().ToString(), processedAt = DateTimeOffset.UtcNow },
                    ErrorMessage = null
                },
                // SimulatedHmacSignature - це просто заглушка для мока, він ігнорує її
                SimulatedHmacSignature = "dummy_signature_from_client"
            };

            // Серіалізуємо об'єкт запиту в JSON
            var jsonRequest = JsonSerializer.Serialize(externalServiceRequest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            try
            {
                _logger.LogInformation("TestClientService: Sending POST request to {MockServiceBaseUrl}/startjob for Job ID: {JobId}", _mockServiceBaseUrl, jobIdFromCallbackUrl);
                _logger.LogDebug("TestClientService: Request Payload: {JsonRequest}", jsonRequest);

                var response = await _httpClient.PostAsync("/startjob", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("TestClientService: Request successfully sent to Mock Third-Party Service for Job ID: {JobId}. Status Code: {StatusCode}", jobIdFromCallbackUrl, response.StatusCode);
                    _logger.LogDebug("TestClientService: Mock Service Response: {ResponseContent}", responseContent);
                    _logger.LogInformation("TestClientService: Awaiting callback to AsyncJobProcessor for Job ID: {JobId} from Mock Third-Party Service...", jobIdFromCallbackUrl);
                }
                else
                {
                    _logger.LogError("TestClientService: Failed to send request to Mock Third-Party Service for Job ID: {JobId}. Status Code: {StatusCode}", jobIdFromCallbackUrl, response.StatusCode);
                    _logger.LogError("TestClientService: Details: {ResponseContent}", responseContent);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "TestClientService: HTTP request error when trying to call Mock Third-Party Service for Job ID: {JobId}. Is MockThirdPartyService running on {MockBaseUrl} and accessible?", jobIdFromCallbackUrl, _mockServiceBaseUrl);
                _logger.LogError("TestClientService: Error Message: {ErrorMessage}", ex.Message);
                if (ex.InnerException != null)
                {
                    _logger.LogError("TestClientService: Inner Exception: {InnerExceptionMessage}", ex.InnerException.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TestClientService: An unexpected error occurred while running test job for Job ID: {JobId}: {ErrorMessage}", jobIdFromCallbackUrl, ex.Message);
            }
        }
    }
    
    public class ExternalServiceRequest
    {
        public string JobId { get; set; }
        public string CallbackUrl { get; set; }
        public object Data { get; set; }
        public JobResult? SimulatedCallbackPayload { get; set; }
        public string? SimulatedHmacSignature { get; set; }
    }
}