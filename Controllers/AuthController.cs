using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QwenSolvingApi.Data;
using QwenSolvingApi.Models;
using QwenSolvingApi.Services;

namespace QwenSolvingApi.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TokenService _tokenService;
    private readonly PasswordHasher<User> _passwordHasher;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AppDbContext db,
        TokenService tokenService,
        PasswordHasher<User> passwordHasher,
        ILogger<AuthController> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    /// <summary>
    /// Authenticates an admin user and returns a signed JWT.
    /// </summary>
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Username and password are required." });
        }

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == request.Username, ct);

        if (user is null)
        {
            _logger.LogWarning("Login failed: unknown username '{Username}'.", request.Username);
            return Unauthorized(new { error = "Invalid username or password." });
        }

        var verification = _passwordHasher.VerifyHashedPassword(
            user, user.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Login failed: invalid password for '{Username}'.", request.Username);
            return Unauthorized(new { error = "Invalid username or password." });
        }

        var (token, expiresAtUtc) = _tokenService.CreateToken(user);

        _logger.LogInformation("User '{Username}' logged in successfully.", user.Username);

        return Ok(new LoginResponse
        {
            Token = token,
            TokenType = "Bearer",
            ExpiresAtUtc = expiresAtUtc
        });
    }
}
