using Serilog;
using StackExchange.Redis;
using AsyncJobProcessor.Interfaces;
using AsyncJobProcessor.Middleware;
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
            sp.GetRequiredService<IConfiguration>().GetConnectionString("RedisConnection") // Змінено тут
            ?? "localhost:6379,abortConnect=false"; // Додано fallback значення
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
            // Запустіть один або кілька тестів
            _ = testClientService.RunTestJobAsync("Test Data 1");
            _ = testClientService.RunTestJobAsync("Another Test Data");
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