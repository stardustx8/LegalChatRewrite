using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LegalDocProcessor;

public class ProcessDocument
{
    // Static HttpClient for outbound calls (OpenAI + Search REST if used)
    private static readonly SocketsHttpHandler Handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 50
    };
    private static readonly HttpClient Http = new(Handler, disposeHandler: false);

    private static async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> action, int retries = 2)
    {
        // Total tries = 1 + retries; initial 0.4s, factor 2.0, jitter up to 200ms, max 5s
        var delayMs = 400.0;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource();
                return await action(cts.Token);
            }
            catch when (attempt < retries)
            {
                var jitter = Random.Shared.Next(0, 200);
                var sleep = Math.Min(delayMs, 5000) + jitter;
                await Task.Delay(TimeSpan.FromMilliseconds(sleep));
                delayMs *= 2.0;
            }
        }
    }

    private readonly ILogger _logger;
    public ProcessDocument(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ProcessDocument>();
    }

    [Function("ProcessDocument")]
    public async Task Run([
        BlobTrigger("legaldocsrag/{name}", Connection = "KNIFE_STORAGE_CONNECTION_STRING")
    ] Stream myBlob, string name)
    {
        _logger.LogInformation("Python blob trigger function processed blob");
        _logger.LogInformation($"Name: {name}");
        _logger.LogInformation($"Size: {myBlob.Length} Bytes");

        // Validate filename pattern XX.docx
        var match = Regex.Match(name, "^([A-Z]{2})\\.docx$");
        if (!match.Success)
        {
            _logger.LogError($"Invalid filename format: {name}. Expected 'XX.docx' where XX is a 2-letter ISO code.");
            return;
        }
        var isoCode = match.Groups[1].Value;
        _logger.LogInformation($"Processing document for ISO code: {isoCode}");

        // Caption/OCR enabled when chat deploy set
        var enableCaptioning = true;
        _logger.LogInformation("Caption/OCR is enabled by default");

        // Env
        var searchEndpoint = Environment.GetEnvironmentVariable("KNIFE_SEARCH_ENDPOINT");
        var searchKey = Environment.GetEnvironmentVariable("KNIFE_SEARCH_KEY");
        var indexName = Environment.GetEnvironmentVariable("KNIFE_SEARCH_INDEX");
        var openaiEndpoint = Environment.GetEnvironmentVariable("KNIFE_OPENAI_ENDPOINT");
        var openaiKey = Environment.GetEnvironmentVariable("KNIFE_OPENAI_KEY");
        var embedDeploy = Environment.GetEnvironmentVariable("OPENAI_EMBED_DEPLOY") ?? Environment.GetEnvironmentVariable("KNIFE_OPENAI_DEPLOY");
        var chatDeploy = Environment.GetEnvironmentVariable("OPENAI_CHAT_DEPLOY");
        var storageConn = Environment.GetEnvironmentVariable("KNIFE_STORAGE_CONNECTION_STRING");

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(searchEndpoint)) missing.Add("KNIFE_SEARCH_ENDPOINT");
        if (string.IsNullOrWhiteSpace(searchKey)) missing.Add("KNIFE_SEARCH_KEY");
        if (string.IsNullOrWhiteSpace(indexName)) missing.Add("KNIFE_SEARCH_INDEX");
        if (string.IsNullOrWhiteSpace(openaiEndpoint)) missing.Add("KNIFE_OPENAI_ENDPOINT");
        if (string.IsNullOrWhiteSpace(openaiKey)) missing.Add("KNIFE_OPENAI_KEY");
        if (string.IsNullOrWhiteSpace(embedDeploy)) missing.Add("OPENAI_EMBED_DEPLOY");
        if (missing.Count > 0)
        {
            _logger.LogError($"Missing required environment variables: {string.Join(", ", missing)}");
            return;
        }

        // Normalize OpenAI endpoint (scheme+host only)
        var openaiBase = NormalizeEndpoint(openaiEndpoint!);
        var embedApiVersion = Environment.GetEnvironmentVariable("OPENAI_API_VERSION")
            ?? Environment.GetEnvironmentVariable("OPENAI_EMBED_API_VERSION")
            ?? "2023-05-15";
        var chatApiVersion = Environment.GetEnvironmentVariable("OPENAI_API_VERSION")
            ?? Environment.GetEnvironmentVariable("OPENAI_CHAT_API_VERSION")
            ?? "2024-02-15-preview";

        var openaiEmbedUrl = $"{openaiBase}/openai/deployments/{embedDeploy}/embeddings?api-version={embedApiVersion}";
        var openaiChatUrl = string.IsNullOrWhiteSpace(chatDeploy) ? null : $"{openaiBase}/openai/deployments/{chatDeploy}/chat/completions?api-version={chatApiVersion}";

        var searchClient = new SearchClient(new Uri(searchEndpoint!), indexName!, new AzureKeyCredential(searchKey!));

        try
        {
            // Read all bytes of blob
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await myBlob.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            // Extract elements
            var elements = ExtractElements(bytes);
            if (elements.Count == 0)
            {
                _logger.LogWarning($"No content extracted from {name}");
                return;
            }

            // Separate images
            var imageElements = elements.Where(e => e.Type == ElementType.Image).ToList();
            var contentElements = elements.Where(e => e.Type != ElementType.Image).ToList();

            // Vision captions + OCR
            if (!string.IsNullOrWhiteSpace(openaiChatUrl) && imageElements.Count > 0)
            {
                _logger.LogInformation($"Processing {imageElements.Count} images");
                var enriched = await GenerateCaptionsAsync(imageElements, openaiChatUrl!, openaiKey!);

                // Upload images
                var blobService = new BlobServiceClient(storageConn);
                foreach (var img in enriched)
                {
                    var blobName = $"images/{isoCode}/{img.FileName}";
                    var blobClient = blobService.GetBlobContainerClient("legaldocsrag").GetBlobClient(blobName);
                    using var ims = new MemoryStream(img.ImageBytes);
                    await blobClient.UploadAsync(ims, overwrite: true);
                    img.BlobUrl = blobClient.Uri.ToString();
                }

                // Update elements content and metadata
                foreach (var elem in imageElements)
                {
                    var upd = enriched.FirstOrDefault(i => i.FigureId == elem.Metadata.GetValueOrDefault("figure_id"));
                    if (upd != null)
                    {
                        var caption = upd.Caption ?? string.Empty;
                        var ocr = upd.OcrText ?? string.Empty;
                        var content = !string.IsNullOrWhiteSpace(caption) && !string.IsNullOrWhiteSpace(ocr)
                            ? $"{caption}\n\n{ocr}"
                            : (!string.IsNullOrWhiteSpace(caption) ? caption : ocr);
                        if (!string.IsNullOrWhiteSpace(content)) elem.Content = content;
                        elem.Metadata["ocr_text"] = upd.OcrText ?? string.Empty;
                        elem.Metadata["blob_url"] = upd.BlobUrl ?? string.Empty;
                    }
                }
            }

            // Chunking
            var chunks = new List<Chunk>();
            var buffer = new StringBuilder();
            foreach (var e in contentElements)
            {
                if (e.Type == ElementType.Table)
                {
                    if (buffer.Length > 0)
                    {
                        chunks.Add(new Chunk { Text = buffer.ToString(), ChunkType = "text" });
                        buffer.Clear();
                    }
                    chunks.Add(new Chunk
                    {
                        Text = e.Content,
                        ChunkType = "table",
                        TableMarkdown = e.Content
                    });
                }
                else
                {
                    if (buffer.Length + e.Content.Length > 2000 && buffer.Length > 0)
                    {
                        chunks.Add(new Chunk { Text = buffer.ToString(), ChunkType = "text" });
                        buffer.Clear();
                    }
                    if (!string.IsNullOrWhiteSpace(e.Content))
                    {
                        if (buffer.Length > 0) buffer.Append("\n\n");
                        buffer.Append(e.Content);
                    }
                }
            }
            if (buffer.Length > 0)
            {
                chunks.Add(new Chunk { Text = buffer.ToString(), ChunkType = "text" });
            }
            // Add image chunks
            foreach (var e in imageElements)
            {
                chunks.Add(new Chunk
                {
                    Text = e.Content,
                    ChunkType = "image",
                    ImageFigureId = e.Metadata.GetValueOrDefault("figure_id"),
                    ImageOcrText = e.Metadata.GetValueOrDefault("ocr_text"),
                    ImageBlobUrl = e.Metadata.GetValueOrDefault("blob_url")
                });
            }

            // Embeddings
            var embeddings = new List<float[]>();
            foreach (var ch in chunks)
            {
                try
                {
                    var vec = await CreateEmbeddingAsync(openaiEmbedUrl, openaiKey!, ch.Text);
                    embeddings.Add(vec);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Embedding failed; using zero-vector fallback");
                    embeddings.Add(new float[3072]);
                }
            }

            // Delete existing docs for iso
            _logger.LogInformation($"Deleting existing documents for ISO code: {isoCode}");
            var response = searchClient.Search<Dictionary<string, object>>("*", new SearchOptions
            {
                Filter = $"iso_code eq '{isoCode}'",
                Select = { "id" }
            });
            var results = response.Value;
            var toDelete = results.GetResults().Select(r => new Dictionary<string, string> { ["id"] = r.Document["id"].ToString()! }).ToList();
            if (toDelete.Count > 0)
            {
                _logger.LogInformation($"Deleting {toDelete.Count} existing documents");
                await searchClient.DeleteDocumentsAsync(toDelete);
            }

            // Upload
            var documents = new List<Dictionary<string, object>>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var ch = chunks[i];
                var doc = new Dictionary<string, object>
                {
                    ["id"] = $"{isoCode}_{i}",
                    ["iso_code"] = isoCode,
                    ["chunk"] = ch.Text,
                    ["embedding"] = embeddings[i].Select(f => (float)f).ToArray(),
                    ["chunk_type"] = ch.ChunkType
                };
                if (ch.ChunkType == "table")
                {
                    doc["table_md"] = ch.TableMarkdown ?? string.Empty;
                }
                documents.Add(doc);
            }

            if (documents.Count > 0)
            {
                _logger.LogInformation($"Uploading {documents.Count} new documents to the search index");
                var upload = await searchClient.UploadDocumentsAsync(documents);
                var succeeded = upload.Value.Results.Count(r => r.Succeeded);
                _logger.LogInformation($"Successfully uploaded {succeeded}/{documents.Count} documents");
                if (succeeded < documents.Count)
                {
                    var failed = upload.Value.Results.Where(r => !r.Succeeded).ToList();
                    _logger.LogError($"Failed uploads: {failed.Count}");
                }
            }

            _logger.LogInformation($"Document processing completed for {name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing document: {err}", ex.Message);
            // Rethrow to allow Azure Functions retry policy to reprocess the blob, matching Python behavior
            throw;
        }
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        try
        {
            var uri = new Uri(endpoint);
            var builder = new UriBuilder(uri.Scheme, uri.Host);
            return builder.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return endpoint;
        }
    }

    private enum ElementType { Text, Table, Image }

    private class Element
    {
        public ElementType Type { get; set; }
        public string Content { get; set; } = string.Empty;
        public Dictionary<string, string> Metadata { get; set; } = new();
        public byte[]? ImageBytes { get; set; }
        public string? ContentType { get; set; }
    }

    private class ImageInfo
    {
        public string FigureId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = "image/jpeg";
        public string? Caption { get; set; }
        public string? OcrText { get; set; }
        public string? BlobUrl { get; set; }
    }

    private class Chunk
    {
        public string Text { get; set; } = string.Empty;
        public string ChunkType { get; set; } = "text"; // text|table|image
        public string? TableMarkdown { get; set; }
        public string? ImageFigureId { get; set; }
        public string? ImageOcrText { get; set; }
        public string? ImageBlobUrl { get; set; }
    }

    private static List<Element> ExtractElements(byte[] bytes)
    {
        var list = new List<Element>();
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        var main = doc.MainDocumentPart!;
        var body = main.Document.Body!;

        foreach (var node in body.ChildElements)
        {
            if (node is Paragraph p)
            {
                var text = p.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(new Element { Type = ElementType.Text, Content = text });
                }
            }
            else if (node is Table t)
            {
                var md = TableToMarkdown(t, out var headers, out var jsonRows);
                if (!string.IsNullOrWhiteSpace(md))
                {
                    var id = CreateMd5Id(string.Join(',', headers) + string.Join('|', jsonRows.SelectMany(r => r.Values)));
                    list.Add(new Element
                    {
                        Type = ElementType.Table,
                        Content = md,
                        Metadata = new Dictionary<string, string>
                        {
                            ["table_id"] = id
                        }
                    });
                }
            }
        }

        // Images
        int imgCounter = 0;
        foreach (var imgPart in main.ImageParts)
        {
            using var s = imgPart.GetStream();
            using var ims = new MemoryStream();
            s.CopyTo(ims);
            var data = ims.ToArray();
            var hash = MD5.HashData(data);
            var hashStr = Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 8);
            var figId = $"figure_{hashStr}";
            var ext = imgPart.ContentType.Split('/').Last();
            var file = $"image_{hashStr}.{ext}";

            list.Add(new Element
            {
                Type = ElementType.Image,
                Content = $"Image: {file}",
                Metadata = new Dictionary<string, string>
                {
                    ["figure_id"] = figId,
                    ["filename"] = file,
                    ["ocr_text"] = string.Empty
                },
                ImageBytes = data,
                ContentType = imgPart.ContentType
            });
            imgCounter++;
        }

        return list;
    }

    private static string CreateMd5Id(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = MD5.HashData(bytes);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant().Substring(0, 8);
    }

    private static string TableToMarkdown(Table table, out List<string> headers, out List<Dictionary<string, string>> rows)
    {
        headers = new List<string>();
        rows = new List<Dictionary<string, string>>();
        try
        {
            var trList = table.Elements<TableRow>().ToList();
            if (trList.Count == 0) return string.Empty;

            var first = trList[0];
            headers = first.Elements<TableCell>().Select(c => (c.InnerText ?? string.Empty).Trim()).ToList();
            var sb = new StringBuilder();
            if (headers.Count > 0)
            {
                sb.AppendLine("| " + string.Join(" | ", headers) + " |");
                sb.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", headers.Count)) + " |");
            }

            for (int ri = 1; ri < trList.Count; ri++)
            {
                var row = trList[ri];
                var cells = row.Elements<TableCell>().Select(c => (c.InnerText ?? string.Empty).Trim()).ToList();
                while (cells.Count < headers.Count) cells.Add(string.Empty);
                if (cells.Count > headers.Count) cells = cells.Take(headers.Count).ToList();
                sb.AppendLine("| " + string.Join(" | ", cells) + " |");

                var obj = new Dictionary<string, string>();
                for (int i = 0; i < headers.Count; i++)
                {
                    obj[headers[i]] = i < cells.Count ? cells[i] : string.Empty;
                }
                rows.Add(obj);
            }
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<List<ImageInfo>> GenerateCaptionsAsync(List<Element> images, string openaiChatUrl, string apiKey)
    {
        var results = new List<ImageInfo>();
        foreach (var e in images)
        {
            try
            {
                var b64 = Convert.ToBase64String(e.ImageBytes ?? Array.Empty<byte>());
                var payload = new
                {
                    messages = new object[]
                    {
                        new { role = "system", content = "You caption legal document images and extract exact text." },
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = "Provide a concise caption for this legal document image, and extract any legible text exactly as OCR. Return JSON with keys 'caption' and 'image_text'." },
                                new { type = "image_url", image_url = new { url = $"data:{e.ContentType};base64,{b64}" } }
                            }
                        }
                    },
                    temperature = 0.0,
                    max_tokens = 1500,
                    response_format = new { type = "json_object" }
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, openaiChatUrl);
                req.Headers.Add("api-key", apiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var content = await WithRetryAsync(async ct =>
                {
                    using var res = await Http.SendAsync(req, ct);
                    res.EnsureSuccessStatusCode();
                    return await res.Content.ReadAsStringAsync(ct);
                });
                using var doc = JsonDocument.Parse(content);
                var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
                Dictionary<string, string>? obj = null;
                try
                {
                    obj = JsonSerializer.Deserialize<Dictionary<string, string>>(msg);
                }
                catch
                {
                    obj = new Dictionary<string, string> { ["caption"] = msg, ["image_text"] = string.Empty };
                }
                results.Add(new ImageInfo
                {
                    FigureId = e.Metadata.GetValueOrDefault("figure_id")!,
                    FileName = e.Metadata.GetValueOrDefault("filename")!,
                    ImageBytes = e.ImageBytes ?? Array.Empty<byte>(),
                    ContentType = e.ContentType ?? "image/jpeg",
                    Caption = obj!.GetValueOrDefault("caption"),
                    OcrText = obj!.GetValueOrDefault("image_text")
                });
            }
            catch (Exception ex)
            {
                // Best-effort: still return placeholder
                results.Add(new ImageInfo
                {
                    FigureId = e.Metadata.GetValueOrDefault("figure_id")!,
                    FileName = e.Metadata.GetValueOrDefault("filename")!,
                    ImageBytes = e.ImageBytes ?? Array.Empty<byte>(),
                    ContentType = e.ContentType ?? "image/jpeg",
                    Caption = e.Content,
                    OcrText = string.Empty
                });
                Console.WriteLine($"Caption/OCR failed for {e.Metadata.GetValueOrDefault("filename")}: {ex.Message}");
            }
        }
        return results;
    }

    private static async Task<float[]> CreateEmbeddingAsync(string openaiEmbedUrl, string apiKey, string text)
    {
        var payload = new { input = text };
        using var req = new HttpRequestMessage(HttpMethod.Post, openaiEmbedUrl);
        req.Headers.Add("api-key", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var content = await WithRetryAsync(async ct =>
        {
            using var res = await Http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(ct);
        });
        using var doc = JsonDocument.Parse(content);
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vec = new float[arr.GetArrayLength()];
        int i = 0;
        foreach (var f in arr.EnumerateArray()) vec[i++] = f.GetSingle();
        return vec;
    }
}
