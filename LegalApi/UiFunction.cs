using System.Net;
using System.IO;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace LegalApi;

public class UiFunction
{
    private readonly ILogger _logger;
    public UiFunction(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<UiFunction>();
    }

    // Redirect site root to /app/
    [Function("UiRoot")]
    public async Task<HttpResponseData> Root(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "")] HttpRequestData req)
    {
        var res = req.CreateResponse(HttpStatusCode.MovedPermanently);
        res.Headers.Add("Location", "/app/");
        return res;
    }

    // Serve SPA from /app and static assets under /app/*
    [Function("UiApp")]
    public async Task<HttpResponseData> App(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "app/{*rest}")] HttpRequestData req,
        string? rest)
    {
        var (filePath, contentType) = ResolveAppFile(rest);
        if (!File.Exists(filePath))
        {
            // SPA fallback to index.html
            (filePath, contentType) = ResolveAppFile("index.html");
            if (!File.Exists(filePath))
            {
                var nf = req.CreateResponse(HttpStatusCode.NotFound);
                await nf.WriteStringAsync("UI not deployed. Missing /wwwroot/app/index.html");
                return nf;
            }
        }

        var ok = req.CreateResponse(HttpStatusCode.OK);
        ok.Headers.Add("Content-Type", contentType);
        await using var fs = File.OpenRead(filePath);
        await fs.CopyToAsync(ok.Body);
        return ok;
    }

    private static (string fullPath, string contentType) ResolveAppFile(string? relative)
    {
        var rel = string.IsNullOrWhiteSpace(relative) ? "index.html" : relative.TrimStart('/');
        var root = GetWwwRoot();
        var appRoot = Path.GetFullPath(Path.Combine(root, "app"));
        var combined = Path.GetFullPath(Path.Combine(appRoot, rel));

        // Prevent directory traversal
        if (!combined.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
        {
            combined = Path.Combine(appRoot, "index.html");
        }

        var ext = Path.GetExtension(combined).ToLowerInvariant();
        var contentType = ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".map" => "application/octet-stream",
            ".ico" => "image/x-icon",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
        return (combined, contentType);
    }

    private static string GetWwwRoot()
    {
        // Try working directory
        var wd = Environment.CurrentDirectory;
        var p1 = Path.Combine(wd, "wwwroot");
        if (Directory.Exists(p1)) return p1;

        // Try base directory (for some hosting layouts)
        var bd = AppContext.BaseDirectory;
        var p2 = Path.Combine(bd, "wwwroot");
        if (Directory.Exists(p2)) return p2;

        // Try HOME/site/wwwroot (Azure Functions Windows)
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            var p3 = Path.Combine(home, "site", "wwwroot");
            if (Directory.Exists(p3)) return p3;
        }

        // Fallback
        return "wwwroot";
    }
}
