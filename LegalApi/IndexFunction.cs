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
      <button id="copy">Copy Answer</button>
    </div>
    <div id="status" style="margin-top:0.5rem;color:#444" aria-live="polite"></div>
  </div>

  <div class="panel">
    <div class="header">Response</div>
    <details open>
      <summary><strong>Country detection</strong></summary>
      <pre id="outHeader">(no header yet)</pre>
    </details>
    <div id="out" style="margin-top:0.5rem">(no response yet)</div>
  </div>

  <div class="panel">
    <div class="header">Cleanup Index</div>
    <input id="iso" type="text" placeholder="e.g., CH or ALL">
    <button id="cleanup">Cleanup</button>
    <pre id="adminOut"></pre>
  </div>

  <div class="panel">
    <div class="header">Upload Blob</div>
    <input id="file" type="file">
    <input id="passcode" type="password" placeholder="Passcode (optional)">
    <button id="upload">Upload</button>
    <pre id="uploadStatus"></pre>
  </div>

  <div class="panel">
    <div class="header">History</div>
    <ul id="history"></ul>
  </div>

  <script src="https://cdn.jsdelivr.net/npm/marked/marked.min.js"></script>
  <script src="https://cdn.jsdelivr.net/npm/dompurify@3.1.6/dist/purify.min.js"></script>
  <script>
    const $ = (id) => document.getElementById(id);
    const out = $("out");
    const outHeader = $("outHeader");
    const statusEl = $("status");
    const historyEl = $("history");
    const adminOut = $("adminOut");
    const uploadStatus = $("uploadStatus");

    const history = [];
    function pushHistory(q, summary){
      history.unshift({q, summary, t: new Date().toLocaleTimeString()});
      while(history.length>5) history.pop();
      historyEl.innerHTML = history.map(h => `<li><strong>${h.t}</strong> — ${h.q}<br/><em>${h.summary||''}</em></li>`).join('');
    }

    function renderMarkdown(md){
      if (window.DOMPurify && window.marked){
        return DOMPurify.sanitize(marked.parse(md||''));
      }
      // Fallback: escape
      const div=document.createElement('div'); div.innerText = md||''; return div.innerHTML;
    }

    async function ask() {
      const question = $("q").value.trim();
      if (!question) { out.textContent = "Please enter a question."; return; }
      statusEl.textContent = "Identifying countries in user query…";
      out.textContent = "Loading…";
      try {
        const res = await fetch('/api/ask', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ question })
        });
        const data = await res.json();
        outHeader.textContent = data.country_header || '';
        statusEl.textContent = `Finalized. Detected countries: ${data?.country_detection?.summary || ''}`;
        out.innerHTML = renderMarkdown(data.refined_answer || '');
        pushHistory(question, data?.country_detection?.summary || '');
      } catch (e) {
        out.innerText = 'Error: ' + e;
        statusEl.textContent = 'Error generating answer.';
      }
    }

    async function ping() {
      out.textContent = "Pinging…";
      try {
        const txt = await fetch('/api/ask?ping=1').then(r => r.text());
        out.textContent = txt;
      } catch (e) {
        out.textContent = 'Ping error: ' + e;
      }
    }

    $("ask").addEventListener('click', ask);
    $("ping").addEventListener('click', ping);
    $("copy").addEventListener('click', async () => {
      try{ await navigator.clipboard.writeText(out.innerText); statusEl.textContent = 'Answer copied.'; }catch{}
    });

    $("cleanup").addEventListener('click', async () => {
      const iso = $("iso").value.trim().toUpperCase();
      if (!iso){ adminOut.innerText = 'Enter ISO code (e.g., CH) or ALL.'; return; }
      adminOut.innerText = 'Cleaning…';
      try{
        const res = await fetch('/api/cleanup_index', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ iso_code: iso })});
        const data = await res.json();
        adminOut.innerText = JSON.stringify(data,null,2);
      }catch(e){ adminOut.innerText = 'Cleanup error: '+e; }
    });

    $("upload").addEventListener('click', async () => {
      const file = $("file").files[0];
      const pass = $("passcode").value.trim();
      uploadStatus.textContent = '';
      if(!file){ uploadStatus.textContent='Select a .docx file named XX.docx'; return; }
      if(!/^([A-Z]{2})\.docx$/.test(file.name)){ uploadStatus.textContent='Filename must be exactly XX.docx (uppercase ISO).'; return; }
      try{
        const b64 = await new Promise((resolve,reject)=>{ const fr=new FileReader(); fr.onload=()=>resolve(fr.result.split(',')[1]); fr.onerror=reject; fr.readAsDataURL(file); });
        uploadStatus.textContent='Uploading…';
        const headers = { 'Content-Type':'application/json' };
        if(pass) headers['x-legal-admin-passcode'] = pass;
        const res = await fetch('/api/upload_blob', { method:'POST', headers, body: JSON.stringify({ filename:file.name, file_data:b64, container:'legaldocsrag', passcode:pass||undefined }) });
        const data = await res.json();
        uploadStatus.textContent = JSON.stringify(data,null,2);
      }catch(e){ uploadStatus.textContent='Upload error: '+e; }
    });
  </script>
</body>
</html>
""";

        await res.WriteStringAsync(html);
        return res;
    }
}
