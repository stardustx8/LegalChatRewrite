using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace LegalDocProcessor;

public class CleanupIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        WriteIndented = true
    };

    private readonly ILogger _logger;
    public CleanupIndex(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CleanupIndex>();
    }

    public class CleanupResponse
    {
        [JsonPropertyName("success")] public bool success { get; set; }
        [JsonPropertyName("message")] public string message { get; set; } = string.Empty;
        [JsonPropertyName("deleted_count")] public int deleted_count { get; set; }
        [JsonPropertyName("failed_count")] public int failed_count { get; set; }
        [JsonPropertyName("iso_code")] public string iso_code { get; set; } = string.Empty;
        [JsonPropertyName("warning")] public string? warning { get; set; }
    }

    [Function("CleanupIndex")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cleanup_index")] HttpRequestData req)
    {
        _logger.LogInformation("HTTP trigger function for index cleanup started");

        try
        {
            using var reader = new StreamReader(req.Body);
            var bodyText = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(bodyText))
            {
                return await WriteJson(req, HttpStatusCode.BadRequest, new { error = "Request body is required" });
            }
            var body = JsonSerializer.Deserialize<Dictionary<string, string>>(bodyText) ?? new();
            var isoCode = (body.TryGetValue("iso_code", out var v) ? v : string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(isoCode))
            {
                return await WriteJson(req, HttpStatusCode.BadRequest, new { error = "iso_code is required" });
            }

            _logger.LogInformation("Processing cleanup request for ISO code: {iso}", isoCode);

            var searchEndpoint = Environment.GetEnvironmentVariable("KNIFE_SEARCH_ENDPOINT");
            var searchKey = Environment.GetEnvironmentVariable("KNIFE_SEARCH_KEY");
            var indexName = Environment.GetEnvironmentVariable("KNIFE_SEARCH_INDEX");

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(searchEndpoint)) missing.Add("KNIFE_SEARCH_ENDPOINT");
            if (string.IsNullOrWhiteSpace(searchKey)) missing.Add("KNIFE_SEARCH_KEY");
            if (string.IsNullOrWhiteSpace(indexName)) missing.Add("KNIFE_SEARCH_INDEX");
            if (missing.Count > 0)
            {
                var error = $"Missing required environment variables: {string.Join(", ", missing)}";
                _logger.LogError(error);
                return await WriteJson(req, HttpStatusCode.InternalServerError, new { error });
            }

            var client = new SearchClient(new Uri(searchEndpoint!), indexName!, new AzureKeyCredential(searchKey!));

            List<Dictionary<string, string>> docsToDelete;
            string cleanupType;
            if (isoCode == "ALL")
            {
                _logger.LogInformation("Processing cleanup for ALL documents");
                var results = client.Search<Dictionary<string, object>>("*", new SearchOptions { Select = { "id", "iso_code" } });
                docsToDelete = results.GetResults().Select(r => new Dictionary<string, string> { ["id"] = r.Document["id"].ToString()! }).ToList();
                cleanupType = "all documents";
            }
            else
            {
                if (!Regex.IsMatch(isoCode, "^[A-Z]{2}$"))
                {
                    return await WriteJson(req, HttpStatusCode.BadRequest, new { error = "iso_code must be a 2-letter country code (e.g., 'FR', 'DE')" });
                }
                _logger.LogInformation("Searching for documents with iso_code: {iso}", isoCode);
                var options = new SearchOptions { Filter = $"iso_code eq '{isoCode}'" };
                options.Select.Add("id");
                var results = client.Search<Dictionary<string, object>>("*", options);
                docsToDelete = results.GetResults().Select(r => new Dictionary<string, string> { ["id"] = r.Document["id"].ToString()! }).ToList();
                cleanupType = $"documents for {isoCode}";
            }

            CleanupResponse resp;
            if (docsToDelete.Count > 0)
            {
                _logger.LogInformation("Found {count} documents to delete", docsToDelete.Count);
                var batchResult = await client.DeleteDocumentsAsync(docsToDelete);
                var succeeded = batchResult.Value.Results.Count(r => r.Succeeded);
                var failed = batchResult.Value.Results.Count - succeeded;

                resp = new CleanupResponse
                {
                    success = true,
                    message = $"Cleaned up {cleanupType}",
                    deleted_count = succeeded,
                    failed_count = failed,
                    iso_code = isoCode,
                    warning = failed > 0 ? "Some documents failed to delete" : null
                };
            }
            else
            {
                _logger.LogInformation("No documents found for cleanup: {type}", cleanupType);
                resp = new CleanupResponse
                {
                    success = true,
                    message = $"No {cleanupType} found to clean up",
                    deleted_count = 0,
                    failed_count = 0,
                    iso_code = isoCode
                };
            }

            return await WriteJson(req, HttpStatusCode.OK, resp);
        }
        catch (Exception ex)
        {
            var errorMsg = $"Error during index cleanup: {ex.Message}";
            _logger.LogError(ex, errorMsg);
            return await WriteJson(req, HttpStatusCode.InternalServerError, new { error = errorMsg });
        }
    }

    private static async Task<HttpResponseData> WriteJson(HttpRequestData req, HttpStatusCode code, object obj)
    {
        var res = req.CreateResponse(code);
        res.Headers.Add("Content-Type", "application/json");
        await res.WriteStringAsync(JsonSerializer.Serialize(obj, JsonOptions));
        return res;
    }
}
