// Program.cs
using Google.Apis.Auth.OAuth2;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// === Config (ENV overrides appsettings.json) ===
string projectId   = builder.Configuration["Gcp:ProjectId"]   ?? Environment.GetEnvironmentVariable("PROJECT_ID")   ?? "";
string triggerId   = builder.Configuration["Gcp:TriggerId"]   ?? Environment.GetEnvironmentVariable("TRIGGER_ID")   ?? "";
string region      = builder.Configuration["Gcp:Region"]      ?? Environment.GetEnvironmentVariable("REGION")       ?? "global";
string progressUrl = builder.Configuration["Gcp:ProgressUrl"] ?? Environment.GetEnvironmentVariable("PROGRESS_URL") ?? "";

// CORS for local UI
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("ui", p => p
        .WithOrigins("http://localhost:4200","http://127.0.0.1:4200",
                     "http://localhost:51516","http://127.0.0.1:51516")
        .AllowAnyHeader()
        .AllowAnyMethod());
});
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();
app.UseCors("ui");

// === In-memory live store (fed by /progress) ===
var stepStore = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();
// key: buildId ; value: stepId(lower) -> "START" | "DONE" | "FAIL"

// live logs (last 200 lines / build)
var logStore  = new ConcurrentDictionary<string, ConcurrentQueue<string>>();

static void EnqueueLog(ConcurrentDictionary<string, ConcurrentQueue<string>> store, string buildId, string line)
{
    var q = store.GetOrAdd(buildId, _ => new ConcurrentQueue<string>());
    q.Enqueue($"{DateTime.UtcNow:HH:mm:ss}  {line}");
    while (q.Count > 200 && q.TryDequeue(out _)) { }
}

// FIX: Adaugă artifactory și github în stepOrder!
string[] stepOrder = new[] { "frontend", "backend", "gitea", "confluence", "jira", "artifactory", "github" };

static string N(string? s) => (s ?? "").Trim().ToLowerInvariant();
static int Rank(string s) => (N(s)) switch
{
    "failure" or "fail" or "internal_error" or "timeout" or "cancelled" => 5,
    "success"                                                           => 4,
    "running" or "working" or "queued"                                  => 3,
    "start"                                                             => 2,
    "unknown" or ""                                                     => 1,
    _                                                                   => 1
};
static string NormalizeLive(string s) => (N(s)) switch
{
    "start" => "RUNNING",
    "done"  => "SUCCESS",
    _       => s.ToUpperInvariant()
};

// --- Health & debug ---
app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/routes", (IEnumerable<EndpointDataSource> sources) =>
{
    var all = sources.SelectMany(s => s.Endpoints)
                     .OfType<RouteEndpoint>()
                     .Select(e => new
                     {
                         Route = e.RoutePattern.RawText,
                         Methods = string.Join(",", e.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods))
                     });
    return Results.Ok(all);
});

// --- /progress (callbacks from Cloud Build) ---
// POST /progress?op=$BUILD_ID&step=frontend&status=START|DONE|FAIL
app.MapPost("/progress", (HttpRequest req) =>
{
    var buildId = req.Query["op"].ToString();
    var step    = N(req.Query["step"].ToString());
    var status  = req.Query["status"].ToString();

    if (string.IsNullOrWhiteSpace(buildId) || string.IsNullOrWhiteSpace(step))
        return Results.BadRequest(new { ok = false, error = "missing op/step" });

    var map = stepStore.GetOrAdd(buildId, _ => new ConcurrentDictionary<string, string>());
    var incoming = NormalizeLive(status);
    map.AddOrUpdate(step, incoming, (_, prev) => Rank(incoming) >= Rank(prev) ? incoming : prev);

    // log live
    EnqueueLog(logStore, buildId, $"[{step}] {(status ?? "START").ToUpperInvariant()}");

    return Results.Ok(new { ok = true });
});

// Preflight
app.MapMethods("/run", new[] { "OPTIONS" }, () => Results.Ok());

// GET /run (diagnostic)
app.MapGet("/run", () => Results.Ok("run-get-ok"));

