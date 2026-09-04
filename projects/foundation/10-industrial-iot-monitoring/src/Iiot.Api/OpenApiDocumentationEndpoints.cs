namespace Iiot.Api;

public static class OpenApiDocumentationEndpoints
{
    public static void MapOpenApiDocumentation(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        app.MapGet("/docs", () => Results.Content(Html, "text/html; charset=utf-8")).AllowAnonymous();
    }

    private const string Html = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>Industrial IoT API documentation</title>
        <style>body{font-family:system-ui,sans-serif;max-width:1000px;margin:32px auto;color:#17212b}code{background:#f1f3f5;padding:2px 4px}details{border:1px solid #d7dce0;margin:8px 0;padding:10px}summary{cursor:pointer;font-weight:600}pre{white-space:pre-wrap;background:#f7f8fa;padding:12px}</style>
        </head><body><h1>Industrial IoT API</h1><p>Development OpenAPI explorer. The raw contract is <a href="/openapi/v1.json">/openapi/v1.json</a>.</p><div id="operations">Loading contract…</div>
        <script>
        fetch('/openapi/v1.json').then(r=>r.json()).then(doc=>{
          const operations=document.getElementById('operations');operations.textContent='';
          Object.entries(doc.paths||{}).forEach(([path,methods])=>Object.entries(methods).forEach(([method,operation])=>{
            const detail=document.createElement('details'),summary=document.createElement('summary'),pre=document.createElement('pre');
            summary.textContent=method.toUpperCase()+' '+path+' — '+(operation.summary||operation.operationId||'');
            pre.textContent=JSON.stringify(operation,null,2);detail.append(summary,pre);operations.append(detail);
          }));
        }).catch(error=>document.getElementById('operations').textContent='Unable to load OpenAPI document: '+error);
        </script></body></html>
        """;
}
