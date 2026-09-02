using System.Text.Json.Serialization;

namespace QwenSolvingApi.Models;

public sealed class SolveRequest
{
    [JsonPropertyName("encrypted_token")]
    public string EncryptedToken { get; set; } = string.Empty;

    [JsonPropertyName("captcha_url")]
    public string? CaptchaUrl { get; set; }
}
