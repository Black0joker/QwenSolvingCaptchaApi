using System.Text.Json.Serialization;

namespace QwenSolvingApi.Models;

public sealed class LoginRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public sealed class LoginResponse
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";

    [JsonPropertyName("expires_at_utc")]
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed class AddCaptchaUrlRequest
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class CaptchaUrlResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("created_at_utc")]
    public DateTime CreatedAtUtc { get; init; }
}
