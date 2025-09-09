using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Linq;

namespace LegalApi;

public class AskFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null, // preserve exact property names with underscores
        WriteIndented = true
    };

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

    public AskFunction(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AskFunction>();
    }

    [Function("AskFunction")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "ask")] HttpRequestData req)
    {
        _logger.LogInformation("API function invoked.");

        var query = ParseQuery(req.Url);
        if (query.TryGetValue("ping", out var _))
        {
            var ping = req.CreateResponse(HttpStatusCode.OK);
            ping.Headers.Add("Content-Type", "text/plain");
            await ping.WriteStringAsync("ok");
            return ping;
        }

        // Load environment
        Dictionary<string, string> config;
        try
        {
            config = new()
            {
                ["search_endpoint"] = Environment.GetEnvironmentVariable("KNIFE_SEARCH_ENDPOINT")!,
                ["search_key"] = Environment.GetEnvironmentVariable("KNIFE_SEARCH_KEY")!,
                ["openai_endpoint"] = Environment.GetEnvironmentVariable("KNIFE_OPENAI_ENDPOINT")!,
                ["openai_key"] = Environment.GetEnvironmentVariable("KNIFE_OPENAI_KEY")!,
            };
            if (config.Values.Any(string.IsNullOrWhiteSpace))
            {
                var missingKey = config.First(kv => string.IsNullOrWhiteSpace(kv.Value)).Key;
                var envName = missingKey switch
                {
                    "search_endpoint" => "KNIFE_SEARCH_ENDPOINT",
                    "search_key" => "KNIFE_SEARCH_KEY",
                    "openai_endpoint" => "KNIFE_OPENAI_ENDPOINT",
                    "openai_key" => "KNIFE_OPENAI_KEY",
                    _ => missingKey
                };
                var error = $"Configuration error: Missing required environment variable: {envName}";
                _logger.LogError(error);
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync(error);
                return res;
            }
            config["index_name"] = Environment.GetEnvironmentVariable("KNIFE_SEARCH_INDEX") ?? "knife-index";
            config["deploy_chat"] = Environment.GetEnvironmentVariable("OPENAI_CHAT_DEPLOY") ?? "gpt-5-chat";
            config["deploy_embed"] = Environment.GetEnvironmentVariable("OPENAI_EMBED_DEPLOY") ?? "text-embedding-3-large";
            config["api_version"] = Environment.GetEnvironmentVariable("OPENAI_API_VERSION") ?? "2024-12-01-preview";
        }
        catch (Exception ex)
        {
            var msg = $"Configuration error: {ex.Message}";
            _logger.LogError(ex, msg);
            var res = req.CreateResponse(HttpStatusCode.InternalServerError);
            await res.WriteStringAsync(msg);
            return res;
        }

        try
        {
            // Read question
            string? question = query.TryGetValue("question", out var qv) ? qv : null;
            if (string.IsNullOrWhiteSpace(question))
            {
                using var sr = new StreamReader(req.Body);
                var bodyText = await sr.ReadToEndAsync();
                if (!string.IsNullOrEmpty(bodyText))
                {
                    try
                    {
                        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bodyText);
                        if (body != null && body.TryGetValue("question", out var q) && q.ValueKind == JsonValueKind.String)
                            question = q.GetString();
                    }
                    catch { /* ignore */ }
                }
            }
            if (string.IsNullOrWhiteSpace(question))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("Please pass a question on the query string or in the request body, e.g., /api/ask?question=...");
                return bad;
            }

            _logger.LogInformation("DEBUG: Starting chat function");
            var tTotal = Stopwatch.GetTimestamp();

            _logger.LogInformation("DEBUG: Step 1 - Extracting ISO codes");
            var tIso = Stopwatch.GetTimestamp();
            var isoCodes = await ExtractIsoCodesAsync(question!, config);
            var isoMs = ElapsedMs(tIso);
            _logger.LogInformation("TIMING: iso_detection_ms={isoMs}", isoMs);
            _logger.LogInformation("DEBUG: ISO codes extracted: {iso}", string.Join(",", isoCodes));

            if (isoCodes.Count == 0)
            {
                _logger.LogInformation("DEBUG: No ISO codes found, returning error message");
                var none = new AskResponse
                {
                    country_header = "",
                    refined_answer = "Could not determine a country from your query. Please be more specific.",
                    country_detection = new CountryDetection
                    {
                        iso_codes = new List<string>(),
                        available = new List<string>(),
                        summary = ""
                    }
                };
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonSerializer.Serialize(none, JsonOptions));
                return res;
            }

            _logger.LogInformation("DEBUG: Step 2 - Retrieving documents");
            int baseK = 15;
            int retrievalK = (isoCodes.Count == 1) ? baseK : Math.Min(isoCodes.Count * 10, 50);
            _logger.LogInformation("DEBUG: Using dynamic k={k} for {n} countries: {iso}", retrievalK, isoCodes.Count, string.Join(",", isoCodes));
            _logger.LogInformation("DEBUG: Multi-jurisdictional query detected: {multi}", isoCodes.Count > 1);
            var tRetrieve = Stopwatch.GetTimestamp();
            var chunks = await RetrieveAsync(question!, isoCodes, config, retrievalK);
            var retrieveMs = ElapsedMs(tRetrieve);
            _logger.LogInformation("TIMING: retrieve_total_ms={ms}", retrieveMs);
            _logger.LogInformation("DEBUG: Retrieved {count} chunks", chunks.Count);

            if (chunks.Count == 0)
            {
                _logger.LogInformation("DEBUG: No chunks found, building no-docs response");
                var foundIso = new HashSet<string>();
                var header = BuildResponseHeader(isoCodes, foundIso);
                var summaryList = isoCodes.OrderBy(c => c).Select(c => $"{c} {(foundIso.Contains(c) ? "✅" : "❌")}").ToList();
                var cd = new CountryDetection
                {
                    iso_codes = isoCodes,
                    available = foundIso.OrderBy(x => x).ToList(),
                    summary = string.Join(", ", summaryList)
                };
                var noDocsMessage = $"No documents found for the specified countries: {string.Join(", ", isoCodes)}. Please try another query or check if the relevant legislation is available.";
                var responseObj = new AskResponse
                {
                    country_header = header,
                    refined_answer = noDocsMessage,
                    country_detection = cd
                };
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonSerializer.Serialize(responseObj, JsonOptions));
                return res;
            }

            _logger.LogInformation("DEBUG: Step 3 - Preparing context for single-pass answer generation");
            var structured = new List<string>();
            for (int i = 0; i < chunks.Count; i++)
            {
                structured.Add($"**SOURCE {i + 1}: KL {chunks[i].iso_code} (Document Section)**\n{chunks[i].chunk}");
            }
            var context = string.Join("\n\n---\n\n", structured);
            _logger.LogInformation("DEBUG: Structured context built with {n} sources, {len} characters", chunks.Count, context.Length);
            var jurisdictionList = chunks.Select(c => $"KL {c.iso_code}").ToList();
            _logger.LogInformation("DEBUG: Sources by jurisdiction: {list}", string.Join(",", jurisdictionList));
            _logger.LogInformation("DEBUG: Jurisdiction-aware evaluation will expect comprehensive coverage of: {iso}", string.Join(",", isoCodes));

            var foundCodes = chunks.Select(c => c.iso_code).ToHashSet();
            var headerTable = BuildResponseHeader(isoCodes, foundCodes);
            var summary = string.Join(", ", isoCodes.OrderBy(c => c).Select(c => $"{c} {(foundCodes.Contains(c) ? "✅" : "❌")}"));
            var detection = new CountryDetection
            {
                iso_codes = isoCodes,
                available = foundCodes.OrderBy(x => x).ToList(),
                summary = summary
            };

            _logger.LogInformation("DEBUG: Header and country_detection built for UI");

            _logger.LogInformation("DEBUG: Step 4 - Generating draft answer...");
            var tDraft = Stopwatch.GetTimestamp();
            var answer = await GenerateDraftAsync(question!, context, config);
            var draftMs = ElapsedMs(tDraft);
            _logger.LogInformation("TIMING: llm_draft_ms={ms}", draftMs);

            var final = new AskResponse
            {
                country_header = headerTable,
                refined_answer = answer,
                country_detection = detection
            };
            var totalMs = ElapsedMs(tTotal);
            _logger.LogInformation("TIMING: total_pipeline_ms={ms}", totalMs);
            _logger.LogInformation("DEBUG: RAG pipeline completed");

            var ok = req.CreateResponse(HttpStatusCode.OK);
            ok.Headers.Add("Content-Type", "application/json");
            await ok.WriteStringAsync(JsonSerializer.Serialize(final, JsonOptions));
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEBUG: Chat function failed at some step: {err}", ex.Message);
            var res = req.CreateResponse(HttpStatusCode.InternalServerError);
            await res.WriteStringAsync("An internal server error occurred. Please check the logs for details. Error ID: N/A");
            return res;
        }
    }

    private static long ElapsedMs(long startTimestamp)
    {
        var elapsed = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
        return (long)(elapsed * 1000);
    }

    private record SearchChunk(string id, string iso_code, string chunk);

    private static async Task<List<SearchChunk>> RetrieveAsync(string query, List<string> isoCodes, Dictionary<string, string> cfg, int k)
    {
        // Embedding
        List<float> vector;
        try
        {
            vector = await WithRetryAsync(async ct => await GenerateEmbeddingAsync(query, cfg, ct));
        }
        catch
        {
            throw;
        }

        var searchUrl = $"{cfg["search_endpoint"]}/indexes/{cfg["index_name"]}/docs/search?api-version=2023-11-01";
        var headers = new HttpRequestMessage(HttpMethod.Post, searchUrl);
        headers.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        headers.Headers.Add("api-key", cfg["search_key"]);

        var filter = $"search.in(iso_code, '{string.Join(',', isoCodes)}', ',')";
        var searchK = isoCodes.Count > 1 ? Math.Max(k * isoCodes.Count, 10) : k;

        var payload = new
        {
            vectorQueries = new object[]
            {
                new { kind = "vector", vector = vector, fields = "embedding", k = searchK }
            },
            filter = filter,
            select = "chunk,iso_code,id"
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        headers.Content = new StringContent(json, Encoding.UTF8, "application/json");

        // Logging
        // embed_ms already logged inside GenerateEmbeddingAsync
        var tSearch = Stopwatch.GetTimestamp();
        Console.WriteLine($"DEBUG: Sending search request to {searchUrl}");
        Console.WriteLine($"DEBUG: Filter: {filter}");
        Console.WriteLine($"DEBUG: Search k adjusted from {k} to {searchK} for {isoCodes.Count} countries");
        Console.WriteLine($"DEBUG: Payload keys: vectorQueries, filter, select");

        // Send with retry and 15s timeout
        var response = await WithRetryAsync(async ct =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var res = await Http.SendAsync(headers, cts.Token);
            res.EnsureSuccessStatusCode();
            var content = await res.Content.ReadAsStringAsync(cts.Token);
            return (res, content);
        });

        var searchMs = ElapsedMs(tSearch);
        Console.WriteLine($"TIMING: search_ms={searchMs}");

        using var doc = JsonDocument.Parse(response.content);
        var rawResults = new List<SearchChunk>();
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in arr.EnumerateArray())
            {
                var id = v.GetProperty("id").GetString() ?? string.Empty;
                var iso = v.GetProperty("iso_code").GetString() ?? string.Empty;
                var chunk = v.GetProperty("chunk").GetString() ?? string.Empty;
                rawResults.Add(new SearchChunk(id, iso, chunk));
            }
        }
        Console.WriteLine($"DEBUG: Raw search returned {rawResults.Count} documents");

        // Balance for multi-country
        if (isoCodes.Count > 1 && rawResults.Count > 0)
        {
            var balanced = BalanceCountryRepresentation(rawResults, isoCodes, k);
            var distinct = balanced.Select(b => b.iso_code).Distinct().Count();
            Console.WriteLine($"DEBUG: Balanced results: {balanced.Count} documents from {distinct} countries");
            return balanced;
        }
        else
        {
            return rawResults.Take(k).ToList();
        }
    }

    private static List<SearchChunk> BalanceCountryRepresentation(List<SearchChunk> results, List<string> isoCodes, int k)
    {
        if (results.Count == 0 || isoCodes.Count <= 1) return results.Take(k).ToList();

        var byCountry = results.GroupBy(r => r.iso_code).ToDictionary(g => g.Key, g => g.ToList());
        var available = isoCodes.Where(c => byCountry.ContainsKey(c)).ToList();
        if (available.Count == 0) return results.Take(k).ToList();
        // Use the requested k as the balancing target, matching Python behavior
        var targetK = k;
        var docsPer = Math.Max(1, targetK / isoCodes.Count);
        var remainder = targetK % isoCodes.Count;

        var balanced = new List<SearchChunk>();
        for (int i = 0; i < available.Count; i++)
        {
            var c = available[i];
            var take = docsPer + (i < remainder ? 1 : 0);
            balanced.AddRange(byCountry[c].Take(take));
        }

        if (balanced.Count < targetK)
        {
            foreach (var c in available)
            {
                if (balanced.Count >= targetK) break;
                var extras = byCountry[c].Skip(docsPer).ToList();
                var need = targetK - balanced.Count;
                balanced.AddRange(extras.Take(need));
            }
        }

        return balanced.Take(k).ToList();
    }

    private static string BuildResponseHeader(List<string> isoCodes, HashSet<string> found)
    {
        if (isoCodes.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.Append("# Country Detection\n\n");
        sb.Append("| Detected in Query | Document Available |\n");
        sb.Append("|:-----------------:|:------------------:|\n");
        foreach (var code in isoCodes.OrderBy(c => c))
        {
            var flag = IsoToFlag(code);
            var icon = found.Contains(code) ? "✅" : "❌";
            sb.Append($"| {flag} ({code}) | {icon} |\n");
        }
        sb.Append("\n---\n\n");
        return sb.ToString();
    }

    private static string IsoToFlag(string iso)
    {
        if (string.IsNullOrEmpty(iso) || iso.Length != 2) return string.Empty;
        var first = char.ConvertFromUtf32(char.ToUpperInvariant(iso[0]) - 'A' + 0x1F1E6);
        var second = char.ConvertFromUtf32(char.ToUpperInvariant(iso[1]) - 'A' + 0x1F1E6);
        return first + second;
    }

    private static async Task<List<string>> ExtractIsoCodesAsync(string question, Dictionary<string, string> cfg)
    {
        try
        {
            var payload = new
            {
                messages = new object[]
                {
                    new { role = "system", content = COUNTRY_DETECTION_PROMPT },
                    new { role = "user", content = question }
                },
                temperature = 0.0
            };
            var apiVersion = cfg["api_version"];
            var url = $"{cfg["openai_endpoint"]}/openai/deployments/{cfg["deploy_chat"]}/chat/completions?api-version={apiVersion}";
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("api-key", cfg["openai_key"]);

            var (res, content) = await WithRetryAsync(async ct =>
            {
                using var response = await Http.SendAsync(req, ct);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(ct);
                return (response, body);
            });

            using var doc = JsonDocument.Parse(content);
            var rawContent = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!.Trim();
            var cleaned = rawContent.Trim();
            if (cleaned.StartsWith("```json")) cleaned = cleaned[7..];
            if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            List<string> result = new();
            using var json = JsonDocument.Parse(cleaned);
            if (json.RootElement.ValueKind == JsonValueKind.Array)
            {
                var used = new HashSet<string>();
                foreach (var item in json.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("code", out var codeEl))
                    {
                        var code = codeEl.GetString();
                        if (!string.IsNullOrWhiteSpace(code) && used.Add(code!))
                        {
                            result.Add(code!);
                        }
                    }
                }
            }
            return result;
        }
        catch (Exception e)
        {
            // Parse failure → empty list
            return new List<string>();
        }
    }

    private static async Task<List<float>> GenerateEmbeddingAsync(string text, Dictionary<string, string> cfg, CancellationToken ct)
    {
        // Diagnostics
        Console.WriteLine($"DEBUG: Embedding request - deploy='{cfg["deploy_embed"]}', api_version='{cfg["api_version"]}', endpoint='{cfg["openai_endpoint"]}'");

        var payload = new { input = text };
        var url = $"{cfg["openai_endpoint"]}/openai/deployments/{cfg["deploy_embed"]}/embeddings?api-version={cfg["api_version"]}";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("api-key", cfg["openai_key"]);

        var t = Stopwatch.GetTimestamp();
        var (res, content) = await RetryPipeline.ExecuteAsync(async innerCt =>
        {
            using var response = await Http.SendAsync(req, innerCt);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(innerCt);
            return (response, body);
        });
        var embedMs = ElapsedMs(t);
        Console.WriteLine($"TIMING: embed_ms={embedMs}");

        using var doc = JsonDocument.Parse(content);
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vec = new List<float>(arr.GetArrayLength());
        foreach (var f in arr.EnumerateArray()) vec.Add(f.GetSingle());
        return vec;
    }

    private static async Task<string> GenerateDraftAsync(string question, string context, Dictionary<string, string> cfg)
    {
        var payload = new
        {
            messages = new object[]
            {
                new { role = "system", content = DRAFTER_SYSTEM_MESSAGE },
                new { role = "user", content = $"QUESTION:\n{question}\n\nCONTEXT:\n{context}" }
            },
            temperature = 0.0
        };
        var url = $"{cfg["openai_endpoint"]}/openai/deployments/{cfg["deploy_chat"]}/chat/completions?api-version={cfg["api_version"]}";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("api-key", cfg["openai_key"]);

        var (res, content) = await WithRetryAsync(async ct =>
        {
            using var response = await Http.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            return (response, body);
        });

        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!.Trim();
    }

    public class CountryDetection
    {
        [JsonPropertyName("iso_codes")]
        public List<string> iso_codes { get; set; } = new();
        [JsonPropertyName("available")]
        public List<string> available { get; set; } = new();
        [JsonPropertyName("summary")]
        public string summary { get; set; } = string.Empty;
    }

    public class AskResponse
    {
        [JsonPropertyName("country_header")]
        public string country_header { get; set; } = string.Empty;
        [JsonPropertyName("refined_answer")]
        public string refined_answer { get; set; } = string.Empty;
        [JsonPropertyName("country_detection")]
        public CountryDetection country_detection { get; set; } = new();
    }

    private const string COUNTRY_DETECTION_PROMPT = @"ROLE
