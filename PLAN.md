# Captcha Solver Proxy API — Implementation Plan

## 1. Overview

An ASP.NET Core 10 Web API that acts as a **concurrency-gated streaming proxy** to the upstream Qwen Captcha Solver API. It manages a fixed pool of concurrent "slots" (URLs) using `System.Threading.Channels`, streams SSE responses from upstream directly to clients, and queues excess requests until a slot is freed.

---

## 2. Architecture

```
┌──────────────┐       ┌────────────────────────────────────────────┐       ┌──────────────────────┐
│   Client     │──SSE──│  ASP.NET Core 10 Proxy API                 │──SSE──│  Upstream Solver API │
│              │       │                                            │       │  (max 3 concurrent)  │
│  POST /solve │       │  ┌──────────────────────────────────────┐  │       │                      │
│  (stream)    │       │  │  Channel<Slot> (bounded, capacity=3) │  │       │  POST /solve         │
│              │       │  │  Slot1 │ Slot2 │ Slot3               │  │       │  (SSE stream)        │
│              │       │  └──────────────────────────────────────┘  │       │                      │
│              │       │                                            │       │                      │
│              │       │  Request → Acquire Slot → Forward → Stream │       │                      │
│              │       │           ← Release Slot ← Done            │       │                      │
└──────────────┘       └────────────────────────────────────────────┘       └──────────────────────┘
```

### Key Design Decisions

| Concern | Decision |
|---------|----------|
| Concurrency control | Bounded `Channel<int>` as a slot pool (capacity = max concurrent URLs) |
| Slot acquisition | `await channel.Reader.ReadAsync(ct)` — blocks asynchronously if pool is empty |
| Slot release | `await channel.Writer.WriteAsync(slotId, ct)` — returns slot to pool |
| Streaming to upstream | `HttpClient.PostAsync` with `HttpCompletionOption.ResponseHeadersRead` |
| Streaming to client | SSE via `HttpResponse.BodyWriter` (PipeWriter), chunk-by-chunk passthrough |
| Cancellation | `CancellationToken` propagated through the entire pipeline |
| Slot URL binding | Each slot carries its own logical "URL" identifier; request binds to acquired slot |

---

## 3. Project Structure

```
QwenSolvingApi/
├── QwenSolvingApi.csproj
├── Program.cs
├── appsettings.json
├── appsettings.Development.json
├── Models/
│   ├── SolveRequest.cs
│   └── SlotInfo.cs
├── Services/
│   ├── SlotPool.cs              ← Channel-based concurrency gate
│   └── CaptchaProxyService.cs   ← HTTP streaming to upstream
├── Controllers/
│   └── SolveController.cs       ← POST /solve SSE endpoint
└── Middleware/
    └── SlotMetricsMiddleware.cs ← (optional) observability
```

---

## 4. Component Design

### 4.1 Models

#### `SolveRequest.cs`
```csharp
public sealed class SolveRequest
{
    [JsonPropertyName("encrypted_token")]
    public string EncryptedToken { get; set; } = string.Empty;

    [JsonPropertyName("captcha_url")]
    public string? CaptchaUrl { get; set; }
}
```

#### `SlotInfo.cs`
```csharp
public sealed class SlotInfo
{
    public int SlotId { get; init; }
    public string Url { get; init; } = string.Empty;
}
```

---

### 4.2 SlotPool — Channel-Based Concurrency Gate

**Responsibility:** Manage a fixed set of URL slots. Requests acquire a slot before proceeding; when the slot is busy, the caller awaits asynchronously.

**Implementation sketch:**

```csharp
public sealed class SlotPool : IDisposable
{
    private readonly Channel<SlotInfo> _channel;

    public SlotPool(int capacity, string upstreamBaseUrl)
    {
        _channel = Channel.CreateBounded<SlotInfo>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        for (int i = 0; i < capacity; i++)
        {
            _channel.Writer.TryWrite(new SlotInfo
            {
                SlotId = i,
                Url = upstreamBaseUrl
            });
        }
    }

    public async Task<SlotInfo> AcquireAsync(CancellationToken ct = default)
    {
        return await _channel.Reader.ReadAsync(ct);
    }

    public async Task ReleaseAsync(SlotInfo slot, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(slot, ct);
    }

    public int AvailableCount => _channel.Reader.Count;

    public void Dispose() => _channel.Writer.TryComplete();
}
```

