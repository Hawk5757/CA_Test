using AsyncJobProcessor.Models;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography;
using AsyncJobProcessor.Interfaces;
using Prometheus;

namespace AsyncJobProcessor.Services
{
    public class JobService : IJobService
    {
        // Нові статичні метрики Prometheus
        // Лічильник для всіх зареєстрованих завдань
        private static readonly Counter JobsRegisteredTotal = Metrics.CreateCounter(
            "async_job_processor_jobs_registered_total",
            "Total number of jobs registered.");

        // Лічильник для завдань, що завершилися успішно
        private static readonly Counter JobsCompletedTotal = Metrics.CreateCounter(
            "async_job_processor_jobs_completed_total",
            "Total number of jobs completed successfully.");

        // Лічильник для завдань, що завершилися з помилкою (будь-який неуспішний статус)
        private static readonly Counter JobsFailedTotal = Metrics.CreateCounter(
            "async_job_processor_jobs_failed_total",
            "Total number of jobs failed.",
            new CounterConfiguration { LabelNames = new[] { "reason" } }); // Додаємо лейбл для причини

        // Gauge для кількості одночасних очікувань (активних завдань)
        private static readonly Gauge JobsConcurrentWaiting = Metrics.CreateGauge(
            "async_job_processor_jobs_concurrent_waiting",
            "Number of jobs currently waiting for a result.");

        // Histogram для часу обробки завдань (від реєстрації до завершення)
        private static readonly Histogram JobProcessingDurationSeconds = Metrics.CreateHistogram(
            "async_job_processor_job_processing_duration_seconds",
            "Duration of job processing from registration to completion.",
            new HistogramConfiguration
            {
                Buckets = Histogram.LinearBuckets(start: 0, width: 5, count: 12) // Від 0 до 60 секунд з кроком 5с
            });

        private readonly IDatabase _redisDb;
        private readonly IConnectionMultiplexer _redisMux;
        private readonly ILogger<JobService> _logger;
        private readonly IConfiguration _configuration;
        private readonly HttpClient _thirdPartyHttpClient;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JobResult>> _jobCompletionSources;
        private readonly byte[] _callbackSecretKeyBytes;

        public JobService(IDatabase redisDb, IConnectionMultiplexer redisMux,
            ILogger<JobService> logger, IConfiguration configuration,
            IHttpClientFactory httpClientFactory)
        {
            _redisDb = redisDb;
            _redisMux = redisMux;
            _logger = logger;
            _configuration = configuration;
            _thirdPartyHttpClient = httpClientFactory.CreateClient("ThirdPartyService");
            _jobCompletionSources = new ConcurrentDictionary<string, TaskCompletionSource<JobResult>>();

            var secretKey = _configuration["Security:CallbackSecretKey"]
                            ?? throw new InvalidOperationException("CallbackSecretKey is not configured.");
            _callbackSecretKeyBytes = System.Text.Encoding.UTF8.GetBytes(secretKey);
        }

