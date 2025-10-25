// Program.cs
using Google.Apis.Auth.OAuth2;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// === Config din appsettings.json sau ENV ===
var projectId = builder.Configuration["Gcp:ProjectId"]
               ?? Environment.GetEnvironmentVariable("PROJECT_ID")
               ?? "";
var triggerId = builder.Configuration["Gcp:TriggerId"]
               ?? Environment.GetEnvironmentVariable("TRIGGER_ID")
               ?? "";
var region    = builder.Configuration["Gcp:Region"]
               ?? Environment.GetEnvironmentVariable("REGION")
               ?? "global"; // triggerul tău e global

var progressUrl = builder.Configuration["Gcp:ProgressUrl"]
               ?? Environment.GetEnvironmentVariable("PROGRESS_URL")
               ?? ""; // dacă e gol, nu trimitem _PROGRESS_URL

builder.Services.AddCors(opt =>
{
    opt.AddPolicy("ui", p => p
        .WithOrigins("http://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod());
});
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();
app.UseCors("ui");

// === Store in-memory pt. status live pe pași (populate de /progress) ===
var stepStore = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();
// key: buildId ; val: stepId -> status ("START" | "DONE" | "FAIL")

// --- Health ---
app.MapGet("/healthz", () => Results.Ok("ok"));

// --- Rute pt. debugging ---
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

// --- Callback din Cloud Build pentru progres pe pași ---
// ex: POST /progress?op=$BUILD_ID&step=frontend&status=START|DONE|FAIL
app.MapPost("/progress", (HttpRequest req) =>
{
    var buildId = req.Query["op"].ToString();
    var step    = req.Query["step"].ToString();
    var status  = req.Query["status"].ToString();

    if (string.IsNullOrWhiteSpace(buildId) || string.IsNullOrWhiteSpace(step))
        return Results.BadRequest(new { ok = false, error = "missing op/step" });

    var map = stepStore.GetOrAdd(buildId, _ => new ConcurrentDictionary<string, string>());
    map[step.ToLowerInvariant()] = (status ?? "START").ToUpperInvariant();

    return Results.Ok(new { ok = true });
});

// --- Preflight CORS ---
app.MapMethods("/run", new[] { "OPTIONS" }, () => Results.Ok());

// --- GET /run (diagnostic) ---
app.MapGet("/run", () => Results.Ok("run-get-ok"));

// --- POST /run — pornește triggerul Cloud Build ---
app.MapPost("/run", async (RunRequest req) =>
{
    if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(triggerId))
        return Results.BadRequest(new { ok = false, error = "Set PROJECT_ID și TRIGGER_ID (env sau appsettings.json)" });

    try
    {
        var credential = await GoogleCredential.GetApplicationDefaultAsync();
        var scoped = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        var token = await scoped.UnderlyingCredential.GetAccessTokenForRequestAsync();

        var url = $"https://cloudbuild.googleapis.com/v1/projects/{projectId}/locations/{region}/triggers/{triggerId}:run";

        var branch = string.IsNullOrWhiteSpace(req.Branch) ? "main" : req.Branch;

        // substitutions (inclusiv PROGRESS_URL dacă e setat)
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

        if (!resp.IsSuccessStatusCode)
            return Results.Problem(text, statusCode: (int)resp.StatusCode);

        using var doc = JsonDocument.Parse(text);
        string? opName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;

        return Results.Ok(new { ok = true, operation = opName });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

// --- GET /status?operation=... ---
app.MapGet("/status", async (string operation) =>
{
    try
    {
        var credential = await GoogleCredential.GetApplicationDefaultAsync();
        var scoped = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        var token = await scoped.UnderlyingCredential.GetAccessTokenForRequestAsync();

        var op = operation.StartsWith("projects/")
            ? operation
            : $"projects/{projectId}/locations/{(string.IsNullOrWhiteSpace(region) ? "global" : region)}/operations/{operation}";

        var url = $"https://cloudbuild.googleapis.com/v1/{op}";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            return Results.Problem(text, statusCode: (int)resp.StatusCode);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        bool done = root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean();
        string state = "UNKNOWN";
        string? logUrl = null;
        string? buildId = null;

        int total = 0, finished = 0;
        var stepsOut = new List<object>();

        if (root.TryGetProperty("metadata", out var meta) &&
            meta.TryGetProperty("build", out var build))
        {
            if (build.TryGetProperty("status", out var st))
                state = st.GetString() ?? "UNKNOWN";

            if (build.TryGetProperty("logUrl", out var lu))
                logUrl = lu.GetString();

            if (build.TryGetProperty("id", out var idEl))
                buildId = idEl.GetString();

            // 1) Pașii raportați de Cloud Build (de obicei disponibili la final)
            if (build.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                total = steps.GetArrayLength();
                foreach (var s in steps.EnumerateArray())
                {
                    string id = s.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(id))
                        id = s.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";

                    string stepStatus = s.TryGetProperty("status", out var ss)
                        ? ss.GetString() ?? "UNKNOWN"
                        : "UNKNOWN";

                    bool isDone = stepStatus is "SUCCESS" or "FAILURE" or "CANCELLED" or "INTERNAL_ERROR" or "TIMEOUT";
                    if (isDone) finished++;

                    stepsOut.Add(new { id, status = stepStatus });
                }
            }

            // 2) Suprascriem/îmbogățim cu ce vine live prin /progress
            if (!string.IsNullOrEmpty(buildId) && stepStore.TryGetValue(buildId, out var liveMap))
            {
                var live = liveMap.Select(kv => new { id = kv.Key, status = kv.Value }).ToList();

                if (stepsOut.Count == 0)
                {
                    stepsOut.AddRange(live);
                }
                else
                {
                    var dict = stepsOut.ToDictionary(
                        x => ((dynamic)x).id?.ToString()?.ToLowerInvariant() ?? "",
                        x => x
                    );
                    foreach (var l in live)
                        dict[l.id.ToLowerInvariant()] = l;

                    stepsOut = dict.Values.ToList();
                }

                // recalculăm total/finished după live
                var doneSet = new HashSet<string> { "SUCCESS", "DONE", "FAIL", "FAILURE", "CANCELLED", "INTERNAL_ERROR", "TIMEOUT" };
                total = stepsOut.Count;
                finished = stepsOut.Count(x => doneSet.Contains(((dynamic)x).status?.ToString()?.ToUpperInvariant() ?? ""));
            }
        }

        // procent (fallback dacă n-avem pași)
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

        return Results.Ok(new
        {
            ok = true,
            done,
            state,
            percent,
            logs = logUrl,
            steps = stepsOut
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

app.Run();

// DTO pt. /run
public record RunRequest(string Targets, string Branch);
