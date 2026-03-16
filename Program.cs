using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("ChromeWebStore", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

app.UseCors();

var cacheTtl = TimeSpan.FromMinutes(app.Configuration.GetValue("CacheTtlMinutes", 5));

app.MapGet("/check-published-extension-version/{extensionId}", async (
    string extensionId,
    IMemoryCache cache,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger) =>
{
    if (!Patterns.ExtensionId().IsMatch(extensionId))
    {
        return Results.Json(
            new ErrorResponse("Invalid extension ID format. Must be 32 lowercase letters (a-p)."),
            AppJsonContext.Default.ErrorResponse,
            statusCode: 400);
    }

    var cacheKey = $"cws-version-{extensionId}";

    if (cache.TryGetValue(cacheKey, out CachedVersion? cached))
    {
        logger.LogDebug("Cache hit for extension {ExtensionId}", extensionId);
        return Results.Json(
            new VersionResponse(cached!.Version, true, cached.CheckedAt),
            AppJsonContext.Default.VersionResponse);
    }

    try
    {
        var client = httpClientFactory.CreateClient("ChromeWebStore");
        var storeUrl = $"https://chromewebstore.google.com/detail/_/{extensionId}";

        logger.LogInformation("Fetching Chrome Web Store page for extension {ExtensionId}", extensionId);
        var html = await client.GetStringAsync(storeUrl);

        var version = VersionExtractor.Extract(html);

        if (string.IsNullOrEmpty(version))
        {
            logger.LogWarning("Could not extract version from Chrome Web Store page for extension {ExtensionId}",
                extensionId);
            return Results.Json(
                new ErrorResponse("Could not extract version from Chrome Web Store page."),
                AppJsonContext.Default.ErrorResponse,
                statusCode: 404);
        }

        var result = new CachedVersion(version, DateTimeOffset.UtcNow);
        cache.Set(cacheKey, result, cacheTtl);

        logger.LogInformation("Extension {ExtensionId} version: {Version}", extensionId, version);

        return Results.Json(
            new VersionResponse(version, false, result.CheckedAt),
            AppJsonContext.Default.VersionResponse);
    }
    catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests)
    {
        logger.LogError(ex, "Rate limited by Chrome Web Store for extension {ExtensionId}", extensionId);

        return Results.Json(
            new ErrorResponse("Rate limited by Chrome Web Store. Please try again later.", RetryAfterSeconds: 300),
            AppJsonContext.Default.ErrorResponse,
            statusCode: 503);
    }
    catch (HttpRequestException ex)
    {
        logger.LogError(ex, "Failed to fetch Chrome Web Store page for extension {ExtensionId}", extensionId);

        return Results.Json(
            new ErrorResponse("Failed to fetch Chrome Web Store page.", Details: ex.Message),
            AppJsonContext.Default.ErrorResponse,
            statusCode: 503);
    }
    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
    {
        logger.LogError(ex, "Timeout fetching Chrome Web Store page for extension {ExtensionId}", extensionId);

        return Results.Json(
            new ErrorResponse("Chrome Web Store request timed out."),
            AppJsonContext.Default.ErrorResponse,
            statusCode: 503);
    }
});

app.MapGet("/healthz", () => Results.Json(
    new HealthResponse("healthy"),
    AppJsonContext.Default.HealthResponse));

app.Run();

// --- Source-generated Regex (must be in a partial class, not top-level) ---

internal static partial class Patterns
{
    [GeneratedRegex(@"^[a-p]{32}$")]
    public static partial Regex ExtensionId();

    [GeneratedRegex(@"^(\d+\.\d+\.\d+)")]
    public static partial Regex Version();
}

internal static class VersionExtractor
{
    public static string? Extract(string html)
    {
        var parts = html.Split('>');
        foreach (var part in parts)
        {
            var match = Patterns.Version().Match(part);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }
}

// --- Models ---

record CachedVersion(string Version, DateTimeOffset CheckedAt);

record VersionResponse(string Version, bool Cached, DateTimeOffset CheckedAt);

record ErrorResponse(
    string Error,
    string Source = "chrome-extension-version-api",
    string? Details = null,
    int? RetryAfterSeconds = null);

record HealthResponse(string Status);

// --- AOT JSON serialization ---

[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal partial class AppJsonContext : JsonSerializerContext;
