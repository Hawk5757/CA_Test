using Serilog;
using StackExchange.Redis;
using AsyncJobProcessor.Interfaces;
using AsyncJobProcessor.Middleware;
using AsyncJobProcessor.Models;
using AsyncJobProcessor.Services; // Для HttpStatusCodes
using Polly; // Для базових типів Polly
using Polly.Extensions.Http; // Для ResilienceContext.Key<HttpRequestMessage>
using Prometheus;
using Prometheus.DotNetRuntime;

//config serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/log.txt", rollingInterval: RollingInterval.Day)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProcessId()
    .Enrich.WithThreadId()
    .MinimumLevel.Information()
    .CreateLogger();

try
{
    Log.Information("Starting web application");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    builder.Services.AddControllers();

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Setup Redis
    var redisConnection = builder.Configuration["RedisConnection"] ?? "localhost:6379";
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        var connectionString =
            sp.GetRequiredService<IConfiguration>().GetConnectionString("RedisConnection") 
            ?? "localhost:6379,abortConnect=false";
        return ConnectionMultiplexer.Connect(connectionString);
    });
    builder.Services.AddSingleton<IDatabase>(provider =>
        provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

    builder.Services.AddScoped<IJobService, JobService>();

    builder.Services.AddHealthChecks()
        .AddRedis(redisConnection, name: "Redis Health Check", tags: ["db", "redis"]);

    // Налаштування HttpClient з Polly Resilience Policies
    builder.Services.AddHttpClient("ThirdPartyService", client =>
        {
            var thirdPartyServiceUrl = builder.Configuration["ThirdPartyService:BaseUrl"]
                                       ?? throw new InvalidOperationException(
                                           "ThirdPartyService:BaseUrl is not configured.");
            client.BaseAddress = new Uri(thirdPartyServiceUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .AddPolicyHandler(GetRetryPolicy())
        .AddPolicyHandler(GetCircuitBreakerPolicy())
        .AddPolicyHandler(GetTimeoutPolicy());
    
    builder.Services.AddHttpClient<TestClientService>(); // Автоматично інжектує HttpClient

    builder.Services.AddSingleton<TestClientService>();

    static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    Log.Warning(
                        $"Retry {retryCount} encountered for {context.PolicyKey} at {timeSpan.TotalSeconds}s: {exception.Result?.StatusCode} - {exception.Exception?.Message ?? exception.Result?.ReasonPhrase}");
                });
    }

    static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (exception, breakDelay) =>
                {
                    Log.Warning(
                        $"Circuit breaker is tripping for {breakDelay.TotalSeconds}s due to {exception.Result?.StatusCode} - {exception.Exception?.Message ?? exception.Result?.ReasonPhrase}");
                },
                onReset: () => Log.Information("Circuit breaker has reset."),
                onHalfOpen: () => Log.Information("Circuit breaker is in half-open state.")
            );
    }

    static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy()
    {
        return Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(10));
    }

    // Налаштування збору метрик .NET Runtime (GC, Threads тощо)
    DotNetRuntimeStatsBuilder.Default().StartCollecting();

    var app = builder.Build();

    // setup HTTP request pipeline
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
        
        using (var scope = app.Services.CreateScope())
        {
            var testClientService = scope.ServiceProvider.GetRequiredService<TestClientService>();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var jobService = scope.ServiceProvider.GetRequiredService<IJobService>(); // Переконайтеся, що JobService зареєстрований і доступний

            var callbackHost = configuration["CallbackHost"] ?? "https://localhost:7213";

            // --- Зміни починаються тут ---

            // *** 1. Створюємо JobId ОДИН раз і використовуємо його для всіх подальших кроків ***
            var job1Id = Guid.NewGuid().ToString();
            var job2Id = Guid.NewGuid().ToString();
            var job3Id = Guid.NewGuid().ToString();

            // *** 2. Зберігаємо завдання в Redis вручну для тестового сценарію ***
            // Це імітує те, що JobsController робив би при отриманні реального JobRequest.
            // Ми повинні "повідомити" AsyncJobProcessor, що ці завдання існують і перебувають у статусі Pending.

            // Модель JobData - це ваша внутрішня модель для зберігання в Redis
            // Припускаємо, що у вас є такий клас у AsyncJobProcessor.Models
            // Якщо ні, створіть його. Він має містити як мінімум JobId та Status.
            // public class JobData { public string JobId { get; set; } public JobStatus Status { get; set; } public string RequestData { get; set; } }

            await jobService.SaveJobAsync(new JobData { JobId = job1Id, Status = JobStatus.Pending, RequestData = "Test Data 1 (client service)" });
            await jobService.SaveJobAsync(new JobData { JobId = job2Id, Status = JobStatus.Pending, RequestData = "Test Data 2 (client service)" });
            await jobService.SaveJobAsync(new JobData { JobId = job3Id, Status = JobStatus.Pending, RequestData = "Test Data 3 (client service)" });


            // *** 3. Викликаємо TestClientService для відправки запитів до MockThirdPartyService ***
            // CallbackUrl містить той самий JobId, який ми щойно зберегли в Redis.
            _ = testClientService.RunTestJobAsync("Test Data 1 (client service)", $"{callbackHost}/api/callbacks/jobs/{job1Id}");
            _ = testClientService.RunTestJobAsync("Test Data 2 (client service)", $"{callbackHost}/api/callbacks/jobs/{job2Id}");
            _ = testClientService.RunTestJobAsync("Test Data 3 (client service)", $"{callbackHost}/api/callbacks/jobs/{job3Id}");

            // --- Зміни закінчуються тут ---
        }
    }

    app.UseHttpsRedirection();

    // Включаємо Prometheus Middleware для метрик запитів ASP.NET Core
    // За замовчуванням, він виставляє метрики на /metrics
    app.UseHttpMetrics();

    // [!!!] Реєстрація нашого HmacValidationMiddleware
    // Це Middleware має бути розміщено ПЕРЕД app.UseAuthorization() та app.MapControllers(),
    // щоб він міг перехопити запит до того, як він досягне контролера.
    app.UseMiddleware<HmacValidationMiddleware>();

    app.UseAuthorization();

    app.MapHealthChecks("/health");

    app.MapControllers();

    // Дозволяємо ендпоінт для метрик Prometheus
    // Він доступний за URL /metrics
    app.UseMetricServer();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}