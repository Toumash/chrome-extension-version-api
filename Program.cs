using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;

[assembly: InternalsVisibleTo("ChromeExtensionVersionApi.TestRunner")]

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
builder.Services.AddHttpClient("ChromeUpdateService", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/xml");
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
        var client = httpClientFactory.CreateClient("ChromeUpdateService");
        var updateUrl = ChromeUpdateService.BuildUpdateCheckUrl(extensionId);

        logger.LogInformation("Fetching Chrome update service response for extension {ExtensionId}", extensionId);
        var response = await client.GetStringAsync(updateUrl);

        var version = VersionExtractor.Extract(response, extensionId);

        if (string.IsNullOrEmpty(version))
        {
            logger.LogWarning("Could not extract version from Chrome update service response for extension {ExtensionId}",
                extensionId);
            return Results.Json(
                new ErrorResponse("Could not extract version from Chrome update service response."),
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
        logger.LogError(ex, "Rate limited by Chrome update service for extension {ExtensionId}", extensionId);

        return Results.Json(
            new ErrorResponse("Rate limited by Chrome update service. Please try again later.", RetryAfterSeconds: 300),
            AppJsonContext.Default.ErrorResponse,
            statusCode: 503);
    }
    catch (HttpRequestException ex)
    {
        logger.LogError(ex, "Failed to fetch Chrome update service response for extension {ExtensionId}", extensionId);

        return Results.Json(
            new ErrorResponse("Failed to fetch Chrome update service response.", Details: ex.Message),
            AppJsonContext.Default.ErrorResponse,
            statusCode: 503);
    }
    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
    {
        logger.LogError(ex, "Timeout fetching Chrome update service response for extension {ExtensionId}", extensionId);

        return Results.Json(
            new ErrorResponse("Chrome update service request timed out."),
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

    [GeneratedRegex(@"^\d+(?:\.\d+){0,3}$")]
    public static partial Regex ChromeVersion();
}

internal static class ChromeUpdateService
{
    private const string ProductVersion = "131.0.0.0";

    public static string BuildUpdateCheckUrl(string extensionId)
    {
        var updateCheck = Uri.EscapeDataString($"id={extensionId}&installsource=ondemand&uc");
        return "https://clients2.google.com/service/update2/crx" +
            $"?response=updatecheck&prodversion={ProductVersion}&acceptformat=crx2,crx3&x={updateCheck}";
    }
}

internal static class VersionExtractor
{
    private static readonly XNamespace UpdateServiceNamespace = "http://www.google.com/update2/response";

    public static string? Extract(string response, string extensionId)
    {
        try
        {
            var document = XDocument.Parse(response);
            var app = document.Descendants(UpdateServiceNamespace + "app")
                .FirstOrDefault(element =>
                    string.Equals((string?)element.Attribute("appid"), extensionId, StringComparison.Ordinal));

            if (app is null)
                return null;

            var updateCheck = app.Element(UpdateServiceNamespace + "updatecheck");
            if (updateCheck is null ||
                !string.Equals((string?)updateCheck.Attribute("status"), "ok", StringComparison.OrdinalIgnoreCase))
                return null;

            return NormalizeVersion((string?)updateCheck.Attribute("version"));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? NormalizeVersion(string? version)
    {
        version = version?.Trim();
        return !string.IsNullOrEmpty(version) && Patterns.ChromeVersion().IsMatch(version) ? version : null;
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
