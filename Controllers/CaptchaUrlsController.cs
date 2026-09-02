using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QwenSolvingApi.Data;
using QwenSolvingApi.Models;
using QwenSolvingApi.Services;

namespace QwenSolvingApi.Controllers;

/// <summary>
/// Admin-only management of captcha URLs stored in the CaptchaUrls table.
/// Requires a valid JWT (obtained from POST /auth/login).
/// </summary>
[ApiController]
[Authorize]
[Route("captcha-urls")]
public class CaptchaUrlsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly SlotPool _slotPool;
    private readonly ILogger<CaptchaUrlsController> _logger;

    public CaptchaUrlsController(
        AppDbContext db,
        SlotPool slotPool,
        ILogger<CaptchaUrlsController> logger)
    {
        _db = db;
        _slotPool = slotPool;
        _logger = logger;
    }

    /// <summary>
    /// Lists all stored captcha URLs (newest first).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<CaptchaUrlResponse>>> GetAll(CancellationToken ct)
    {
        var urls = await _db.CaptchaUrls
            .AsNoTracking()
            .OrderByDescending(u => u.CreatedAtUtc)
            .Select(u => new CaptchaUrlResponse
            {
                Id = u.Id,
                Url = u.Url,
                CreatedAtUtc = u.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(urls);
    }

    /// <summary>
    /// Adds a new captcha URL to the CaptchaUrls table.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CaptchaUrlResponse>> Add(
        [FromBody] AddCaptchaUrlRequest request,
        CancellationToken ct)
    {
        var url = request.Url?.Trim();

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return BadRequest(new { error = "A valid absolute URL is required." });
        }

        var exists = await _db.CaptchaUrls.AnyAsync(u => u.Url == url, ct);
        if (exists)
        {
            return Conflict(new { error = "This captcha URL already exists." });
        }

        var entity = new CaptchaUrl
        {
            Url = url,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.CaptchaUrls.Add(entity);
        await _db.SaveChangesAsync(ct);

        // Register the new URL with the slot pool immediately — the new slots
        // become available to waiting requests without restarting the API.
        _slotPool.RegisterUrl(entity.Url);

        _logger.LogInformation(
            "Added captcha URL {Url} (Id={Id}) and registered {Slots} slot(s) in the pool",
            url, entity.Id, _slotPool.SlotsPerUrl);

        return CreatedAtAction(
            nameof(GetAll),
            new CaptchaUrlResponse
            {
                Id = entity.Id,
                Url = entity.Url,
                CreatedAtUtc = entity.CreatedAtUtc
            });
    }
}