        public async Task<JobResult> RegisterAndProcessJobAsync(JobRequest request, CancellationToken cancellationToken)
        {
            JobsRegisteredTotal.Inc(); // Збільшуємо лічильник зареєстрованих завдань
            JobsConcurrentWaiting.Inc(); // Збільшуємо Gauge для одночасних очікувань

            // 1.1. Генеруємо унікальний jobId (UUID).
            var jobId = Guid.NewGuid().ToString();
            _logger.LogInformation("Generated jobId: {JobId}", jobId);

            // 1.2. Зберігаємо в Redis початковий запис стану задачі.
            var jobKey = $"job:{jobId}";
            var createdAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await _redisDb.HashSetAsync(jobKey, new HashEntry[]
            {
                new HashEntry("state", JobStatus.Pending.ToString()),
                new HashEntry("createdAt", createdAtUnixSeconds) // Зберігаємо час для розрахунку тривалості
            });
            _logger.LogInformation("Job {JobId} registered in Redis with state 'pending'.", jobId);

            //Встановлення TTL для ключа завдання в Redis
            // Ключ буде автоматично видалено через 24 години, якщо не буде оновлено.
            await _redisDb.KeyExpireAsync(jobKey, TimeSpan.FromMinutes(30));
            _logger.LogInformation("Set TTL for job {JobId} to 24 hours.", jobId);

            // 1.3. Підписуємо поточний екземпляр сервісу на Redis-канал.
            var pubSub = _redisMux.GetSubscriber();
            var channelName = $"jobs:results:{jobId}";
            var tcs = new TaskCompletionSource<JobResult>();
            _jobCompletionSources.TryAdd(jobId, tcs);
            _logger.LogInformation("Service subscribed to Redis channel {ChannelName} for jobId {JobId}.", channelName,
                jobId);

            await pubSub.SubscribeAsync(RedisChannel.Literal(channelName), async (channel, message) =>
            {
                _logger.LogInformation("Received message on channel {ChannelName} for jobId {JobId}.", channelName,
                    jobId);
                try
                {
                    var result = JsonSerializer.Deserialize<JobResult>(message.ToString());
                    if (result != null)
                    {
                        if (_jobCompletionSources.TryRemove(jobId, out var currentTcs))
                        {
                            currentTcs.SetResult(result);
                            _logger.LogInformation("TaskCompletionSource for jobId {JobId} completed with result.",
                                jobId);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "TaskCompletionSource for jobId {JobId} not found or already completed when message received.",
                                jobId);
                        }
                    }
                    else
                    {
                        _logger.LogError(
                            "Failed to deserialize JobResult from Redis message for jobId {JobId}. Message: {Message}",
                            jobId, message.ToString());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error deserializing or processing Redis message for jobId {JobId}. Message: {Message}", jobId,
                        message.ToString());
                }
                finally
                {
                    await pubSub.UnsubscribeAsync(RedisChannel.Literal(channelName));
                    _logger.LogInformation(
                        "Unsubscribed from Redis channel {ChannelName} for jobId {JobId} in Pub/Sub callback.",
                        channelName, jobId);
                }
            });

            // 2. Запуск обробки на сторонньому сервісі (з використанням HttpClient з Polly)
            var host = _configuration["CallbackHost"] ?? "https://localhost:7213";
            var callbackUrl = $"{host}/api/callbacks/jobs/{jobId}";

            var simulatedJobResultForHmac = new JobResult
            {
                JobId = jobId,
                Status = JobStatus.Completed, //  Використання Enum для статусу
                Message = $"Job {jobId} processed successfully by external service (simulated).",
                Payload = new { originalData = request.Data, processedValue = Guid.NewGuid().ToString() }
            };
            var jsonBodyForHmac = JsonSerializer.Serialize(simulatedJobResultForHmac);
            var hmacSignature = GenerateHmacSha256(jsonBodyForHmac, _callbackSecretKeyBytes);

            var externalServiceRequest = new
            {
                JobId = jobId,
                CallbackUrl = callbackUrl,
                Data = request.Data,
                SimulatedCallbackPayload = simulatedJobResultForHmac,
                SimulatedHmacSignature = hmacSignature
            };

            var jsonRequest = JsonSerializer.Serialize(externalServiceRequest);
            var content = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "Calling external service for jobId {JobId} with callback {CallbackUrl}. Request data: {RequestData}",
                jobId, callbackUrl, jsonRequest);

