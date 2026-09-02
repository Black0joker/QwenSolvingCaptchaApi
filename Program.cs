using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QwenSolvingApi.Data;
using QwenSolvingApi.Models;
using QwenSolvingApi.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Read configuration

var maxConcurrency = builder.Configuration.GetValue<int>("UpstreamApi:MaxConcurrency", 3);
var timeoutSeconds = builder.Configuration.GetValue<int>("UpstreamApi:TimeoutSeconds", 300);

// SQL Server via Entity Framework Core
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));

// Password hashing and JWT issuance
builder.Services.AddSingleton<PasswordHasher<User>>();
builder.Services.AddSingleton<TokenService>();

// JWT bearer authentication
var jwtSecret = builder.Configuration["Jwt:SecretKey"]
    ?? throw new InvalidOperationException("Jwt:SecretKey is not configured.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "QwenSolvingApi";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "QwenSolvingApiClients";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();

// Fixed-window rate limiting: 5 requests per hour per IP
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests. Please try again later." }, ct);
    };
    options.AddFixedWindowLimiter("auth", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromHours(1);
        opt.QueueLimit = 0;
    });
});

// Redis distributed cache (AbortOnConnectFail = false so the app starts
// and falls back to the database when Redis is unreachable)
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(new ConfigurationOptions
    {
        EndPoints = { redisConnectionString },
        AbortOnConnectFail = false
    }));
builder.Services.AddSingleton<ICacheService, RedisCacheService>();

// Register SlotPool as singleton (one pool for the entire application).
// Slots are populated dynamically from the CaptchaUrls table (see startup block below);
// MaxConcurrency controls how many slots each registered URL contributes.
var slotPool = new SlotPool(maxConcurrency);
builder.Services.AddSingleton(slotPool);

// Register proxy service (scoped per request)
builder.Services.AddScoped<CaptchaProxyService>();

// Named HttpClient for upstream API calls
builder.Services.AddHttpClient("upstream", client =>
{
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
});

// Register the upstream health check background service
builder.Services.AddHostedService<UpstreamHealthCheckService>();

// Controllers
builder.Services.AddControllers();

// OpenAPI (development only)
builder.Services.AddOpenApi();

var app = builder.Build();

// Apply pending migrations, seed the initial admin user, and populate the slot pool
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        await db.Database.MigrateAsync();

        if (!await db.Users.AnyAsync())
        {
            var adminUsername = builder.Configuration["AdminSeed:Username"] ?? "admin";
            var adminPassword = builder.Configuration["AdminSeed:Password"] ?? "ChangeMe123!";

            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<User>>();
            var admin = new User
            {
                Username = adminUsername,
                Role = "Admin",
                CreatedAtUtc = DateTime.UtcNow
            };
            admin.PasswordHash = hasher.HashPassword(admin, adminPassword);

            db.Users.Add(admin);
            await db.SaveChangesAsync();

            app.Logger.LogInformation("Seeded admin user '{Username}'.", adminUsername);
        }

        // Populate the slot pool from the CaptchaUrls table:
        // each stored URL becomes SlotsPerUrl (= MaxConcurrency) channel slots.
        var captchaUrls = await db.CaptchaUrls
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => u.Url)
            .ToListAsync();

        foreach (var captchaUrl in captchaUrls)
        {
            slotPool.RegisterUrl(captchaUrl);
        }

        app.Logger.LogInformation(
            "Registered {UrlCount} captcha URL(s) from the database ({SlotCount} slot(s) total).",
            captchaUrls.Count, slotPool.TotalSlotCount);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Database migration/seeding failed. The API will start, but database-backed endpoints are unavailable.");

    }
}

// Middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();
