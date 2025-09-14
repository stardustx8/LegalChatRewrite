using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace LegalDocProcessor;

public class DiagnosticFunction
{
    private readonly ILogger _logger;
    public DiagnosticFunction(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<DiagnosticFunction>();
    }

    [Function("Diagnostic")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "diagnostic")] HttpRequestData req)
    {
        _logger.LogInformation("Diagnostic function triggered");
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        
        var result = new Dictionary<string, object>();
        
        // Check environment variables
        var envCheck = new Dictionary<string, bool>();
        envCheck["KNIFE_STORAGE_CONNECTION_STRING"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KNIFE_STORAGE_CONNECTION_STRING"));
        envCheck["AzureWebJobsStorage"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));
        envCheck["KNIFE_SEARCH_ENDPOINT"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KNIFE_SEARCH_ENDPOINT"));
        envCheck["KNIFE_SEARCH_KEY"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KNIFE_SEARCH_KEY"));
        envCheck["KNIFE_SEARCH_INDEX"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KNIFE_SEARCH_INDEX"));
        envCheck["KNIFE_OPENAI_ENDPOINT"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KNIFE_OPENAI_ENDPOINT"));
        envCheck["KNIFE_OPENAI_KEY"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KNIFE_OPENAI_KEY"));
        envCheck["OPENAI_EMBED_DEPLOY"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_EMBED_DEPLOY"));
        result["environment_variables"] = envCheck;
        
        // Check storage connectivity
        var storageCheck = new Dictionary<string, object>();
        try
        {
            var conn = Environment.GetEnvironmentVariable("KNIFE_STORAGE_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(conn))
            {
                var blobService = new BlobServiceClient(conn);
                var containerClient = blobService.GetBlobContainerClient("legaldocsrag");
                
                // Check if container exists
                var exists = await containerClient.ExistsAsync();
                storageCheck["container_exists"] = exists.Value;
                
                // List blobs
                var blobs = new List<string>();
                await foreach (var blobItem in containerClient.GetBlobsAsync())
                {
                    blobs.Add(blobItem.Name);
                    if (blobs.Count >= 10) break; // Limit to 10
                }
                storageCheck["blob_count"] = blobs.Count;
                storageCheck["recent_blobs"] = blobs;
                storageCheck["status"] = "connected";
            }
            else
            {
                storageCheck["status"] = "missing_connection_string";
            }
        }
        catch (Exception ex)
        {
            storageCheck["status"] = "error";
            storageCheck["error"] = ex.Message;
        }
        result["storage"] = storageCheck;
        
        // Check search connectivity
        var searchCheck = new Dictionary<string, object>();
        try
        {
            var searchEndpoint = Environment.GetEnvironmentVariable("KNIFE_SEARCH_ENDPOINT");
            var searchKey = Environment.GetEnvironmentVariable("KNIFE_SEARCH_KEY");
            var indexName = Environment.GetEnvironmentVariable("KNIFE_SEARCH_INDEX");
            
            if (!string.IsNullOrWhiteSpace(searchEndpoint) && !string.IsNullOrWhiteSpace(searchKey) && !string.IsNullOrWhiteSpace(indexName))
            {
                var searchClient = new SearchClient(new Uri(searchEndpoint), indexName, new AzureKeyCredential(searchKey));
                
                // Try to count documents
                var response2 = searchClient.Search<Dictionary<string, object>>("*", new SearchOptions 
                { 
                    IncludeTotalCount = true,
                    Size = 0
                });
                
                searchCheck["index_name"] = indexName;
                searchCheck["document_count"] = response2.Value.TotalCount ?? 0;
                searchCheck["status"] = "connected";
                
                // Check for specific ISOs
                var isos = new[] { "CH", "AT", "DE" };
                var isoCounts = new Dictionary<string, long>();
                foreach (var iso in isos)
                {
                    var isoResponse = searchClient.Search<Dictionary<string, object>>("*", new SearchOptions 
                    { 
                        Filter = $"iso_code eq '{iso}'",
                        IncludeTotalCount = true,
                        Size = 0
                    });
                    isoCounts[iso] = isoResponse.Value.TotalCount ?? 0;
                }
                searchCheck["iso_counts"] = isoCounts;
            }
            else
            {
                searchCheck["status"] = "missing_configuration";
            }
        }
        catch (Exception ex)
        {
            searchCheck["status"] = "error";
            searchCheck["error"] = ex.Message;
        }
        result["search"] = searchCheck;
        
        // Check OpenAI connectivity
        var openaiCheck = new Dictionary<string, object>();
        try
        {
            var openaiEndpoint = Environment.GetEnvironmentVariable("KNIFE_OPENAI_ENDPOINT");
            var openaiKey = Environment.GetEnvironmentVariable("KNIFE_OPENAI_KEY");
            var embedDeploy = Environment.GetEnvironmentVariable("OPENAI_EMBED_DEPLOY");
            
            openaiCheck["endpoint_set"] = !string.IsNullOrWhiteSpace(openaiEndpoint);
            openaiCheck["key_set"] = !string.IsNullOrWhiteSpace(openaiKey);
            openaiCheck["embed_deploy"] = embedDeploy ?? "not_set";
            
            if (!string.IsNullOrWhiteSpace(openaiEndpoint) && !string.IsNullOrWhiteSpace(openaiKey) && !string.IsNullOrWhiteSpace(embedDeploy))
            {
                // Normalize endpoint
                var uri = new Uri(openaiEndpoint);
                var baseUrl = $"{uri.Scheme}://{uri.Host}";
                var embedApiVersion = Environment.GetEnvironmentVariable("OPENAI_EMBED_API_VERSION") ?? "2023-05-15";
                var embedUrl = $"{baseUrl}/openai/deployments/{embedDeploy}/embeddings?api-version={embedApiVersion}";
                
                // Test embedding
                using var http = new HttpClient();
                var payload = new { input = "test" };
                using var req2 = new HttpRequestMessage(HttpMethod.Post, embedUrl);
                req2.Headers.Add("api-key", openaiKey);
                req2.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                
                using var res = await http.SendAsync(req2);
                openaiCheck["embed_status"] = (int)res.StatusCode;
                openaiCheck["embed_test"] = res.IsSuccessStatusCode ? "success" : "failed";
            }
            else
            {
                openaiCheck["status"] = "missing_configuration";
            }
        }
        catch (Exception ex)
        {
            openaiCheck["status"] = "error";
            openaiCheck["error"] = ex.Message;
        }
        result["openai"] = openaiCheck;
        
        // Runtime info
        result["runtime"] = new Dictionary<string, object>
        {
            ["function_app_name"] = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "unknown",
            ["runtime_version"] = Environment.GetEnvironmentVariable("FUNCTIONS_WORKER_RUNTIME_VERSION") ?? "unknown",
            ["instance_id"] = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "unknown"
        };
        
        await response.WriteStringAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return response;
    }
}
