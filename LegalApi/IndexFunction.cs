using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace LegalApi;

public class IndexFunction
{
    private readonly ILogger _logger;
    public IndexFunction(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<IndexFunction>();
    }

    [Function("Index")] 
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "")] HttpRequestData req)
    {
        _logger.LogInformation("Serving inline UI index page");
        var res = req.CreateResponse(HttpStatusCode.OK);
        res.Headers.Add("Content-Type", "text/html; charset=utf-8");

        var html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>LegalChat – Local UI</title>
  <style>
    body { font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; margin: 2rem; color: #111; }
    .row { display: flex; gap: 1rem; }
    textarea { width: 100%; height: 120px; }
    button { padding: 0.5rem 1rem; font-size: 1rem; }
    .panel { margin-top: 1rem; padding: 1rem; border: 1px solid #ddd; border-radius: 8px; background: #fafafa; }
    pre { white-space: pre-wrap; }
    .header { font-size: 1.25rem; font-weight: 600; margin-bottom: 0.5rem; }
  </style>
</head>
<body>
  <h1>LegalChat – Local UI</h1>
  <div class="panel">
    <div class="header">Ask a question</div>
    <textarea id="q" placeholder="e.g., Summarize knife carry rules in CH and DE"></textarea>
    <div class="row">
      <button id="ask">Ask</button>
      <button id="ping">Ping</button>
    </div>
  </div>

  <div class="panel">
    <div class="header">Response</div>
    <pre id="out">(no response yet)</pre>
  </div>

  <script>
    const $ = (id) => document.getElementById(id);
    const out = $("out");

    async function ask() {
      const question = $("q").value.trim();
      if (!question) { out.textContent = "Please enter a question."; return; }
      out.textContent = "Loading...";
      try {
        const res = await fetch('/api/ask', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ question })
        });
        const data = await res.json();
        out.textContent = JSON.stringify(data, null, 2);
      } catch (e) {
        out.textContent = 'Error: ' + e;
      }
    }

    async function ping() {
      out.textContent = "Pinging...";
      try {
        const txt = await fetch('/api/ask?ping=1').then(r => r.text());
        out.textContent = txt;
      } catch (e) {
        out.textContent = 'Ping error: ' + e;
      }
    }

    $("ask").addEventListener('click', ask);
    $("ping").addEventListener('click', ping);
  </script>
</body>
</html>
""";

        await res.WriteStringAsync(html);
        return res;
    }
}