            try
            {
                var response = await _thirdPartyHttpClient.PostAsync("/startjob", content, cancellationToken);
                response.EnsureSuccessStatusCode();
                _logger.LogInformation(
                    "Successfully initiated job {JobId} on external service. HTTP Status: {StatusCode}", jobId,
                    response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex,
                    "Failed to initiate job {JobId} on external service after retries. Error: {ErrorMessage}", jobId,
                    ex.Message);
                await _redisDb.HashSetAsync(jobKey, new HashEntry[]
                {
                    new HashEntry("state", JobStatus.Failed.ToString()),
                    new HashEntry("errorMessage", $"Failed to call external service: {ex.Message}")
                });
                JobsFailedTotal.WithLabels("http_request_failed").Inc(); //  Збільшуємо лічильник помилок
                JobsConcurrentWaiting.Dec(); // Зменшуємо Gauge
                if (_jobCompletionSources.TryRemove(jobId, out var currentTcs))
                {
                    currentTcs.SetException(
                        new ApplicationException($"Failed to start external service for job {jobId}", ex));
                }

                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("HTTP request to external service for jobId {JobId} was cancelled.", jobId);
                JobsFailedTotal.WithLabels("request_cancelled").Inc(); // Збільшуємо лічильник помилок
                JobsConcurrentWaiting.Dec(); // Зменшуємо Gauge
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while calling external service for jobId {JobId}.",
                    jobId);
                await _redisDb.HashSetAsync(jobKey, new HashEntry[]
                {
                    new HashEntry("state", JobStatus.Failed.ToString()),
                    new HashEntry("errorMessage", $"Unexpected error calling external service: {ex.Message}")
                });
                JobsFailedTotal.WithLabels("unexpected_error").Inc(); //  Збільшуємо лічильник помилок
                JobsConcurrentWaiting.Dec(); //  Зменшуємо Gauge
                if (_jobCompletionSources.TryRemove(jobId, out var currentTcs))
                {
                    currentTcs.SetException(
                        new ApplicationException($"Unexpected error calling external service for job {jobId}", ex));
                }