**Key behaviors:**
- `AcquireAsync` **blocks asynchronously** (no thread spin) when all slots are in use.
- `ReleaseAsync` **must** be called in a `finally` block to guarantee slot return.
- The channel is bounded to `capacity` (default 3, matching upstream's `limit_concurrency`).
- Each slot carries a URL, allowing future per-slot upstream URL rotation.

---

### 4.3 CaptchaProxyService — Streaming Forwarder

**Responsibility:** Forward the SSE stream from the upstream API to the client response.

**Implementation sketch:**

```csharp
public sealed class CaptchaProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public CaptchaProxyService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task StreamSolveAsync(
        SolveRequest request,
        HttpResponse clientResponse,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("upstream");

        var upstreamBody = new
        {
            encrypted_token = request.EncryptedToken,
            captcha_url = request.CaptchaUrl
        };

        var httpContent = JsonContent.Create(upstreamBody);

        // ResponseHeadersRead enables streaming without buffering the full response
        using var response = await client.PostAsync(
            "/solve",
            httpContent,
            ct,
            HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        // Set SSE headers on client response
        clientResponse.ContentType = "text/event-stream";
        clientResponse.Headers.CacheControl = "no-cache";
        clientResponse.Headers.Connection = "keep-alive";
        clientResponse.StatusCode = 200;

        // Stream upstream → client chunk by chunk
        using var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[4096];
        int bytesRead;

        while ((bytesRead = await upstreamStream.ReadAsync(buffer, ct)) > 0)
        {
            await clientResponse.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            await clientResponse.Body.FlushAsync(ct);
        }
    }
}
```

**Key behaviors:**
- Uses `HttpCompletionOption.ResponseHeadersRead` to start streaming immediately.
- Reads upstream in 4 KB chunks, writes + flushes each chunk to the client.
- Propagates `CancellationToken` end-to-end.
- Does NOT buffer the entire upstream response in memory.

---

### 4.4 SolveController — SSE Endpoint

**Responsibility:** Expose `POST /solve`, acquire a slot, delegate streaming, release the slot.

```csharp
[ApiController]
public class SolveController : ControllerBase
{
    private readonly SlotPool _slotPool;
    private readonly CaptchaProxyService _proxyService;
    private readonly ILogger<SolveController> _logger;

    public SolveController(
        SlotPool slotPool,
        CaptchaProxyService proxyService,
        ILogger<SolveController> logger)
    {
        _slotPool = slotPool;
        _proxyService = proxyService;
        _logger = logger;
    }

    [HttpPost("/solve")]
    public async Task Solve([FromBody] SolveRequest request, CancellationToken ct)
    {
        SlotInfo? slot = null;

        try
        {
            // Acquire a slot — awaits asynchronously if all slots are busy
            _logger.LogInformation("Request waiting for a slot...");
            slot = await _slotPool.AcquireAsync(ct);
            _logger.LogInformation("Acquired slot {SlotId} (URL: {Url})", slot.SlotId, slot.Url);

            // Stream the upstream response to the client
            await _proxyService.StreamSolveAsync(request, Response, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request cancelled.");
            Response.StatusCode = 499;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during captcha solve.");
            if (!Response.HasStarted)
            {
                Response.StatusCode = 502;
                await Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
            }
        }
        finally
        {
            // ALWAYS release the slot back to the pool
            if (slot is not null)
            {
                await _slotPool.ReleaseAsync(slot, CancellationToken.None);
                _logger.LogInformation("Released slot {SlotId}", slot.SlotId);
            }
        }
    }
}
```

**Key behaviors:**
- `AcquireAsync` blocks the request (non-thread-blocking) until a slot is available.
- The slot is released in `finally`, guaranteeing return even on exceptions.
- SSE headers are set before streaming begins.
- Client disconnection propagates via `CancellationToken` → upstream request is also cancelled.

---

### 4.5 Program.cs — DI & Configuration

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configuration
var upstreamUrl = builder.Configuration["UpstreamApi:BaseUrl"]
    ?? "https://qwencaptchasolver-production-a965.up.railway.app";
var maxConcurrency = builder.Configuration.GetValue<int>("UpstreamApi:MaxConcurrency", 3);

// Register SlotPool as singleton
var slotPool = new SlotPool(maxConcurrency, upstreamUrl);
builder.Services.AddSingleton(slotPool);

// Register proxy service
builder.Services.AddScoped<CaptchaProxyService>();

// Named HttpClient for upstream
builder.Services.AddHttpClient("upstream", client =>
{
    client.BaseAddress = new Uri(upstreamUrl);
    client.Timeout = TimeSpan.FromMinutes(5); // generous timeout for captcha solving
});

builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
app.Run();
```

---

### 4.6 appsettings.json

```json
{
  "UpstreamApi": {
    "BaseUrl": "https://qwencaptchasolver-production-a965.up.railway.app",
    "MaxConcurrency": 3,
    "TimeoutSeconds": 300
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

---

## 5. Request Lifecycle (Sequence Diagram)

```
Client          SolveController       SlotPool (Channel)      CaptchaProxyService      Upstream API
  │                  │                      │                        │                      │
  │──POST /solve────▶│                      │                        │                      │
  │                  │──AcquireAsync()─────▶│                        │                      │
  │                  │                      │                        │                      │
  │                  │  (if no slot free,   │                        │                      │
  │                  │   awaits here)       │                        │                      │
  │                  │                      │                        │                      │
  │                  │◀──SlotInfo───────────│                        │                      │
  │                  │                      │                        │                      │
  │                  │──StreamSolveAsync()──────────────────────────▶│                      │
  │                  │                      │                        │──POST /solve────────▶│
  │                  │                      │                        │                      │
  │◀─SSE chunk──────────────────────────────────────────────────────│◀─SSE chunk───────────│
  │◀─SSE chunk──────────────────────────────────────────────────────│◀─SSE chunk───────────│
  │◀─SSE chunk──────────────────────────────────────────────────────│◀─SSE chunk───────────│
  │                  │                      │                        │                      │
  │◀─done───────────────────────────────────────────────────────────│◀─done────────────────│
  │                  │                      │                        │                      │
  │                  │──ReleaseAsync()─────▶│                        │                      │
  │                  │                      │  (slot available again)│                      │
  │                  │                      │                        │                      │
```

---

## 6. Concurrency Model

### Slot Pool Behavior

| Scenario | Behavior |
|----------|----------|
| Slot available | Request acquires immediately, proceeds to upstream |
| All slots busy | Request awaits on `Channel.Reader.ReadAsync()` (async, no thread block) |
| Request completes | Slot released via `Channel.Writer.WriteAsync()` |
| Request fails/cancelled | Slot released in `finally` block |
| Client disconnects | `CancellationToken` fires → upstream request cancelled → slot released |

### Why Channel over SemaphoreSlim?

| Feature | Channel | SemaphoreSlim |
|---------|---------|---------------|
| Carries metadata (URL per slot) | ✅ Yes | ❌ No |
| Async-first | ✅ Yes | ✅ Yes |
| Bounded by design | ✅ Yes | ✅ Yes |
| Supports multiple producers/consumers | ✅ Yes | ⚠️ Limited |
| Allows per-slot URL rotation | ✅ Yes | ❌ No |

The Channel approach lets each slot carry its own URL, enabling future scenarios like:
- URL rotation / load balancing across multiple upstream instances
- Per-slot health tracking
- Slot-specific configuration

---

## 7. Error Handling Strategy

| Error | Handling |
|-------|----------|
| Upstream returns non-2xx | Proxy returns 502 with error body (if response not started) |
| Upstream stream breaks mid-transfer | Client receives partial SSE; slot is released in `finally` |
| Client disconnects mid-stream | `OperationCanceledException` → slot released → no error response |
| Slot acquisition timeout (optional) | Configurable wait timeout → 503 Service Unavailable |
| Invalid request body | 400 Bad Request (model binding) |

---

## 8. Configuration & Extensibility

### Planned Configuration Options

| Key | Default | Description |
|-----|---------|-------------|
| `UpstreamApi:BaseUrl` | Railway URL | Upstream solver API base URL |
| `UpstreamApi:MaxConcurrency` | `3` | Number of concurrent slots |
| `UpstreamApi:TimeoutSeconds` | `300` | HTTP timeout for upstream calls |
| `UpstreamApi:SlotAcquireTimeoutSeconds` | `0` (infinite) | Max wait time for a slot (0 = wait forever) |

### Future Extensions

1. **Slot acquisition timeout** — if a request waits > N seconds for a slot, return 503.
2. **Health endpoint** — `GET /health` returning slot availability.
3. **Metrics** — track active slots, queue depth, average solve time.
4. **Multiple upstream URLs** — each slot can point to a different upstream instance.
5. **Retry logic** — on upstream failure, retry with backoff before releasing slot.

---

## 9. Implementation Order

| Step | Task | Depends On |
|------|------|------------|
| 1 | Create ASP.NET Core 10 project scaffold | — |
| 2 | Define `SolveRequest` and `SlotInfo` models | — |
| 3 | Implement `SlotPool` (Channel-based) | Models |
| 4 | Implement `CaptchaProxyService` (streaming) | Models |
| 5 | Implement `SolveController` with SSE streaming | SlotPool, ProxyService |
| 6 | Wire up DI in `Program.cs` | All services |
| 7 | Add `appsettings.json` configuration | — |
| 8 | Build & verify compilation | All |
| 9 | Test with upstream API (integration) | Build |

---

## 10. Testing Strategy

| Test Type | What to Verify |
|-----------|----------------|
| Unit — SlotPool | Acquire/release cycle, bounded capacity, async wait behavior |
| Unit — Controller | Correct slot acquire/release on success/failure/cancellation |
| Integration — Streaming | SSE chunks pass through unmodified |
| Integration — Concurrency | 4th request waits when 3 are active; proceeds when one finishes |
| Edge — Client disconnect | Slot released, upstream cancelled |
| Edge — Upstream failure | 502 returned, slot released |

---

## 11. Dependencies

| Package | Purpose |
|---------|--------|
| `System.Threading.Channels` | Built-in to .NET — Channel<T> |
| `Microsoft.AspNetCore.*` | Built-in — SSE, Controllers |
| `Microsoft.Extensions.Http` | `IHttpClientFactory` for upstream calls |
| No external packages needed | All streaming is native ASP.NET Core |

---

## 12. Summary

The API acts as a **streaming concurrency gate**:

1. **Client** sends `POST /solve` with `encrypted_token` + optional `captcha_url`.
2. **Controller** acquires a slot from the Channel pool (waits if none available).
3. **ProxyService** forwards the request to upstream and streams SSE chunks back.
4. **Controller** releases the slot in `finally`.
5. **Next waiting request** acquires the freed slot and proceeds.

No request is ever rejected due to concurrency — they simply queue until a slot is free.
