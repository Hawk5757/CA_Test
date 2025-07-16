// AsyncJobProcessor/Services/TestClientService.cs
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration; // Додаємо для доступу до конфігурації
using AsyncJobProcessor.Models; // Щоб використовувати JobRequest та JobResult

namespace AsyncJobProcessor.Services
{
    public class TestClientService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<TestClientService> _logger;
        private readonly string _baseUrl;

        public TestClientService(HttpClient httpClient, ILogger<TestClientService> logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            // BaseUrl тепер беремо з конфігурації, вказуючи на наш же сервіс
            _baseUrl = configuration["CallbackHost"] ?? "https://localhost:7213"; 
            _httpClient.BaseAddress = new Uri(_baseUrl);
        }

        public async Task RunTestJobAsync(string data)
        {
            _logger.LogInformation("TestClientService: Running test job with data: '{Data}'", data);

            var request = new JobRequest { Data = data };
            var jsonRequest = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync("/api/jobs", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var jobResult = JsonSerializer.Deserialize<JobResult>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    _logger.LogInformation("TestClientService: Job registered successfully!");
                    _logger.LogInformation("TestClientService:   Job ID: {JobId}", jobResult?.JobId);
                    _logger.LogInformation("TestClientService:   Status: {Status}", jobResult?.Status);
                    _logger.LogInformation("TestClientService:   Message: {Message}", jobResult?.Message);
                }
                else
                {
                    _logger.LogError("TestClientService: Failed to register job. Status Code: {StatusCode}", response.StatusCode);
                    _logger.LogError("TestClientService:   Details: {ResponseContent}", responseContent);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "TestClientService: HTTP request error when trying to register job: {ErrorMessage}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TestClientService: An unexpected error occurred while running test job: {ErrorMessage}", ex.Message);
            }
        }
    }
}