You are a specialized assistant whose sole task is to extract country references from user text.

SCOPE OF EXTRACTION
Return **every** genuine country reference that can be inferred, using the rules below:
1.  ISO 3166-1 ALPHA-2 CODES
    •  Detect any two-letter, UPPER-CASE sequence in the input text that is a valid ISO 3166-1 alpha-2 country code (e.g., CH, US, CN, DE).
    •  Crucially, ignore common short words (typically 2-3 letters), especially if lowercase, that might incidentally resemble country codes OR that you might mistakenly associate with a country. This includes articles, prepositions, pronouns, and other grammatical particles in any language (e.g., English "in", "on", "it", "is", "at", "to", "do", "am", "pm", "id", "tv", "an", "of", "or"; German "ich", "er", "sie", "es", "der", "die", "das", "ein", "mit", "auf", "in", "zu", "so", "ob"). Such words should ONLY be considered if they are unambiguously used as a direct country reference AND appear in uppercase as a specific ISO code.
    •  Context must strongly support that the sequence is a country indicator, not an accidental substring or a common word.

2.  COUNTRY NAMES (any language)
    •  Official and common names, case-insensitive: “Switzerland”, “switzerland”.
    •  Major international variants: “Deutschland”, “Schweiz”, “Suiza”, “Éire”, …
    •  Adjectival forms that clearly point to a country: “Swiss law”, “German regulations”.