                throw;
            }

            // 4. Очікування результату в контролері-ініціаторі.
            // 4.1. Очікування завершення TaskCompletionSource.Task з таймаутом.
            try
            {
                var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

                // Після успішного завершення (JobService.ProcessJobCallbackAsync буде викликано раніше і оновити стан в Redis)
                // Отримуємо createdAt з Redis для обчислення тривалості
                var jobHash = await _redisDb.HashGetAllAsync(jobKey);
                var createdAt = long.Parse(jobHash.First(h => h.Name == "createdAt").Value!);
                var duration = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - createdAt;
                JobProcessingDurationSeconds.Observe(duration); // [нове] Спостерігаємо тривалість

                JobsCompletedTotal.Inc(); // Збільшуємо лічильник успішних завдань
                _logger.LogInformation("Job {JobId} completed successfully.", jobId);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 4.4. У разі відміни клієнтом — повертаємо 499 (обробляється контролером)
                _logger.LogWarning("Job {JobId} cancelled by client. Cleaning up resources.", jobId);
                // Прибираємо підписку та TCS у випадку скасування
                if (_jobCompletionSources.TryRemove(jobId, out _))
                {
                    _logger.LogDebug("TaskCompletionSource for jobId {JobId} removed due to client cancellation.",
                        jobId);
                }

                // Оновлюємо стан в Redis на "cancelled"
                await _redisDb.HashSetAsync(jobKey, new HashEntry[]
                {
                    new HashEntry("state", JobStatus.Cancelled.ToString()), //  Використання Enum для статусу
                    new HashEntry("errorMessage", "Job cancelled by client.")
                });
                JobsFailedTotal.WithLabels("client_cancelled")
                    .Inc(); //  Статус "cancelled" ми теж вважаємо помилкою з точки зору успішності
                JobsConcurrentWaiting.Dec(); // Зменшуємо Gauge
                await pubSub.UnsubscribeAsync(RedisChannel.Literal(channelName));
                _logger.LogInformation(
                    "Unsubscribed from Redis channel {ChannelName} for jobId {JobId} due to cancellation.", channelName,
                    jobId);
                throw;
            }
            catch (TimeoutException)
            {
                // 4.3. У разі таймауту — повертаємо 504 (обробляється контролером)
                _logger.LogWarning("Job {JobId} timed out after 60 seconds. Cleaning up resources.", jobId);
                if (_jobCompletionSources.TryRemove(jobId, out _))
                {
                    _logger.LogDebug("TaskCompletionSource for jobId {JobId} removed due to timeout.", jobId);
                }

                await _redisDb.HashSetAsync(jobKey, new HashEntry[]
                {
                    new HashEntry("state", nameof(JobStatus.Timeout)), // Використання Enum для статусу
                    new HashEntry("errorMessage", "Job processing timed out.")
                });
                JobsFailedTotal.WithLabels("timeout").Inc(); // Збільшуємо лічильник таймаутів
                JobsConcurrentWaiting.Dec(); // Зменшуємо Gauge
                await pubSub.UnsubscribeAsync(RedisChannel.Literal(channelName));
                _logger.LogInformation(
                    "Unsubscribed from Redis channel {ChannelName} for jobId {JobId} due to timeout.", channelName,
                    jobId);
                throw;
            }
            finally
            {
                if (_jobCompletionSources.TryRemove(jobId, out _))
                {
                    _logger.LogDebug("TaskCompletionSource for jobId {JobId} ensured removal in finally block.", jobId);
                }
                // Gauge зменшується в catch або після успішного завершення
            }
        }

        // Допоміжний метод для генерації HMAC
        private string GenerateHmacSha256(string data, byte[] key)
        {
            using (var hmac = new HMACSHA256(key))
            {
                var dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
                var hashBytes = hmac.ComputeHash(dataBytes);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Обробляє колбек від стороннього сервісу з результатом виконання завдання.
        /// Оновлює стан завдання в Redis та публікує результат через Redis Pub/Sub.
        /// Цей метод тепер не виконує перевірку HMAC, це робить Middleware.
        /// </summary>
        /// <param name="jobId">Ідентифікатор завдання.</param>
        /// <param name="result">Об'єкт з результатом завдання.</param>
        public async Task<bool> ProcessJobCallbackAsync(string jobId, JobResult result)
        {
            _logger.LogInformation("Processing callback for jobId: {JobId}, status: {Status}", jobId, result.Status);

            var jobKey = $"job:{jobId}";

            // [1] Перевірка існування ключа
            if (!await _redisDb.KeyExistsAsync(jobKey))
            {
                _logger.LogWarning("Callback received for non-existent or already expired job: {JobId}", jobId);
                return false;
            }

            // [2] Читаємо існуюче завдання як рядок (JSON)
            var jobJson = await _redisDb.StringGetAsync(jobKey);
            if (jobJson.IsNullOrEmpty)
            {
                _logger.LogWarning("Job {JobId} found in Redis but content is empty.", jobId);
                return false;
            }

            JobData? jobData;
            try
            {
                jobData = JsonSerializer.Deserialize<JobData>(jobJson);
                if (jobData == null)
                {
                    _logger.LogError("Failed to deserialize JobData for JobId: {JobId} from Redis. Content: {JobJson}",
                        jobId, jobJson);
                    return false;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON deserialization error for JobId: {JobId}. Content: {JobJson}", jobId,
                    jobJson);
                return false;
            }

            // [3] Оновлюємо статус та інші поля
            jobData.Status = result.Status;
            jobData.CompletedAt = DateTimeOffset.UtcNow; // Оновлюємо час завершення
            jobData.ResultPayload = JsonSerializer.Serialize(result.Payload); // Зберігаємо payload як JSON-рядок
            jobData.ErrorMessage = result.ErrorMessage; // Зберігаємо повідомлення про помилку, якщо є

            _logger.LogInformation("Job {JobId} updated with status: {Status}.", jobId, jobData.Status);

            // [4] Серіалізуємо оновлений об'єкт назад у JSON
            var updatedJobJson = JsonSerializer.Serialize(jobData);

            // [5] Зберігаємо оновлений рядок назад у Redis (як STRING)
            await _redisDb.StringSetAsync(jobKey, updatedJobJson, TimeSpan.FromMinutes(30));

            _logger.LogInformation("Job {JobId} callback processed successfully. Job status updated in Redis.", jobId);

            JobsConcurrentWaiting.Dec(); // Зменшуємо Gauge, оскільки завдання завершено
            
            if (jobData.Status == JobStatus.Completed)
            {
                _logger.LogInformation("Job {JobId} completed successfully.", jobId);
            }
            else if (jobData.Status == JobStatus.Failed)
            {
                _logger.LogError("Job {JobId} failed: {ErrorMessage}", jobId, jobData.ErrorMessage);
            }

            return true;
        }

        public async Task SaveJobAsync(JobData job) // Ваша внутрішня модель завдання
        {
            var jobKey = $"job:{job.JobId}"; // Формування ключа Redis
            var serializedJob = JsonSerializer.Serialize(job);
            await _redisDb.StringSetAsync(jobKey, serializedJob, TimeSpan.FromMinutes(30)); // Зберігаємо з TTL
            _logger.LogInformation("Job {JobId} saved to Redis.", job.JobId);
        }
    }
}