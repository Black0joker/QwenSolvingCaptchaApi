using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using QwenSolvingApi.Models;

namespace QwenSolvingApi.Services;

/// <summary>
/// Forwards the solve request to the upstream captcha solver API and streams
/// the SSE response chunks directly to the client without buffering.
/// </summary>
public sealed class CaptchaProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CaptchaProxyService> _logger;

    public CaptchaProxyService(IHttpClientFactory httpClientFactory, ILogger<CaptchaProxyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Streams the upstream SSE response directly to the client's HttpResponse.
    /// </summary>
    public async Task StreamSolveAsync(
        SolveRequest request,
        string upstreamUrl,
        HttpResponse clientResponse,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("upstream");

        var upstreamBody = new Dictionary<string, string?>
        {
            ["encrypted_token"] = request.EncryptedToken
        };

        if (!string.IsNullOrWhiteSpace(request.CaptchaUrl))
        {
            upstreamBody["captcha_url"] = request.CaptchaUrl;
        }

        var httpContent = JsonContent.Create(upstreamBody);

        _logger.LogInformation("Forwarding solve request to upstream: {Url}", upstreamUrl);

        // Use SendAsync with ResponseHeadersRead to enable streaming without buffering
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{upstreamUrl}/solve")
        {
            Content = httpContent
        };

        using var response = await client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Upstream returned {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Upstream returned {(int)response.StatusCode}: {errorBody}");
        }

        // Set SSE headers on client response
        clientResponse.ContentType = "text/event-stream";
        clientResponse.Headers.CacheControl = "no-cache";
        clientResponse.Headers.Connection = "keep-alive";
        clientResponse.Headers["X-Accel-Buffering"] = "no"; // Disable nginx buffering if behind proxy
        clientResponse.StatusCode = 200;

        // Stream upstream → client chunk by chunk
        using var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[4096];
        int bytesRead;

        while ((bytesRead = await upstreamStream.ReadAsync(buffer, ct)) > 0)
        {
            await clientResponse.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            await clientResponse.Body.FlushAsync(ct);
        }

        _logger.LogInformation("Stream completed successfully.");
    }
}