3.  TRANSNATIONAL ENTITIES, GEOPOLITICAL GROUPINGS & WELL-KNOWN NICKNAMES
    Your goal is to identify entities that represent a group of countries.
    - For the explicitly listed examples below, you MUST expand them to ALL their constituent ISO codes as specified. For each constituent country, create a separate JSON entry using the original detected entity/nickname as the "detected_phrase".
        - "EuroAirport" (also "Basel-Mulhouse-Freiburg"): output CH, FR
        - "Benelux": output BE, NL, LU
        - "The Nordics" (context-dependent): output DK, NO, SE, FI, IS
        - "Iberian Peninsula" (also "Iberische Halbinsel"): output ES, PT
        - "Baltics" (also "Baltische Staaten"): output EE, LV, LT
        - "Scandinavia" (also "Skandinavien"): output DK, NO, SE
    - For other similar transnational entities, intergovernmental organizations (e.g., EFTA, ASEAN, Mercosur), or well-known geopolitical groupings not explicitly listed, if you can confidently identify them and their constituent member countries, you SHOULD also expand them in the same way. If you are not confident about the members of such an unlisted group, do not extract it.

    •  When such an entity is processed, output *all* its known constituent countries.
    •  Do **not** substitute “EU” (the European Union itself is not a country for this purpose, though its member states are if individually referenced).