// --- /run: start Cloud Build trigger ---
app.MapPost("/run", async (RunRequest req) =>
{
    if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(triggerId))
        return Results.BadRequest(new { ok = false, error = "Set PROJECT_ID și TRIGGER_ID" });

    try
    {
        var credential = await GoogleCredential.GetApplicationDefaultAsync();
        var scoped = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        var token = await scoped.UnderlyingCredential.GetAccessTokenForRequestAsync();

        var url = $"https://cloudbuild.googleapis.com/v1/projects/{projectId}/locations/{region}/triggers/{triggerId}:run";
        var branch = string.IsNullOrWhiteSpace(req.Branch) ? "main" : req.Branch;

        var subs = new Dictionary<string, string?>
        {
            ["_TARGETS"] = string.IsNullOrWhiteSpace(req.Targets) ? "frontend,backend" : req.Targets
        };
        if (!string.IsNullOrWhiteSpace(progressUrl))
            subs["_PROGRESS_URL"] = progressUrl;

        var body = new
        {
            projectId,
            triggerId,
            source = new
            {
                branchName = branch,
                substitutions = subs
            }
        };

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var json = JsonSerializer.Serialize(body);
        var resp = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode) return Results.Problem(text, statusCode: (int)resp.StatusCode);

        using var doc = JsonDocument.Parse(text);
        string? opName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;

        return Results.Ok(new { ok = true, operation = opName });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

// --- /status: merge Cloud Build + live overlay (show only canonical steps) ---
// FIX pentru Program.cs - înlocuiește endpoint-ul /status
// Linia aproximativ 157-341

app.MapGet("/status", async (HttpContext http, string operation) =>
{
    try
    {
        http.Response.Headers.CacheControl = "no-store";

        var credential = await GoogleCredential.GetApplicationDefaultAsync();
        var scoped = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        var token = await scoped.UnderlyingCredential.GetAccessTokenForRequestAsync();

        // normalize operation path
        var opPath =
            operation.StartsWith("projects/", StringComparison.OrdinalIgnoreCase)   ? operation :
            operation.StartsWith("operations/", StringComparison.OrdinalIgnoreCase) ? operation :
            $"projects/{projectId}/locations/{(string.IsNullOrWhiteSpace(region) ? "global" : region)}/operations/{operation}";

        var url = $"https://cloudbuild.googleapis.com/v1/{opPath}";

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await httpClient.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return Results.Problem(text, statusCode: (int)resp.StatusCode);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        bool done = root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean();
        string state = "UNKNOWN";
        string? logUrl = null;
        string? buildId = null; // <-- CRUCIAL: extrage din metadata, nu din path

        var merged = new Dictionary<string, string>();

        // 1) Extract build info from metadata
        if (root.TryGetProperty("metadata", out var metadata))
        {
            // Extract buildId from metadata.build.id
            if (metadata.TryGetProperty("build", out var buildEl) &&
                buildEl.TryGetProperty("id", out var idEl))
            {
                buildId = idEl.GetString();
            }

            if (buildEl.ValueKind != JsonValueKind.Undefined)
            {
                // status
                if (buildEl.TryGetProperty("status", out var statusEl))
                    state = statusEl.GetString()?.ToUpperInvariant() ?? "UNKNOWN";

                // logUrl
                if (buildEl.TryGetProperty("logUrl", out var logUrlEl))
                    logUrl = logUrlEl.GetString();

                // canonical steps
                if (buildEl.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var step in stepsEl.EnumerateArray())
                    {
                        string? stepId = null;
                        string? stepStatus = null;

                        if (step.TryGetProperty("id", out var stepIdEl)) stepId = stepIdEl.GetString();
                        if (step.TryGetProperty("status", out var stepStatusEl)) stepStatus = stepStatusEl.GetString();

                        if (!string.IsNullOrWhiteSpace(stepId) && !string.IsNullOrWhiteSpace(stepStatus))
                        {
                            var norm = N(stepId);
                            var incoming = stepStatus?.ToUpperInvariant() ?? "QUEUED";
                            if (!merged.TryGetValue(norm, out var prev) || Rank(incoming) >= Rank(prev))
                                merged[norm] = incoming;
                        }
                    }
                }
            }
        }

        // 2) Overlay live progress from callbacks (if buildId found)
        if (!string.IsNullOrEmpty(buildId) && stepStore.TryGetValue(buildId, out var liveMap))
        {
            foreach (var kv in liveMap)
            {
                var norm = kv.Key;
                var incoming = kv.Value.ToUpperInvariant();
                if (!merged.TryGetValue(norm, out var prev) || Rank(incoming) >= Rank(prev))
                    merged[norm] = incoming;
            }
        }

        // 3) Build ordered list for UI
        var stepsOut = new List<object>();
        foreach (var id in stepOrder)
            if (merged.TryGetValue(id, out var stCanon))
                stepsOut.Add(new { id, status = stCanon });

        // 4) Percent
        int total = stepsOut.Count;
        int finished = stepsOut.Count(x =>
        {
            var stCanon = ((dynamic)x).status?.ToString()?.ToUpperInvariant() ?? "";
            return stCanon is "SUCCESS" or "FAILURE" or "CANCELLED" or "INTERNAL_ERROR" or "TIMEOUT";
        });

        int percent;
        if (total > 0)
        {
            percent = (int)Math.Round((finished * 100.0) / Math.Max(1, total));
            if (!done && percent >= 100) percent = 99;
        }
        else
        {
            percent = state switch
            {
                "QUEUED"  => 5,
                "WORKING" => 50,
                "SUCCESS" => 100,
                "FAILURE" or "CANCELLED" or "INTERNAL_ERROR" or "TIMEOUT" => 100,
                _ => 0
            };
        }
        if (done && state == "SUCCESS") percent = 100;

        // 5) Live events (din logStore) - ACUM VA FUNCȚIONA!
        var eventsOut = new List<string>();
        if (!string.IsNullOrEmpty(buildId) && logStore.TryGetValue(buildId, out var q))
        {
            eventsOut = q.ToList();
            Console.WriteLine($"[DEBUG] Found {eventsOut.Count} events for buildId: {buildId}");
        }
        else
        {
            Console.WriteLine($"[DEBUG] No events found for buildId: {buildId ?? "(null)"}");
        }

        // cleanup memory when finished
        if (done && !string.IsNullOrEmpty(buildId))
        {
            stepStore.TryRemove(buildId, out _);
            logStore.TryRemove(buildId, out _);
        }

        return Results.Ok(new
        {
            ok = true,
            done,
            state,
            percent,
            logs = logUrl,
            steps = stepsOut,
            events = eventsOut,
            buildId = buildId // <-- ADAUGĂ PENTRU DEBUG
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

// --- /logs: citește logurile Cloud Build din Cloud Logging (live/paginate) ---
// GET /logs?operation=...&pageToken=...&since=2025-10-27T15:00:00Z
app.MapGet("/logs", async (HttpContext http, string operation, string? pageToken, string? since) =>
{
    try
    {
        http.Response.Headers.CacheControl = "no-store";

        static string NormalizeOp(string op, string projectId, string region)
            => op.StartsWith("projects/", StringComparison.OrdinalIgnoreCase) || op.StartsWith("operations/", StringComparison.OrdinalIgnoreCase)
               ? op
               : $"projects/{projectId}/locations/{(string.IsNullOrWhiteSpace(region) ? "global" : region)}/operations/{op}";

        var opPath = NormalizeOp(operation, projectId, region);

        string? buildId = null;
        if (opPath.StartsWith("operations/build/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = opPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4) buildId = parts[^1];
        }

        if (string.IsNullOrEmpty(buildId))
        {
            var cred0 = await GoogleCredential.GetApplicationDefaultAsync();
            var scoped0 = cred0.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
            var token0 = await scoped0.UnderlyingCredential.GetAccessTokenForRequestAsync();

            using var hc0 = new HttpClient();
            hc0.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token0);
            var getUrl = $"https://cloudbuild.googleapis.com/v1/{opPath}";
            var r0 = await hc0.GetAsync(getUrl);
            var t0 = await r0.Content.ReadAsStringAsync();
            if (!r0.IsSuccessStatusCode) return Results.Problem(t0, statusCode: (int)r0.StatusCode);

            using var d0 = JsonDocument.Parse(t0);
            if (d0.RootElement.TryGetProperty("metadata", out var meta) &&
                meta.TryGetProperty("build", out var build) &&
                build.TryGetProperty("id", out var idEl))
            {
                buildId = idEl.GetString();
            }
        }

        if (string.IsNullOrEmpty(buildId))
            return Results.BadRequest(new { ok = false, error = "cannot resolve buildId" });

        var cred = await GoogleCredential.GetApplicationDefaultAsync();
        var scoped = cred.CreateScoped(new[]
        {
            "https://www.googleapis.com/auth/cloud-platform",
            "https://www.googleapis.com/auth/logging.read"
        });
        var token = await scoped.UnderlyingCredential.GetAccessTokenForRequestAsync();

        var filter = $"resource.type=\"build\" AND labels.build_id=\"{buildId}\"";
        if (!string.IsNullOrWhiteSpace(since) && DateTimeOffset.TryParse(since, out var sinceTs))
        {
            var iso = sinceTs.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            filter += $" AND timestamp>=\"{iso}\"";
        }

        var body = new
        {
            resourceNames = new[] { $"projects/{projectId}" },
            filter,
            orderBy = "timestamp asc",
            pageSize = 500,
            pageToken = pageToken
        };

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var json = JsonSerializer.Serialize(body);
        var resp = await httpClient.PostAsync(
            "https://logging.googleapis.com/v2/entries:list",
            new StringContent(json, Encoding.UTF8, "application/json"));

        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return Results.Problem(text, statusCode: (int)resp.StatusCode);

        using var doc = JsonDocument.Parse(text);
        var arr = new List<object>();
        string? next = null;

        if (doc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entries.EnumerateArray())
            {
                string ts =
                    e.TryGetProperty("timestamp", out var tsEl) ? tsEl.GetString() ?? "" : "";
                string line =
                    e.TryGetProperty("textPayload", out var tp) ? tp.GetString() ?? "" :
                    e.TryGetProperty("jsonPayload", out var jp) && jp.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" :
                    e.TryGetProperty("protoPayload", out var pp) && pp.TryGetProperty("@type", out var _) ? pp.ToString() :
                    e.ToString();

                foreach (var ln in (line ?? "").Replace("\r\n","\n").Split('\n'))
                {
                    if (!string.IsNullOrEmpty(ln))
                        arr.Add(new { ts, line = ln });
                }
            }
        }
        if (doc.RootElement.TryGetProperty("nextPageToken", out var npt))
            next = npt.GetString();

        return Results.Ok(new
        {
            ok = true,
            buildId,
            nextPageToken = next,
            entries = arr
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

app.Run();

// DTO for /run
public record RunRequest(string Targets, string Branch);