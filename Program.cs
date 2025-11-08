// Program.cs - Extended with Firestore platform management
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
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

// === Firestore Setup ===
Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", projectId);
FirestoreDb db = FirestoreDb.Create(projectId);

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
var logStore  = new ConcurrentDictionary<string, ConcurrentQueue<string>>();

static void EnqueueLog(ConcurrentDictionary<string, ConcurrentQueue<string>> store, string buildId, string line)
{
    var q = store.GetOrAdd(buildId, _ => new ConcurrentQueue<string>());
    q.Enqueue($"{DateTime.UtcNow:HH:mm:ss}  {line}");
    while (q.Count > 200 && q.TryDequeue(out _)) { }
}

string[] stepOrder = new[] { "frontend", "backend", "gitea", "confluence", "jira", "artifactory", "github" };
string[] requiredApps = new[] { "frontend", "backend" }; // Always required

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

// ===================================
// PLATFORM MANAGEMENT ENDPOINTS
// ===================================

// GET /platform - Get current platform state
app.MapGet("/platform", async () =>
{
    try
    {
        var docRef = db.Collection("platforms").Document("demo-platform");
        var snapshot = await docRef.GetSnapshotAsync();
        
        if (!snapshot.Exists)
        {
            return Results.Ok(new
            {
                id = "demo-platform",
                deployed_apps = Array.Empty<string>(),
                created_at = (DateTime?)null,
                last_modified = (DateTime?)null,
                status = "not_deployed"
            });
        }

        var data = snapshot.ToDictionary();
        return Results.Ok(new
        {
            id = snapshot.Id,
            deployed_apps = data.ContainsKey("deployed_apps") ? ((List<object>)data["deployed_apps"]).Select(x => x.ToString()).ToArray() : Array.Empty<string>(),
            created_at = data.ContainsKey("created_at") ? ((Timestamp)data["created_at"]).ToDateTime() : (DateTime?)null,
            last_modified = data.ContainsKey("last_modified") ? ((Timestamp)data["last_modified"]).ToDateTime() : (DateTime?)null,
            status = data.ContainsKey("status") ? data["status"].ToString() : "unknown"
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

// POST /platform/deploy - Full platform deployment
app.MapPost("/platform/deploy", async (DeployPlatformRequest req) =>
{
    try
    {
        var apps = req.Apps.Select(a => a.ToLowerInvariant()).ToList();
        if (!apps.Contains("frontend") || !apps.Contains("backend"))
        {
            return Results.BadRequest(new { ok = false, error = "Frontend and Backend are required" });
        }

        var docRef = db.Collection("platforms").Document("demo-platform");
        await docRef.SetAsync(new Dictionary<string, object>
        {
            ["deployed_apps"] = apps,
            ["created_at"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["last_modified"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["status"] = "deploying"
        });

        var result = await TriggerCloudBuild(projectId, triggerId, region, progressUrl, 
            req.Branch ?? "main", 
            string.Join(",", apps), 
            "deploy_platform",
            req.Namespace,
            req.UserEmail);

        if (!result.Success)
            return Results.Problem(result.Error, statusCode: 500);

        return Results.Ok(new { ok = true, operation = result.Operation, action = "deploy_platform" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

// POST /platform/add - Add apps to existing platform
app.MapPost("/platform/add", async (ModifyPlatformRequest req) =>
{
    try
    {
        var docRef = db.Collection("platforms").Document("demo-platform");
        var snapshot = await docRef.GetSnapshotAsync();
        
        if (!snapshot.Exists)
        {
            return Results.BadRequest(new { ok = false, error = "Platform not deployed yet. Use 'Deploy Platform' first." });
        }

        var data = snapshot.ToDictionary();
        var currentApps = ((List<object>)data["deployed_apps"]).Select(x => x.ToString()!.ToLowerInvariant()).ToList();
        
        var appsToAdd = req.Apps.Select(a => a.ToLowerInvariant()).Where(a => !currentApps.Contains(a)).ToList();
        
        if (appsToAdd.Count == 0)
        {
            return Results.BadRequest(new { ok = false, error = "All selected apps are already deployed" });
        }

        currentApps.AddRange(appsToAdd);

        await docRef.UpdateAsync(new Dictionary<string, object>
        {
            ["deployed_apps"] = currentApps,
            ["last_modified"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["status"] = "updating"
        });

        var result = await TriggerCloudBuild(projectId, triggerId, region, progressUrl,
            req.Branch ?? "main",
            string.Join(",", appsToAdd),
            "add_app");

        if (!result.Success)
            return Results.Problem(result.Error, statusCode: 500);

        return Results.Ok(new { ok = true, operation = result.Operation, action = "add_app", added = appsToAdd });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

// POST /platform/remove - Remove apps from platform
app.MapPost("/platform/remove", async (ModifyPlatformRequest req) =>
{
    try
    {
        var docRef = db.Collection("platforms").Document("demo-platform");
        var snapshot = await docRef.GetSnapshotAsync();
        
        if (!snapshot.Exists)
        {
            return Results.BadRequest(new { ok = false, error = "Platform not deployed" });
        }

        var data = snapshot.ToDictionary();
        var currentApps = ((List<object>)data["deployed_apps"]).Select(x => x.ToString()!.ToLowerInvariant()).ToList();
        
        var appsToRemove = req.Apps.Select(a => a.ToLowerInvariant()).ToList();
        var blockedApps = appsToRemove.Where(a => requiredApps.Contains(a)).ToList();
        
        if (blockedApps.Any())
        {
            return Results.BadRequest(new { ok = false, error = $"Cannot remove required apps: {string.Join(", ", blockedApps)}" });
        }

        var validRemoves = appsToRemove.Where(a => currentApps.Contains(a)).ToList();
        
        if (validRemoves.Count == 0)
        {
            return Results.BadRequest(new { ok = false, error = "None of the selected apps are currently deployed" });
        }

        currentApps.RemoveAll(a => validRemoves.Contains(a));

        await docRef.UpdateAsync(new Dictionary<string, object>
        {
            ["deployed_apps"] = currentApps,
            ["last_modified"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["status"] = "updating"
        });

        var result = await TriggerCloudBuild(projectId, triggerId, region, progressUrl,
            req.Branch ?? "main",
            string.Join(",", validRemoves),
            "remove_app");

        if (!result.Success)
            return Results.Problem(result.Error, statusCode: 500);

        return Results.Ok(new { ok = true, operation = result.Operation, action = "remove_app", removed = validRemoves });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

// POST /platform/delete - Delete entire platform
app.MapPost("/platform/delete", async (DeletePlatformRequest req) =>
{
    try
    {
        var docRef = db.Collection("platforms").Document("demo-platform");
        var snapshot = await docRef.GetSnapshotAsync();
        
        if (!snapshot.Exists)
        {
            return Results.BadRequest(new { ok = false, error = "Platform not deployed" });
        }

        var data = snapshot.ToDictionary();
        var allApps = ((List<object>)data["deployed_apps"]).Select(x => x.ToString()!.ToLowerInvariant()).ToList();

        // Trigger Cloud Build to delete all apps
        var result = await TriggerCloudBuild(projectId, triggerId, region, progressUrl,
            req.Branch ?? "main",
            string.Join(",", allApps),
            "delete_platform");

        if (!result.Success)
            return Results.Problem(result.Error, statusCode: 500);

        // Mark platform as deleting (will be fully deleted when build completes)
        await docRef.UpdateAsync(new Dictionary<string, object>
        {
            ["status"] = "deleting",
            ["last_modified"] = Timestamp.FromDateTime(DateTime.UtcNow)
        });

        return Results.Ok(new { ok = true, operation = result.Operation, action = "delete_platform" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

// Helper function
static async Task<(bool Success, string? Operation, string? Error)> TriggerCloudBuild(
    string projectId, string triggerId, string region, string progressUrl,
    string branch, string targets, string action, string? namespaceParam = null, string? userEmail = null)
{
    try
    {
        var credential = await GoogleCredential.GetApplicationDefaultAsync();
        var scoped = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        var token = await scoped.UnderlyingCredential.GetAccessTokenForRequestAsync();

        var url = $"https://cloudbuild.googleapis.com/v1/projects/{projectId}/locations/{region}/triggers/{triggerId}:run";

        var subs = new Dictionary<string, string?>
        {
            ["_TARGETS"] = targets,
            ["_ACTION"] = action
        };
        if (!string.IsNullOrWhiteSpace(progressUrl))
            subs["_PROGRESS_URL"] = progressUrl;
        if (!string.IsNullOrWhiteSpace(namespaceParam))
            subs["_NAMESPACE"] = namespaceParam;
        if (!string.IsNullOrWhiteSpace(userEmail))
            subs["_USER_EMAIL"] = userEmail;

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

        // Log the request for debugging
        var bodyJson = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("📤 Sending to Cloud Build API:");
        Console.WriteLine(bodyJson);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var json = JsonSerializer.Serialize(body);
        var resp = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"❌ Cloud Build API Error: {text}");
            return (false, null, text);
        }

        using var doc = JsonDocument.Parse(text);
        string? opName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;

        Console.WriteLine($"✅ Cloud Build started successfully. Operation: {opName}");
        return (true, opName, null);
    }
    catch (Exception ex)
    {
        return (false, null, ex.ToString());
    }
}

// --- /progress (callbacks from Cloud Build) ---
app.MapPost("/progress/progress", (HttpRequest req) =>
{
    var buildId = req.Query["op"].ToString();
    var step    = N(req.Query["step"].ToString());
    var status  = req.Query["status"].ToString();

    if (string.IsNullOrWhiteSpace(buildId) || string.IsNullOrWhiteSpace(step))
        return Results.BadRequest(new { ok = false, error = "missing op/step" });

    var map = stepStore.GetOrAdd(buildId, _ => new ConcurrentDictionary<string, string>());
    var incoming = NormalizeLive(status);
    map.AddOrUpdate(step, incoming, (_, prev) => Rank(incoming) >= Rank(prev) ? incoming : prev);

    EnqueueLog(logStore, buildId, $"[{step}] {(status ?? "START").ToUpperInvariant()}");

    return Results.Ok(new { ok = true });
});

app.MapMethods("/run", new[] { "OPTIONS" }, () => Results.Ok());
app.MapGet("/run", () => Results.Ok("run-get-ok"));

app.MapPost("/run", async (RunRequest req) =>
{
    if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(triggerId))
        return Results.BadRequest(new { ok = false, error = "Set PROJECT_ID and TRIGGER_ID" });

    var result = await TriggerCloudBuild(projectId, triggerId, region, progressUrl,
        req.Branch ?? "main",
        req.Targets ?? "frontend,backend",
        "deploy_platform");

    if (!result.Success)
        return Results.Problem(result.Error, statusCode: 500);

    return Results.Ok(new { ok = true, operation = result.Operation });
});

// GET /status
app.MapGet("/status", async (HttpContext http, string operation) =>
{
    try
    {
        http.Response.Headers.CacheControl = "no-store";

        var credential = await GoogleCredential.GetApplicationDefaultAsync();
        var scoped = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        var token = await scoped.UnderlyingCredential.GetAccessTokenForRequestAsync();

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
        string? buildId = null;

        var merged = new Dictionary<string, string>();

        if (root.TryGetProperty("metadata", out var metadata))
        {
            if (metadata.TryGetProperty("build", out var buildEl) &&
                buildEl.TryGetProperty("id", out var idEl))
            {
                buildId = idEl.GetString();
            }

            if (buildEl.ValueKind != JsonValueKind.Undefined)
            {
                if (buildEl.TryGetProperty("status", out var statusEl))
                    state = statusEl.GetString()?.ToUpperInvariant() ?? "UNKNOWN";

                if (buildEl.TryGetProperty("logUrl", out var logUrlEl))
                    logUrl = logUrlEl.GetString();

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

        var stepsOut = new List<object>();
        foreach (var id in stepOrder)
            if (merged.TryGetValue(id, out var stCanon))
                stepsOut.Add(new { id, status = stCanon });

        int total = stepsOut.Count;
        int finished = stepsOut.Count(x =>
        {
            var stCanon = ((dynamic)x).status?.ToString()?.ToUpperInvariant() ?? "";
            return stCanon is "SUCCESS" or "FAILURE" or "CANCELLED" or "INTERNAL_ERROR" or "TIMEOUT";
        });

        int percent = total > 0 ? (int)Math.Round((finished * 100.0) / Math.Max(1, total)) : 0;
        if (!done && percent >= 100) percent = 99;
        if (done && state == "SUCCESS") percent = 100;

        var eventsOut = new List<string>();
        if (!string.IsNullOrEmpty(buildId) && logStore.TryGetValue(buildId, out var q))
        {
            eventsOut = q.ToList();
        }

        if (done && !string.IsNullOrEmpty(buildId))
        {
            stepStore.TryRemove(buildId, out _);
            logStore.TryRemove(buildId, out _);
            
            if (state == "SUCCESS")
            {
                var docRef = db.Collection("platforms").Document("demo-platform");
                var snapshot = await docRef.GetSnapshotAsync();
                
                if (snapshot.Exists)
                {
                    var data = snapshot.ToDictionary();
                    var status = data.ContainsKey("status") ? data["status"].ToString() : "";
                    
                    // If status is "deleting", delete the entire document
                    if (status == "deleting")
                    {
                        await docRef.DeleteAsync();
                    }
                    else
                    {
                        // Otherwise just update status to "active"
                        await docRef.UpdateAsync(new Dictionary<string, object>
                        {
                            ["status"] = "active",
                            ["last_modified"] = Timestamp.FromDateTime(DateTime.UtcNow)
                        });
                    }
                }
            }
        }

        return Results.Ok(new
        {
            ok = true,
            done,
            state,
            percent,
            logs = logUrl,
            steps = stepsOut,
            events = eventsOut
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString(), statusCode: 500);
    }
});

app.Run();

public record RunRequest(string Targets, string Branch);
public record DeployPlatformRequest(string[] Apps, string? Branch, string? Namespace, string? UserEmail);
public record ModifyPlatformRequest(string[] Apps, string? Branch);
public record DeletePlatformRequest(string? Branch);