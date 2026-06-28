using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

// ---------------------------------------------------------------------------
//  MockRestServer
//  A throwaway REST server used to exercise the FileTransfer service end-to-end.
//  It captures every request it receives and echoes a configurable status code,
//  so it can stand in for BOTH upstream HTTP flows of the FileTransfer service:
//
//    * the SENDER  uploads files via multipart/form-data  (POST .../api/files/upload)
//    * the RECEIVER posts file-status records to the registered callback URL
//
//  Captured requests can be inspected via the /__requests endpoint, which makes
//  it easy to assert what the service actually sent.
//
//  Config (appsettings.json, env vars or --MockServer:Xxx args):
//    MockServer:Port        port to listen on              (default 6000)
//    MockServer:StatusCode  status code returned to callers (default 200)
//                           set to e.g. 500 to test failure/retry paths
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// Accept arbitrarily large uploads (the receiver allows up to 512 MB by default).
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = long.MaxValue;
    o.ValueLengthLimit         = int.MaxValue;
});

var app = builder.Build();

int port       = builder.Configuration.GetValue("MockServer:Port", 6000);
int statusCode = builder.Configuration.GetValue("MockServer:StatusCode", 200);

var captured = new ConcurrentQueue<CapturedRequest>();

// --- Inspection / control endpoints (explicit routes win over the catch-all) ---

app.MapGet("/__ping", () => Results.Text("ok"));
app.MapGet("/__requests", () => Results.Json(captured.ToArray()));
app.MapGet("/__requests/count", () => Results.Json(new { count = captured.Count }));
app.MapPost("/__reset", () =>
{
    captured.Clear();
    app.Logger.LogInformation("Captured request log cleared.");
    return Results.Ok();
});

// --- Catch-all: record and acknowledge every other request (any method/path) ---

app.Map("/{**path}", async (HttpContext ctx) =>
{
    var req   = ctx.Request;
    var files = new List<CapturedFile>();
    string? body = null;

    if (req.HasFormContentType)
    {
        var form = await req.ReadFormAsync();
        foreach (var f in form.Files)
            files.Add(new CapturedFile(f.Name, f.FileName, f.Length));

        var fields = form
            .Where(kv => !string.IsNullOrEmpty(kv.Key))
            .Select(kv => $"{kv.Key}={kv.Value}");
        body = string.Join("; ", fields);
    }
    else
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        body = await reader.ReadToEndAsync();
    }

    var record = new CapturedRequest(
        DateTime.UtcNow,
        req.Method,
        req.Path + req.QueryString,
        req.ContentType ?? string.Empty,
        body,
        files);
    captured.Enqueue(record);

    if (files.Count > 0)
        app.Logger.LogInformation("{Method} {Path}  files=[{Files}]  fields=[{Fields}]",
            record.Method, record.Path,
            string.Join(", ", files.Select(f => $"{f.FileName} ({f.SizeBytes} B)")),
            body);
    else
        app.Logger.LogInformation("{Method} {Path}  body={Body}",
            record.Method, record.Path, Truncate(body));

    ctx.Response.StatusCode = statusCode;
    await ctx.Response.WriteAsJsonAsync(new
    {
        status = statusCode is >= 200 and < 300 ? "ok" : "error",
        method = record.Method,
        path   = record.Path
    });
});

app.Logger.LogInformation(
    "MockRestServer listening on http://0.0.0.0:{Port} (responding {StatusCode}). " +
    "Inspect traffic at GET /__requests.", port, statusCode);

app.Run($"http://0.0.0.0:{port}");

static string Truncate(string? s)
{
    s ??= string.Empty;
    return s.Length > 500 ? s[..500] + "…" : s;
}

// --- Captured request model ------------------------------------------------

internal sealed record CapturedRequest(
    DateTime TimestampUtc,
    string Method,
    string Path,
    string ContentType,
    string? Body,
    List<CapturedFile> Files);

internal sealed record CapturedFile(string Field, string FileName, long SizeBytes);
