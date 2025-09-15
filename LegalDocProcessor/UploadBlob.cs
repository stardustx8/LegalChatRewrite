using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace LegalDocProcessor;

public class UploadBlob
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        WriteIndented = true
    };

    private readonly ILogger _logger;
    public UploadBlob(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<UploadBlob>();
    }

    [Function("UploadBlob")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "upload_blob")] HttpRequestData req)
    {
        _logger.LogInformation("Upload blob function processed a request.");
        try
        {
            string bodyText;
            using (var reader = new StreamReader(req.Body))
            {
                bodyText = await reader.ReadToEndAsync();
            }
            if (string.IsNullOrWhiteSpace(bodyText))
            {
                return await WriteJson(req, HttpStatusCode.BadRequest, new { error = "Request body is required" });
            }

            var body = JsonSerializer.Deserialize<Dictionary<string, object>>(bodyText) ?? new();

            // Passcode gate removed (no authentication required here)

            // Extract fields
            var filename = body.TryGetValue("filename", out var fnEl) ? fnEl?.ToString() : null;
            var fileData = body.TryGetValue("file_data", out var fdEl) ? fdEl?.ToString() : null;
            var container = body.TryGetValue("container", out var cEl) ? (cEl?.ToString() ?? "legaldocsrag") : "legaldocsrag";

            if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(fileData))
            {
                return await WriteJson(req, HttpStatusCode.BadRequest, new { success = false, message = "Missing filename or file_data" });
            }

            // Validate filename: exactly 'XX.docx'
            if (!(filename!.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) && filename.Length == 7 && filename[..2] == filename[..2].ToUpperInvariant()))
            {
                return await WriteJson(req, HttpStatusCode.BadRequest, new { success = false, message = $"Invalid filename format. Expected: XX.docx (e.g., DE.docx), got: {filename}" });
            }

            // Storage connection (support multiple variable names)
            var conn = Environment.GetEnvironmentVariable("LEGAL_STORAGE_CONNECTION")
                      ?? Environment.GetEnvironmentVariable("KNIFE_STORAGE_CONNECTION")
                      ?? Environment.GetEnvironmentVariable("KNIFE_STORAGE_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(conn))
            {
                return await WriteJson(req, HttpStatusCode.InternalServerError, new { success = false, message = "Storage connection string not configured" });
            }

            // Decode base64
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(fileData!);
            }
            catch (Exception e)
            {
                return await WriteJson(req, HttpStatusCode.BadRequest, new { success = false, message = $"Invalid base64 file data: {e.Message}" });
            }

            var blobService = new BlobServiceClient(conn);
            // Ensure container exists
            var containerClient = blobService.GetBlobContainerClient(container);
            await containerClient.CreateIfNotExistsAsync();
            var blobClient = containerClient.GetBlobClient(filename);
            using (var ms = new MemoryStream(bytes))
            {
                await blobClient.UploadAsync(ms, overwrite: true);
            }

            _logger.LogInformation("Successfully uploaded {file} to container {container}", filename, container);
            var iso = filename[..2];

            return await WriteJson(req, HttpStatusCode.OK, new { message = $"File {filename} uploaded successfully", iso_code = iso });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading blob: {err}", ex.Message);
            return await WriteJson(req, HttpStatusCode.InternalServerError, new { success = false, message = $"Upload failed: {ex.Message}" });
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
