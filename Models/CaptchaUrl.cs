namespace QwenSolvingApi.Models;

public sealed class CaptchaUrl
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
