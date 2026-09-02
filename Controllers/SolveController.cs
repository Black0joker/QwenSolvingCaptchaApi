using Microsoft.AspNetCore.Mvc;
using QwenSolvingApi.Models;
using QwenSolvingApi.Services;

namespace QwenSolvingApi.Controllers;

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
            _logger.LogInformation(
                "Request waiting for a slot... Available slots: {Available}",
                _slotPool.AvailableCount);

            slot = await _slotPool.AcquireAsync(ct);

            _logger.LogInformation(
                "Acquired slot {SlotId} (URL: {Url})",
                slot.SlotId, slot.Url);

            // Stream the upstream response to the client
            await _proxyService.StreamSolveAsync(request, slot.Url, Response, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request cancelled by client.");
            Response.StatusCode = 499; // Client Closed Request
        }
        catch (HttpRequestException ex) when (!Response.HasStarted)
        {
            _logger.LogError(ex, "Upstream request failed.");
            Response.StatusCode = 502;
            await Response.WriteAsJsonAsync(new { error = "Upstream solver error", detail = ex.Message }, ct);
        }
        catch (Exception ex) when (!Response.HasStarted)
        {
            _logger.LogError(ex, "Unexpected error during captcha solve.");
            Response.StatusCode = 500;
            await Response.WriteAsJsonAsync(new { error = "Internal server error", detail = ex.Message }, ct);
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
