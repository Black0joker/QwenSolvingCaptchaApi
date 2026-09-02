using System.Text.Json;
using StackExchange.Redis;

namespace QwenSolvingApi.Services;

/// <summary>
/// Generic distributed cache abstraction.
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? expiry = null, CancellationToken ct = default);
}

/// <summary>
/// Redis-backed implementation of <see cref="ICacheService"/> using StackExchange.Redis.
/// Serializes values as JSON via System.Text.Json.
/// When Redis is unreachable, all operations degrade gracefully: reads return
/// default (causing callers to hit the database), and writes are skipped.
/// </summary>
public sealed class RedisCacheService : ICacheService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the Redis connection is currently usable.
    /// </summary>
    private bool IsAvailable
    {
        get
        {
            if (!_redis.IsConnected)
            {
                _logger.LogWarning("Redis is not connected. Falling back to database.");
                return false;
            }
            return true;
        }
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return default;
        }

        try
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetAsync(key);

            if (value.IsNullOrEmpty)
            {
                _logger.LogDebug("Cache miss for key: {Key}", key);
                return default;
            }

            _logger.LogDebug("Cache hit for key: {Key}", key);
            return JsonSerializer.Deserialize<T>(value.ToString(), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis GET failed for key: {Key}. Falling back to database.", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            var db = _redis.GetDatabase();
            var serialized = JsonSerializer.Serialize(value, JsonOptions);
            await db.StringSetAsync(key, serialized, expiry);

            _logger.LogDebug("Cache set for key: {Key} (expiry: {Expiry})", key, expiry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis SET failed for key: {Key}", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(key);

            _logger.LogDebug("Cache removed for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis DELETE failed for key: {Key}", key);
        }
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        // Attempt cache first; if Redis is down or misses, factory hits the database.
        var cached = await GetAsync<T>(key, ct);

        if (cached is not null)
        {
            return cached;
        }

        var value = await factory(ct);

        // Best-effort write to cache; failure here does not affect the response.
        await SetAsync(key, value, expiry, ct);

        return value;
    }
}
