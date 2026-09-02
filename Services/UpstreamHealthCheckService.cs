using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QwenSolvingApi.Data;
using QwenSolvingApi.Models;

namespace QwenSolvingApi.Services;

/// <summary>
/// Background service that periodically tests the upstream API health.
/// Sends GET /health and validates the response is {"status":"ok"}.
/// Retries up to 3 times. If all attempts fail for a URL, it removes
/// that URL from the database and resets the slot pool.
/// </summary>
public sealed class UpstreamHealthCheckService : BackgroundService
{
    private readonly SlotPool _slotPool;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UpstreamHealthCheckService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICacheService _cache;

    private const int MaxRetries = 3;
    private const string CacheKey = "captcha-urls:all";

    public UpstreamHealthCheckService(
        SlotPool slotPool,
        IHttpClientFactory httpClientFactory,
        ILogger<UpstreamHealthCheckService> logger,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ICacheService cache)
    {
        _slotPool = slotPool;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkIntervalSeconds = _configuration.GetValue<int>("UpstreamApi:HealthCheckIntervalSeconds", 30);

        _logger.LogInformation(
            "Upstream health check service started. Interval: {Interval}s",
            checkIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), stoppingToken);

                // Fetch captcha URLs from cache; falls back to database on cache miss
                var captchaUrls = await _cache.GetOrSetAsync(
                    CacheKey,
                    async token =>
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        return await db.CaptchaUrls
                            .AsNoTracking()
                            .OrderByDescending(u => u.CreatedAtUtc)
                            .Select(u => new CaptchaUrlResponse
                            {
                                Id = u.Id,
                                Url = u.Url,
                                CreatedAtUtc = u.CreatedAtUtc
                            })
                            .ToListAsync(token);
                    },
                    TimeSpan.FromMinutes(5),
                    stoppingToken);

                if (captchaUrls.Count == 0)
                {
                    _logger.LogDebug("No captcha URLs registered. Skipping health check.");
                    continue;
                }

                var unhealthyIds = new List<int>();

                foreach (var entry in captchaUrls)
                {
                    var isHealthy = await TestUpstreamHealthAsync(entry.Url, stoppingToken);

                    if (!isHealthy)
                    {
                        _logger.LogWarning(
                            "Captcha URL {Url} (Id={Id}) failed health check after {Retries} attempts. Marking for removal.",
                            entry.Url, entry.Id, MaxRetries);
                        unhealthyIds.Add(entry.Id);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Captcha URL {Url} (Id={Id}) health check passed.",
                            entry.Url, entry.Id);
                    }
                }

                if (unhealthyIds.Count > 0)
                {
                    await RemoveUnhealthyUrlsAsync(unhealthyIds, stoppingToken);
                    await DequeueAndResetSlotsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in health check loop.");
            }
        }

        _logger.LogInformation("Upstream health check service stopped.");
    }

    /// <summary>
    /// Tests the upstream GET /health endpoint with up to 3 retry attempts.
    /// Validates the response body is JSON with {"status":"ok"}.
    /// </summary>
    private async Task<bool> TestUpstreamHealthAsync(string upstreamUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("upstream");

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Health check attempt {Attempt}/{MaxRetries}: GET {Url}/health",
                    attempt, MaxRetries, upstreamUrl);

                using var response = await client.GetAsync($"{upstreamUrl}/health", ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                _logger.LogDebug(
                    "Attempt {Attempt} response: Status={Status}, Body={Body}",
                    attempt, (int)response.StatusCode, body);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Attempt {Attempt} returned HTTP {Status}",
                        attempt, (int)response.StatusCode);
                }
                else
                {
                    // Parse JSON and validate {"status":"ok"}
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("status", out var statusElement)
                        && statusElement.GetString() == "ok")
                    {
                        _logger.LogInformation(
                            "Attempt {Attempt} succeeded: {{\"status\":\"ok\"}}",
                            attempt);
                        return true;
                    }

                    _logger.LogWarning(
                        "Attempt {Attempt} returned valid JSON but status is not 'ok': {Body}",
                        attempt, body);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    "Attempt {Attempt} returned invalid JSON: {Message}",
                    attempt, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Attempt {Attempt} failed: {Message}",
                    attempt, ex.Message);
            }

            // Wait before retrying (except after the last attempt)
            if (attempt < MaxRetries)
            {
                var retryDelay = TimeSpan.FromSeconds(attempt * 2); // 2s, 4s
                _logger.LogInformation("Retrying in {Delay}s...", retryDelay.TotalSeconds);
                await Task.Delay(retryDelay, ct);
            }
        }

        return false;
    }

    /// <summary>
    /// Removes unhealthy captcha URLs from the database and invalidates the cache.
    /// </summary>
    private async Task RemoveUnhealthyUrlsAsync(List<int> unhealthyIds, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var removed = await db.CaptchaUrls
            .Where(u => unhealthyIds.Contains(u.Id))
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation(
            "Removed {Count} unhealthy captcha URL(s) from the database.",
            removed);

        // Invalidate the cached URL list so the next GetAll fetches fresh data
        await _cache.RemoveAsync(CacheKey, ct);
    }

    /// <summary>
    /// Dequeues all available slots from the pool and re-enqueues them
    /// to reset any stuck state after upstream failure.
    /// </summary>
    private async Task DequeueAndResetSlotsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Dequeuing all slots to reset pool state...");

        var dequeuedSlots = new List<SlotInfo>();

        // Non-blocking dequeue: read all currently available slots
        while (_slotPool.TryDequeue(out var slot))
        {
            if (slot is not null)
            {
                dequeuedSlots.Add(slot);
                _logger.LogDebug("Dequeued slot {SlotId}", slot.SlotId);
            }
        }

        _logger.LogInformation(
            "Dequeued {Count} slots. Re-enqueuing with fresh state...",
            dequeuedSlots.Count);

        // Re-enqueue all dequeued slots
        foreach (var slot in dequeuedSlots)
        {
            await _slotPool.ReleaseAsync(slot, ct);
            _logger.LogDebug("Re-enqueued slot {SlotId}", slot.SlotId);
        }

        _logger.LogInformation(
            "Slot pool reset complete. Available slots: {Available}",
            _slotPool.AvailableCount);
    }
}
