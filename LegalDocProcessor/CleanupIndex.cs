using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
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
        [JsonPropertyName("blobs_deleted")] public int blobs_deleted { get; set; }
        [JsonPropertyName("receipts_deleted")] public int receipts_deleted { get; set; }
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
            var storageConn = Environment.GetEnvironmentVariable("LEGAL_STORAGE_CONNECTION");

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(searchEndpoint)) missing.Add("KNIFE_SEARCH_ENDPOINT");
            if (string.IsNullOrWhiteSpace(searchKey)) missing.Add("KNIFE_SEARCH_KEY");
            if (string.IsNullOrWhiteSpace(indexName)) missing.Add("KNIFE_SEARCH_INDEX");
            if (string.IsNullOrWhiteSpace(storageConn)) missing.Add("LEGAL_STORAGE_CONNECTION");
            if (missing.Count > 0)
            {
                var error = $"Missing required environment variables: {string.Join(", ", missing)}";
                _logger.LogError(error);
                return await WriteJson(req, HttpStatusCode.InternalServerError, new { error });
            }

            var client = new SearchClient(new Uri(searchEndpoint!), indexName!, new AzureKeyCredential(searchKey!));
            var blobServiceClient = new BlobServiceClient(storageConn);
            var blobContainer = blobServiceClient.GetBlobContainerClient("legaldocsrag");
            var receiptContainer = blobServiceClient.GetBlobContainerClient("blob-receipts");

            List<Dictionary<string, string>> docsToDelete;
            string cleanupType;
            List<string> isoCodesToClean = new();
            
            if (isoCode == "ALL")
            {
                _logger.LogInformation("Processing cleanup for ALL documents");
                var response = client.Search<Dictionary<string, object>>("*", new SearchOptions { Select = { "id", "iso_code" } });
                var results = response.Value;
                var searchResults = results.GetResults().ToList();
                docsToDelete = searchResults.Select(r => new Dictionary<string, string> { ["id"] = r.Document["id"].ToString()! }).ToList();
                // Collect all unique ISO codes for blob deletion
                isoCodesToClean = searchResults.Select(r => r.Document["iso_code"].ToString()!).Distinct().ToList();
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
                var response = client.Search<Dictionary<string, object>>("*", options);
                var results = response.Value;
                docsToDelete = results.GetResults().Select(r => new Dictionary<string, string> { ["id"] = r.Document["id"].ToString()! }).ToList();
                isoCodesToClean.Add(isoCode);
                cleanupType = $"documents for {isoCode}";
            }

            CleanupResponse resp;
            int blobsDeleted = 0;
            int receiptsDeleted = 0;
            
            // Delete blobs and receipts for affected ISO codes
            foreach (var iso in isoCodesToClean)
            {
                // Delete the main blob (XX.docx)
                var blobName = $"{iso}.docx";
                try
                {
                    var blobClient = blobContainer.GetBlobClient(blobName);
                    var deleteResponse = await blobClient.DeleteIfExistsAsync();
                    if (deleteResponse.Value)
                    {
                        blobsDeleted++;
                        _logger.LogInformation("Deleted blob: {blob}", blobName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete blob: {blob}", blobName);
                }
                
                // Delete the receipt blob (XX.docx.receipt)
                var receiptName = $"{iso}.docx.receipt";
                try
                {
                    var receiptClient = receiptContainer.GetBlobClient(receiptName);
                    var deleteResponse = await receiptClient.DeleteIfExistsAsync();
                    if (deleteResponse.Value)
                    {
                        receiptsDeleted++;
                        _logger.LogInformation("Deleted receipt: {receipt}", receiptName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete receipt: {receipt}", receiptName);
                }
            }
            
            if (docsToDelete.Count > 0)
            {
                _logger.LogInformation("Found {count} documents to delete from index", docsToDelete.Count);
                var batchResult = await client.DeleteDocumentsAsync(docsToDelete);
                var succeeded = batchResult.Value.Results.Count(r => r.Succeeded);
                var failed = batchResult.Value.Results.Count - succeeded;

                resp = new CleanupResponse
                {
                    success = true,
                    message = $"Cleaned up {cleanupType}, deleted {blobsDeleted} blob(s) and {receiptsDeleted} receipt(s)",
                    deleted_count = succeeded,
                    failed_count = failed,
                    iso_code = isoCode,
                    blobs_deleted = blobsDeleted,
                    receipts_deleted = receiptsDeleted,
                    warning = failed > 0 ? "Some documents failed to delete from index" : null
                };
            }
            else
            {
                _logger.LogInformation("No documents found for cleanup: {type}", cleanupType);
                resp = new CleanupResponse
                {
                    success = true,
                    message = $"No {cleanupType} found in index, but deleted {blobsDeleted} blob(s) and {receiptsDeleted} receipt(s)",
                    deleted_count = 0,
                    failed_count = 0,
                    iso_code = isoCode,
                    blobs_deleted = blobsDeleted,
                    receipts_deleted = receiptsDeleted
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
