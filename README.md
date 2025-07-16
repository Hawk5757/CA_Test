# Async Job Processor

This project implements a robust and scalable asynchronous job processing system in ASP.NET Core, designed to handle long-running tasks by offloading them to an external service and awaiting their completion via callbacks.

---

## 1. Technical Task Overview

The core objective is to create an API endpoint that initiates a long-running job with an external (third-party) service and immediately returns to the client. The system then asynchronously waits for the external service to send a completion callback.

### Key Requirements:

* **Asynchronous Processing:**
    * Client request immediately returns a Job ID.
    * Job execution is offloaded to a third-party service.
    * The system waits for a callback from the third-party service.
* **Job Status Management:**
    * Track job states: `Pending`, `Completed`, `Failed`, `Cancelled`, `Timeout`.
    * Store job results (status, message, payload, error).
* **Callback Mechanism:**
    * The third-party service sends a callback with job results.
    * The callback endpoint must be secured (HMAC validation).
* **Resilience and Reliability:**
    * Retry mechanism for external service calls.
    * Circuit Breaker pattern to prevent cascading failures.
    * Timeout for waiting on third-party service callbacks.
* **Monitoring:**
    * Collect metrics: average job processing time, timeout percentage, number of concurrent waits.
* **Resource Cleanup:**
    * Unsubscribe from Redis channels and manage Redis key lifetimes.
* **Logging:**
    * Comprehensive logging for debugging and monitoring.
* **API Documentation:**
    * Swagger/OpenAPI for easy API exploration.

---

## 2. Implemented Solutions

The `AsyncJobProcessor` project leverages several modern .NET Core features and robust libraries to meet the above requirements.

### 2.1. Project Structure

* `Controllers/`: API endpoints for job registration and callback handling.
* `Models/`: Data Transfer Objects (DTOs) for requests, responses, and job states.
* `Services/`: Contains the core `JobService` logic for managing job lifecycle, Redis interaction, and external service communication.
* `Interfaces/`: Defines service contracts.
* `Middleware/`: Custom middleware for HMAC validation.

### 2.2. Asynchronous Processing & Job State Management

* **Immediate Response:** When a client requests a job, a unique `jobId` is generated, the job's initial `Pending` status and `createdAt` timestamp are stored in **Redis**, and the `jobId` is immediately returned.
* **`TaskCompletionSource` (TCS):** For each job, a `TaskCompletionSource<JobResult>` is created and stored in a `ConcurrentDictionary`. This allows the `RegisterAndProcessJobAsync` method to `await` the completion of a `Task` that will be fulfilled (or faulted) when the callback arrives.
* **Redis Pub/Sub:**
    * The `JobService` subscribes to a Redis Pub/Sub channel (`jobs:results:{jobId}`) specific to each job.
    * When the external service sends a callback, the `CallbacksController` processes it and publishes the `JobResult` to this specific Redis channel. This decouples callback reception from the waiting task, allowing `JobService` to unblock the awaiting `TCS` across different instances or even processes.
* **Job Status in Redis:** Job details (status, message, payload, error message, `createdAt`, `completedAt`) are stored as a **Redis Hash** under the key `job:{jobId}`. The `Status` field is now an **`enum` (`JobStatus`)** for type safety and readability (`Pending`, `Completed`, `Failed`, `Cancelled`, `Timeout`).

### 2.3. Callback Mechanism & Security

* **`CallbacksController`:** Exposes an endpoint (`/api/callbacks/jobs/{jobId}`) that the external service invokes upon job completion.
* **HMAC Validation Middleware:** A custom `HmacValidationMiddleware` intercepts incoming callbacks to verify their authenticity. It calculates an HMAC-SHA256 signature from the request body using a shared secret key and compares it against the `X-HMAC-SHA256` header provided by the external service. Invalid signatures result in a `401 Unauthorized` response. The shared secret key is retrieved from `IConfiguration`.

### 2.4. Resilience and Reliability (Polly)

The `HttpClient` used to call the third-party service is configured with **Polly** policies:

* **Retry Policy:** Automatically retries transient HTTP errors (`5xx`, `408`, or `429 Too Many Requests`) with an exponential back-off strategy (3 retries).
* **Circuit Breaker Policy:** Prevents overwhelming the external service if it consistently fails. If a configured number of failures occur, the circuit "trips" (opens), and subsequent calls immediately fail for a specified duration, allowing the external service to recover. It then enters a "half-open" state to test the service's health.
* **Timeout Policy:** Implements a client-side timeout for HTTP requests to the third-party service (10 seconds), preventing calls from hanging indefinitely.

### 2.5. Monitoring (Prometheus-net)

The application is instrumented with **Prometheus-net** to expose custom and runtime metrics:

* **Installation:** Uses `Prometheus.AspNetCore` for ASP.NET Core integration and `Prometheus.DotNetRuntime` for automatic .NET runtime metrics.
* **`/metrics` Endpoint:** Metrics are exposed on the standard `/metrics` endpoint.
* **Custom Metrics:**
    * **`async_job_processor_jobs_registered_total` (Counter):** Total number of jobs initiated.
    * **`async_job_processor_jobs_completed_total` (Counter):** Total number of jobs successfully completed.
    * **`async_job_processor_jobs_failed_total` (Counter with `reason` label):** Total number of jobs that ended in a non-successful state (`failed`, `cancelled`, `timeout`). The `reason` label helps distinguish failure types.
    * **`async_job_processor_jobs_concurrent_waiting` (Gauge):** Current number of jobs actively awaiting a callback. This provides real-time insight into the system's concurrency load.
    * **`async_job_processor_job_processing_duration_seconds` (Histogram):** Distribution of job processing times (from registration to callback completion), with configurable buckets for analysis.
* **Usage:** These metrics can be scraped by a **Prometheus server** and visualized using **Grafana** for comprehensive dashboards and alerting.

### 2.6. Resource Cleanup

* **Redis Pub/Sub Unsubscription:** The `JobService` explicitly unsubscribes from job-specific Redis channels once the `TaskCompletionSource` is either completed (result received), cancelled, or timed out. This prevents lingering subscriptions.
* **Redis Key TTL (Time To Live):** Upon job registration, each `job:{jobId}` key in Redis is assigned a **TTL of 24 hours**. This ensures that even if a job's status isn't explicitly read or if the application crashes, the Redis key will automatically expire and be removed after the specified duration, preventing indefinite data accumulation.

### 2.7. Logging (Serilog)

* **Serilog Integration:** The project uses Serilog for structured logging, configured to output to the console and a daily rolling file. This provides detailed insights into job lifecycle, external service calls, errors, and debugging information.

### 2.8. API Documentation (Swagger/OpenAPI)

* **SwaggerGen & SwaggerUI:** Integrated for automatic API documentation generation and an interactive UI, making it easy to test and understand the available endpoints.

---

## 3. Getting Started

### 3.1. Prerequisites

* .NET 8 SDK
* Redis server (running locally or accessible via network)
* A third-party service that can receive job requests and send callbacks. For testing, you might need a mock service.

## 3.2. Running MockThirdPartyService for Testing

For `AsyncJobProcessor` to function correctly in a testing environment (especially when using `TestClientService` to automatically initiate jobs), `MockThirdPartyService` needs to be running as a separate process. It simulates the behavior of the external third-party service that `AsyncJobProcessor` interacts with.

### Steps to Run MockThirdPartyService:

1.  **Open a new terminal or command prompt window.**
  * **Important:** `MockThirdPartyService` must run in its own, separate terminal window.

2.  **Navigate to the `MockThirdPartyService` project directory.**
  * For example, if your root repository is `C:\MySolution`, and `MockThirdPartyService` is a subfolder, execute:
      ```bash
      cd C:\MySolution\MockThirdPartyService
      ```

3.  **Start the service:**
  * In the terminal, while inside the `MockThirdPartyService` directory, execute the command:
      ```bash
      dotnet run
      ```

4.  **Verify the service launched successfully:**
  * In the `MockThirdPartyService` console, you should see messages indicating a successful launch and the port the service is listening on. Look for a line similar to:
      ```
      info: Microsoft.Hosting.Lifetime[14]
            Now listening on: http://localhost:5000
      ```
  * **Note:** By default, `MockThirdPartyService` is configured to listen on `http://localhost:5000`. Ensure this address matches the `ThirdPartyService:BaseUrl` value in your `AsyncJobProcessor`'s `appsettings.json` file. If `MockThirdPartyService` starts on a different port, update the configuration in `AsyncJobProcessor` accordingly.

5.  **Keep this terminal window open.**
  * `MockThirdPartyService` must remain running in the background while you are testing `AsyncJobProcessor`.

---

**Example Usage:**

After launching `MockThirdPartyService` and Redis, you can start `AsyncJobProcessor`. The `TestClientService` within `AsyncJobProcessor` will automatically send requests to `MockThirdPartyService`, which will, in turn, send callbacks back to `AsyncJobProcessor`. Observe the logs in the consoles of both services to confirm successful interaction.

### 3.3. Configuration

Update `appsettings.json` (or `appsettings.Development.json`) with your specific configurations:

```json
{
  "ConnectionStrings": {
    "RedisConnection": "localhost:6379,abortConnect=false"
  },
  "ThirdPartyService": {
    "BaseUrl": "http://localhost:5000" // Example URL for your mock third-party service
  },
  "Security": {
    "CallbackSecretKey": "YourVerySecretKeyForHMACValidation12345" // IMPORTANT: Use a strong, secure key in production
  },
  "CallbackHost": "https://localhost:723" // Base URL where this service's callback endpoint is accessible
}
