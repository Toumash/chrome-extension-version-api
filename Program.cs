using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("ChromeWebStore", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    client.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();

var cacheTtl = TimeSpan.FromMinutes(app.Configuration.GetValue("CacheTtlMinutes", 5));

app.MapGet("/check-published-extension-version/{extensionId}", async (
    string extensionId,
    IMemoryCache cache,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger) =>
{
    // Chrome extension IDs are 32 lowercase letters (a-p)
    if (!Regex.IsMatch(extensionId, "^[a-p]{32}$"))
    {
        return Results.BadRequest(new
        {
            error = "Invalid extension ID format. Must be 32 lowercase letters (a-p).",
            source = "chrome-extension-version-api"
        });
    }

    var cacheKey = $"cws-version-{extensionId}";

    if (cache.TryGetValue(cacheKey, out CachedVersion? cached))
    {
        logger.LogDebug("Cache hit for extension {ExtensionId}", extensionId);
        return Results.Ok(new
        {
            version = cached!.Version,
            cached = true,
            checkedAt = cached.CheckedAt
        });
    }

    try
    {
        var client = httpClientFactory.CreateClient("ChromeWebStore");
        var storeUrl = $"https://chromewebstore.google.com/detail/_/{extensionId}";

        logger.LogInformation("Fetching Chrome Web Store page for extension {ExtensionId}", extensionId);
        var html = await client.GetStringAsync(storeUrl);

        var version = ExtractVersion(html);

        if (string.IsNullOrEmpty(version))
        {
            logger.LogWarning("Could not extract version from Chrome Web Store page for extension {ExtensionId}",
                extensionId);
            return Results.NotFound(new
            {
                error = "Could not extract version from Chrome Web Store page.",
                source = "chrome-extension-version-api"
            });
        }

        var result = new CachedVersion(version, DateTimeOffset.UtcNow);
        cache.Set(cacheKey, result, cacheTtl);

        logger.LogInformation("Extension {ExtensionId} version: {Version}", extensionId, version);

        return Results.Ok(new
        {
            version,
            cached = false,
            checkedAt = result.CheckedAt
        });
    }
    catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests)
    {
        logger.LogError(ex, "Rate limited by Chrome Web Store for extension {ExtensionId}", extensionId);

        // Return 503 with a body that distinguishes from nginx 503
        return Results.Json(new
        {
            error = "Rate limited by Chrome Web Store. Please try again later.",
            source = "chrome-extension-version-api",
            retryAfterSeconds = 300
        }, statusCode: 503);
    }
    catch (HttpRequestException ex)
    {
        logger.LogError(ex, "Failed to fetch Chrome Web Store page for extension {ExtensionId}", extensionId);

        return Results.Json(new
        {
            error = "Failed to fetch Chrome Web Store page.",
            source = "chrome-extension-version-api",
            details = ex.Message
        }, statusCode: 503);
    }
    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
    {
        logger.LogError(ex, "Timeout fetching Chrome Web Store page for extension {ExtensionId}", extensionId);

        return Results.Json(new
        {
            error = "Chrome Web Store request timed out.",
            source = "chrome-extension-version-api"
        }, statusCode: 503);
    }
});

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

app.Run();

static string? ExtractVersion(string html)
{
    // Same logic as the GitHub Action: split on '>', find the first semver-like pattern
    var parts = html.Split('>');
    foreach (var part in parts)
    {
        var match = Regex.Match(part, @"^(\d+\.\d+\.\d+)");
        if (match.Success)
            return match.Groups[1].Value;
    }

    return null;
}

record CachedVersion(string Version, DateTimeOffset CheckedAt);