4.  CONTEXTUAL RULES
    •  Prepositions or articles (“in Switzerland”) never block detection of the country name itself.
    •  Mixed lists are fine: “switzerland, Deutschland & CN”.
    •  Ambiguous or purely figurative uses → **skip**. Err on the side of precision. Only extract if you are highly confident it's a geographical reference.

FORMATTING RULES
•  Output a JSON array exactly in this form:

    ```json
    [
      {"detected_phrase": "<exact text>", "code": "XX"},
      … 
    ]
    ```

•  Preserve the original casing from the input text in "detected_phrase".
•  The "detected_phrase" itself: if its length is 4 characters or less, it MUST be a valid ISO 3166-1 alpha-2 code AND it MUST have appeared in ALL UPPERCASE in the original user text. For example, if the user types "us", do not extract it; if the user types "US", extract it as {"detected_phrase": "US", "code": "US"}. Common lowercase words like "in", "it", "am", "is", "to", "der", "mit" (even if their uppercase versions are valid ISO codes like "IN", "IT", "AM", "IS", "TO", "DE") must not be extracted if they appeared lowercase in the input and are being used as common words.
•  If nothing is found, return `[]`.
";

    private const string DRAFTER_SYSTEM_MESSAGE = @"<role>
You are a jurisdiction‑aware legal drafting agent in a RAG pipeline. Your ONLY knowledge source is the available legal sources supplied to you. Never use outside facts or prior‑turn memory. Do not ask the user clarifying questions—draft strictly from the available legal sources.
</role>

