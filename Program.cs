using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QwenSolvingApi.Data;
using QwenSolvingApi.Models;
using QwenSolvingApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Read configuration
var upstreamUrl = builder.Configuration["UpstreamApi:BaseUrl"]
    ?? "https://qwencaptchasolver-production-a965.up.railway.app";
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
    client.BaseAddress = new Uri(upstreamUrl);
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

        if (captchaUrls.Count == 0)
        {
            slotPool.RegisterUrl(upstreamUrl);
            app.Logger.LogWarning(
                "CaptchaUrls table is empty. Registered fallback upstream URL '{Url}' ({Slots} slot(s)).",
                upstreamUrl, maxConcurrency);
        }
        else
        {
            foreach (var captchaUrl in captchaUrls)
            {
                slotPool.RegisterUrl(captchaUrl);
            }

            app.Logger.LogInformation(
                "Registered {UrlCount} captcha URL(s) from the database ({SlotCount} slot(s) total).",
                captchaUrls.Count, slotPool.TotalSlotCount);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Database migration/seeding failed. The API will start, but database-backed endpoints are unavailable.");

        // Keep the proxy functional even without the database:
        // fall back to the configured upstream URL if no slots were registered.
        if (slotPool.AvailableCount == 0)
        {
            slotPool.RegisterUrl(upstreamUrl);
            app.Logger.LogWarning(
                "Registered fallback upstream URL '{Url}' ({Slots} slot(s)) because the database is unavailable.",
                upstreamUrl, maxConcurrency);
        }
    }
}

// Middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
