using AsyncJobProcessor.Interfaces; // Додаємо using для інтерфейсу
using AsyncJobProcessor.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace AsyncJobProcessor.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CallbacksController : ControllerBase
    {
        private readonly IJobService _jobService;
        private readonly ILogger<CallbacksController> _logger;

        public CallbacksController(IJobService jobService, ILogger<CallbacksController> logger)
        {
            _jobService = jobService;
            _logger = logger;
        }

        /// <summary>
        /// Приймає колбек з результатом виконання завдання від стороннього сервісу.
        /// Сторонній сервіс викликає цей endpoint після завершення асинхронної обробки.
        /// </summary>
        /// <param name="jobId">Ідентифікатор завдання, переданий у URL.</param>
        /// <param name="result">Об'єкт з результатом завдання (статус, повідомлення, корисне навантаження).</param>
        /// <returns>HTTP 200 OK, якщо колбек успішно оброблено.</returns>
        [HttpPost("jobs/{jobId}")]
        [ProducesResponseType(200)] // Успішне отримання колбеку
        [ProducesResponseType(400)] // Невалідний запит (наприклад, jobId порожній, результат null)
        [ProducesResponseType(404)] // Завдання з таким jobId не знайдено (хоча ми його створюємо)
        [ProducesResponseType(500)] // Внутрішня помилка сервера
        public async Task<IActionResult> ProcessJobCallback([FromRoute] string jobId, [FromBody] JobResult result)
        {
            // [1] Валідація вхідних параметрів колбеку
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Invalid JobResult model received for jobId {JobId}. Errors: {Errors}", jobId, ModelState);
                return BadRequest(ModelState); // Поверне деталі помилок валідації
            }

            // [1.1] Додаткова перевірка: валідність формату UUID для jobId
            if (!Guid.TryParse(jobId, out _)) // Спроба розпарсити jobId як Guid
            {
                _logger.LogWarning("Received callback for jobId '{JobId}' with invalid UUID format.", jobId);
                return BadRequest("JobId must be a valid UUID format.");
            }

            if (result == null)
            {
                _logger.LogWarning("Received callback for jobId {JobId} with null result.", jobId);
                return BadRequest("JobResult cannot be null.");
            }
            
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Invalid JobResult model received for jobId {JobId}. Errors: {Errors}", jobId, ModelState);
                return BadRequest(ModelState); // Поверне деталі помилок валідації
            }

            _logger.LogInformation("Received callback for jobId: {JobId} with status: {Status}", jobId, result.Status);

            try
            {
                // [2] Делегуємо обробку JobService.
                // JobService оновить Redis та опублікує результат у Pub/Sub,
                // що розблокує очікування в `RegisterAndWaitForJob`.
                bool jobFoundAndProcessed = await _jobService.ProcessJobCallbackAsync(jobId, result);

                if (!jobFoundAndProcessed)
                {
                    _logger.LogWarning("Callback received for job {JobId} but job key was not found in Redis (possibly expired). Returning 404 Not Found.", jobId);
                    return NotFound(new { message = $"Job with ID '{jobId}' not found or already expired." }); // [нове] Повертаємо 404
                }

                return Ok();
            }
            catch (InvalidOperationException ex) 
            {
                // [4] Якщо JobService викидає InvalidOperationException (наприклад, jobId не знайдено,
                // хоча в нашому поточному сценарії це малоймовірно, оскільки ми створюємо jobId на початку).
                _logger.LogError(ex, "JobId {JobId} not found or invalid state during callback processing.", jobId);
                return NotFound(ex.Message); // 404 Not Found
            }
            catch (Exception ex)
            {
                // [5] Загальна обробка інших непередбачених помилок.
                _logger.LogError(ex, "Error processing callback for JobId: {JobId}. Message: {ErrorMessage}", jobId, ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { message = "An unexpected error occurred while processing the callback.", details = ex.Message });
            }
        }
    }
}