<control>
- reasoning_effort: minimal→medium (favor fast, correct extraction; avoid speculation).
- verbosity: low (compact, information‑dense bullets; no filler text).
- If the sources conflict, surface the conflict briefly; do NOT resolve by guessing.
- Precision‑first: do not assert any figure, rule, example, or practice unless it appears in the sources.
</control>

<generation_order>
- Draft the **Details** section first (internally), then compress a faithful **Summary**.
- **Summary must introduce no claims absent from Details.**
- **Self‑check before returning:** delete any Summary bullet that lacks an exact match in Details or that materially strengthens/weakens it.
</generation_order>

<output_format>
Always return Markdown with exactly two top‑level headings in this order:
## Summary
## Details

Rules:
- Use '-' for bullets everywhere; avoid long paragraphs.
- **Summary:** 4–7 bullets max; high‑signal conclusions for an informed lay reader; **no citations/statute numbers here**.
- **Details:** bulletized and organized under these bolded subheadings when relevant (keep this order):
  **Definitions & Carve‑outs**; **Age & Eligibility**; **Permits & Procedures**; **Penalties & Enforcement**; **Practical Compliance & Measurement**; **Venue & Screened‑Locations**; **Jurisdiction Notes**; **Authoritative Interpretations**.
- Jurisdiction labeling:
  • Single‑jurisdiction queries: OMIT per‑bullet country prefixes. Instead, add one lead bullet at the top of **Summary** — 'Jurisdiction: <Plain name> (<ISO code if provided>)'.
  • Multi‑jurisdiction or unions/facilities: Under each subheading in **Details**, group items by jurisdiction using nested bullets: '- **Switzerland (CH)**:' then two‑space‑indented child bullets. Use plain names for regions/unions/facilities (e.g., 'EuroAirport Basel–Mulhouse–Freiburg').
