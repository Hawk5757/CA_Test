using AsyncJobProcessor.Interfaces; // Додаємо using для інтерфейсу
using AsyncJobProcessor.Models;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;

namespace AsyncJobProcessor.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class JobsController : ControllerBase
    {
        private readonly IJobService _jobService;
        private readonly ILogger<JobsController> _logger;

        public JobsController(IJobService jobService, ILogger<JobsController> logger)
        {
            _jobService = jobService;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> RegisterJob([FromBody] JobRequest request, CancellationToken cancellationToken)
        {
            // Перевірка валідації моделі (стандартна ASP.NET Core поведінка)
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Invalid model state for RegisterJob request: {@ModelStateErrors}", ModelState);
                return BadRequest(ModelState);
            }

            try
            {
                // Викликаємо сервіс для реєстрації та обробки завдання
                var jobResult = await _jobService.RegisterAndProcessJobAsync(request, cancellationToken);
                _logger.LogInformation("Job {JobId} registered successfully.", jobResult.JobId);
                return Ok(jobResult); // Повертаємо 200 OK з результатом завдання
            }
            // Обробка винятків, пов'язаних з підключенням до Redis
            catch (RedisConnectionException ex)
            {
                _logger.LogError(ex, "Redis connection error while registering job: {ErrorMessage}", ex.Message);
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { message = "Service is temporarily unavailable due to a Redis connection issue. Please try again later.", details = ex.Message });
            }
            // Обробка винятків, пов'язаних з таймаутом Redis
            catch (RedisTimeoutException ex)
            {
                _logger.LogError(ex, "Redis timeout error while registering job: {ErrorMessage}", ex.Message);
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { message = "Service is temporarily unavailable due to a Redis timeout. Please try again later.", details = ex.Message });
            }
            // Обробка винятків, пов'язаних зі скасуванням запиту клієнтом
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Job registration was cancelled by the client.");
                return StatusCode(StatusCodes.Status400BadRequest, // Або 409 Conflict, якщо завдання вже було створено, але не оброблено
                    new { message = "Job registration was cancelled by the client." });
            }
            // Обробка таймауту очікування колбеку (виняток, який прокидається з JobService)
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Job processing timed out waiting for callback.");
                return StatusCode(StatusCodes.Status504GatewayTimeout, // 504 є більш доречним для таймауту на стороні сервера/залежності
                    new { message = "Job processing timed out waiting for the external service callback. The job might still be processing.", details = ex.Message });
            }
            // Загальна обробка інших неочікуваних винятків
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while registering job.");
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { message = "An unexpected internal server error occurred.", details = ex.Message });
            }
        }

        /// <summary>
        /// Реєструє нове асинхронне завдання та очікує його виконання.
        /// Цей метод блокує HTTP-з'єднання до отримання колбеку або таймауту/скасування.
        /// </summary>
        /// <param name="request">Дані для завдання (наприклад, { "Data": "some-input" }).</param>
        /// <param name="cancellationToken">Токен скасування, що надходить з HTTP-запиту клієнта.</param>
        /// <returns>Результат виконання завдання.</returns>
        [HttpPost("register-and-wait")]
        [ProducesResponseType(typeof(JobResult), 200)] // Успіх, повертає JobResult
        [ProducesResponseType(400)] // Невалідний запит
        [ProducesResponseType(499)] // Клієнт закрив запит (Client Closed Request - неофіційний, але поширений)
        [ProducesResponseType(504)] // Таймаут шлюзу (Gateway Timeout)
        [ProducesResponseType(500)] // Внутрішня помилка сервера
        public async Task<IActionResult> RegisterAndWaitForJob([FromBody] JobRequest request, CancellationToken cancellationToken)
        {
            // [1] Валідація вхідних даних
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Invalid JobRequest received. Errors: {Errors}", ModelState);
                return BadRequest(ModelState);
            }

            _logger.LogInformation("Received request to register and wait for a job. Request data: {RequestData}", request.Data);

            try
            {
                // [2] Делегування обробки JobService
                // Метод JobService блокуватиметься, доки не отримає результат через Redis Pub/Sub,
                // або доки не відбудеться таймаут/скасування.
                var result = await _jobService.RegisterAndProcessJobAsync(request, cancellationToken);
                _logger.LogInformation("Job completed successfully and result returned for request data: {RequestData}", request.Data);
                return Ok(result); // [3] Повертаємо успішний результат
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // [4] Обробка скасування клієнтом (наприклад, клієнт закрив браузер)
                // `cancellationToken.IsCancellationRequested` перевіряє, чи було скасування ініційовано ззовні.
                _logger.LogWarning("Client cancelled the job processing for request data: {RequestData}", request.Data);
                // Використовуємо 499 - неофіційний HTTP-статус, але часто використовується для Client Closed Request.
                return StatusCode(499, "Client Closed Request (Operation was cancelled)."); 
            }
            catch (TimeoutException)
            {
                // [5] Обробка таймауту, якщо JobService не зміг отримати результат за відведений час.
                _logger.LogError("Job processing timed out for request data: {RequestData}", request.Data);
                // 504 Gateway Timeout - підходить, якщо сервіс не отримав відповідь від іншого сервісу.
                return StatusCode(504, "Gateway Timeout (Job processing exceeded allowed time).");
            }
            catch (Exception ex)
            {
                // [6] Загальна обробка інших непередбачених помилок.
                _logger.LogError(ex, "An error occurred while processing job for request data: {RequestData}", request.Data);
                return StatusCode(500, $"Internal Server Error: {ex.Message}");
            }
        }
    }
}