- Reproduce article/section cites exactly as given in **Details** (e.g., 'Art. 4 WA', 'Art. 28b WA', 'Art. 52 WO'). Do not invent citations.
</output_format>

<required_coverage>
If present in the available legal sources, you MUST explicitly cover:
1) Definitions/classifications and statutory carve‑outs/exemptions.
2) Age thresholds and permit eligibility/issuance rules.
3) Required permits/procedures and criteria (incl. carry/transport rules and operator obligations where stated).
4) Penalties/enforcement (criminal + administrative) at a usable granularity.
5) Authoritative interpretations/guidance (agency practice; court decisions) that clarify definitions/classifications or scope.
6) Safe storage/keeping and loss/theft reporting obligations.
7) Measurement methodology for legal thresholds (e.g., how blade or overall length is measured) when specified.
8) Venue‑ or security‑screened‑location rules (e.g., airports, stadiums, courthouses) including any length/item thresholds **only when present**, and whether stricter local/operator rules are allowed **only when stated**.
</required_coverage>

<normative_hierarchy>
When sources span multiple normative layers, respect this order:
- Supranational baselines (e.g., EU/ICAO) exactly as stated.
- National law; then sub‑national (state/cantonal/municipal).
- Operator/house rules (airlines, airports, venues) **only if present in the sources**.
- Do NOT assume harmonization; note explicitly when stricter measures are permitted or adopted, but only if the sources say so.
</normative_hierarchy>

<multi_entity_scope>
For regions/unions/joint facilities (e.g., 'Baltic States', 'EuroAirport Basel–Mulhouse–Freiburg'):
- Do NOT invent unified rules. Summarize per member jurisdiction as given.
- If shared rules exist, state the exact scope and enumerate any exceptions **as written**.
</multi_entity_scope>

<age_policy>
- If a numeric age threshold or explicit rule for minors is provided, state it verbatim under **Age & Eligibility**, and only infer implications that are explicitly stated.
- If age/minors are silent or ambiguous, include this exact bullet under **Age & Eligibility**:
  - No numeric minimum age found in the available legal sources; eligibility must be assessed against the stated statutory criteria.
</age_policy>

<measurement_policy>
- If measurement methods are provided, restate them precisely (e.g., 'blade length: tip → point where the blade shank exits the handle').
- If absent, add under **Practical Compliance & Measurement**: 'Measurement method: not found in the available legal sources.'
</measurement_policy>

<claim_gate>
- Require explicit source support for ALL of the following before you state them: numerical thresholds/penalties; measurement methods; operator/airline/airport/venue rules; statements that operators **can/cannot** impose stricter rules; examples naming specific operators or airports.
- If unsupported, omit the claim. Optionally record the gap under **Jurisdiction Notes** as 'Not found in the available legal sources: …'.
</claim_gate>

<inference_policy>
- Do NOT present inferences as facts.
- Only if essential for usability, add at most one clearly labeled inference bullet starting with 'Inference:' and quote the narrow textual basis from the available legal sources.
</inference_policy>

<authorities_policy>
- Where the sources note controversy or divergent views, add a one‑line bullet under **Authoritative Interpretations** prefixed 'Controversy:' summarizing the competing view(s).
- Tie agency practice and court holdings directly to the definitions/scope they clarify.
</authorities_policy>

<jurisdiction_notes>
- If any required category (definitions, age, permits, penalties, storage, measurement, venue rules, authoritative interpretations) is missing from the available legal sources, add under **Jurisdiction Notes** a bullet titled 'Not found in the available legal sources:' followed by the missing category names.
- Only elevate an absence to **Summary** when its omission is a key user risk (e.g., 'No age threshold stated').
</jurisdiction_notes>

<style>
- Be concise, neutral, and usable for an informed lay reader.
- Prefer plain terms; keep statutory terms where necessary.
- Do not include internal chunk IDs or tool chatter. Do not mention 'RAG' or 'CONTEXT' in user‑visible text; use 'available legal sources'.
</style>

<validation_checklist>
Before returning, ensure:
- Exactly two top‑level headings, in order.
- Bullets throughout; required subheadings included when relevant.
- Summary has 4–7 bullets, no citations/statutes, and contains no claims absent from Details.
- All required coverage areas addressed when present; missing areas listed using 'Not found in the available legal sources: …'.
- Single‑jurisdiction: no per‑bullet country prefixes; Multi‑jurisdiction: nested grouping by name.
- Every numeric value, measurement, operator rule, and 'stricter rules allowed/forbidden' statement is source‑backed or omitted.
</validation_checklist>
";
}
