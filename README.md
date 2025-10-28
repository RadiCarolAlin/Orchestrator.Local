# Orchestrator.Local - Backend

Backend C# (ASP.NET Core) pentru orchestrarea deploy-urilor modular în Google Cloud Build.

## 📋 Descriere

Acest backend servește ca orchestrator pentru deploy-uri selective ale componentelor platformei:
- **Frontend** (UI principal)
- **Backend** (API principal)
- **Gitea** (Git server)
- **Confluence** (Wiki/Knowledge base)
- **Jira** (Issue tracker)

Backend-ul triggerează Cloud Build manual triggers și monitorizează progresul în timp real.

---

## 🚀 Cerințe

- **.NET 8.0 SDK** sau mai nou
- **Google Cloud Project** cu:
  - Cloud Build API enabled
  - Cloud Logging API enabled
  - Service Account cu permisiuni:
    - `roles/cloudbuild.builds.editor`
    - `roles/logging.viewer`
- **ngrok** (pentru a primi callbacks de la Cloud Build)

---

## ⚙️ Configurare

### 1. Clonează repository

```bash
git clone https://github.com/RadiCarolAlin/Orchestrator.Local.git
cd Orchestrator.Local
```

### 2. Configurează `appsettings.json`

```json
{
  "Gcp": {
    "ProjectId": "your-gcp-project-id",
    "TriggerId": "your-cloud-build-trigger-id",
    "Region": "global",
    "ProgressUrl": "https://your-ngrok-url.ngrok-free.dev"
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:8080" }
    }
  },
  "AllowedHosts": "*"
}
```

**Găsește Trigger ID:**
```bash
gcloud builds triggers list --region=global
```

### 3. Setup Google Cloud Authentication

```bash
gcloud auth application-default login
```

Acest command creează credențiale în `~/.config/gcloud/` pe care backend-ul le va folosi automat.

---

## 🌐 Setup ngrok (OBLIGATORIU pentru local development)

**Ce e ngrok?**  
ngrok este un **tunel** care expune localhost-ul tău pe internet. Cloud Build (care rulează în cloud) trebuie să trimită callback-uri la backend-ul tău local, dar nu poate accesa direct `localhost:8080`. ngrok creează un URL public (ex: `https://abc-123.ngrok-free.dev`) care forward-ează traficul la `localhost:8080`.

**Flow:**
```
Cloud Build (in cloud) 
    ↓
    POST https://abc-123.ngrok-free.dev/progress
    ↓
ngrok tunnel (eu region)
    ↓
    localhost:8080/progress
    ↓
Backend primește callback ✅
```

**Instalare ngrok:**
```bash
# macOS
brew install ngrok

# Linux  
wget https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-linux-amd64.tgz
tar -xvzf ngrok-v3-stable-linux-amd64.tgz
sudo mv ngrok /usr/local/bin/

# Windows
# Download de pe https://ngrok.com/download
```

**Autentificare ngrok (opțional dar recomandat):**
```bash
ngrok config add-authtoken YOUR_NGROK_TOKEN
# Token-ul îl obții gratis de pe https://dashboard.ngrok.com/
```

---

## 🏃 Rulare Locală - Pas cu Pas

### Terminal 1: Pornește Backend-ul

```bash
cd Orchestrator.Local

# Restore dependencies (prima dată)
dotnet restore

# Build
dotnet build

# Run
dotnet run

# Output:
# info: Microsoft.Hosting.Lifetime[14]
#       Now listening on: http://0.0.0.0:8080
# info: Microsoft.Hosting.Lifetime[0]
#       Application started. Press Ctrl+C to shut down.
```

✅ Backend pornit pe `http://localhost:8080`

---

### Terminal 2: Pornește ngrok

```bash
ngrok http http://localhost:8080 --region=eu --host-header=rewrite
```

**Parametri:**
- `http://localhost:8080` - forward către backend local
- `--region=eu` - folosește server-e din Europa (latență mai mică)
- `--host-header=rewrite` - modifică header-ul Host pentru compatibilitate

**Output ngrok:**
```
ngrok                                                                        

Session Status    online
Region            Europe (eu)
Latency           27ms
Web Interface     http://127.0.0.1:4040
Forwarding        https://maurita-unfaceted-prospectively.ngrok-free.dev -> http://localhost:8080

Connections       ttl     opn     rt1     rt5     p50     p90
                  0       0       0.00    0.00    0.00    0.00
```

✅ Copiază URL-ul de la **Forwarding** (ex: `https://maurita-unfaceted-prospectively.ngrok-free.dev`)

---

### Pas 3: Actualizează configurația

**Modifică `appsettings.json`:**

```json
{
  "Gcp": {
    "ProgressUrl": "https://maurita-unfaceted-prospectively.ngrok-free.dev"
  }
}
```

Apoi **restart backend** (Ctrl+C în Terminal 1, apoi `dotnet run` din nou).

---

### Pas 4: Testează

```bash
# Health check backend
curl http://localhost:8080/healthz

# Testează callback prin ngrok
curl -X POST "https://maurita-unfaceted-prospectively.ngrok-free.dev/progress?op=test-123&step=frontend&status=START"

# Ar trebui să vezi în Terminal 1 (backend logs):
# [INFO] Updated deployment state...
```

✅ Totul funcționează!

---

## 🎮 Workflow Complet

1. **Terminal 1:** `dotnet run` (backend pe 8080)
2. **Terminal 2:** `ngrok http http://localhost:8080 --region=eu --host-header=rewrite`
3. **Copiază URL ngrok** și pune-l în `appsettings.json` → `Gcp:ProgressUrl`
4. **Restart backend** (Ctrl+C și `dotnet run` din nou)
5. **Terminal 3:** `ng serve` (frontend pe 4200 - vezi README-ul frontend)
6. **Deschide browser:** `http://localhost:4200`
7. **Selectează apps** și apasă "Run selected"
8. **Urmărește în ngrok dashboard** (`http://127.0.0.1:4040`) - vei vedea request-urile POST /progress de la Cloud Build

---

## 📡 API Endpoints

### `GET /healthz`
Health check endpoint.

```bash
curl http://localhost:8080/healthz
# Response: "ok"
```

---

### `POST /run`
Triggerează un deploy selectiv.

```bash
curl -X POST http://localhost:8080/run \
  -H "Content-Type: application/json" \
  -d '{"Targets": "frontend,backend,gitea", "Branch": "main"}'

# Response:
# {"ok": true, "operation": "operations/build/gcp1042-bv42/abc-123"}
```

---

### `GET /status?operation={operationName}`
Obține statusul unui deploy în curs.

```bash
curl "http://localhost:8080/status?operation=operations/build/gcp1042-bv42/abc-123"
```

**Response:**
```json
{
  "ok": true,
  "done": false,
  "state": "WORKING",
  "percent": 45,
  "logs": "https://console.cloud.google.com/...",
  "steps": [
    {"id": "frontend", "status": "SUCCESS"},
    {"id": "backend", "status": "RUNNING"}
  ],
  "events": [
    "10:51:25  [frontend] START",
    "10:51:27  [frontend] DONE"
  ]
}
```

---

### `POST /progress`
Endpoint pentru callbacks de la Cloud Build (NU îl apelezi manual).

---

### `GET /logs?operation={operationName}`
Citește logurile Cloud Build.

```bash
curl "http://localhost:8080/logs?operation=operations/build/..."
```

---

## 🔧 Troubleshooting

### ❌ 401/403 Authentication Errors

**Soluție:**
```bash
gcloud auth application-default login
```

---

### ❌ Callbacks nu ajung la /progress

**Verifică:**
1. ngrok rulează în Terminal 2
2. URL-ul din `appsettings.json` match-ează cu cel din ngrok
3. `cloudbuild.yaml` are `_PROGRESS_URL` corect setat

**Debug:** Deschide `http://127.0.0.1:4040` (ngrok web interface) și verifică dacă apar request-uri POST.

---

### 🔍 ngrok Web Interface

ngrok oferă un dashboard local super util la `http://127.0.0.1:4040`:
- Vezi TOATE request-urile HTTP
- Poți **replay** request-uri
- Vezi headers, body, response
- Util pentru debugging callbacks de la Cloud Build!

---

## 📁 Structură Proiect

```
Orchestrator.Local/
├── Program.cs                    # Main application + API endpoints
├── appsettings.json              # Configuration (UPDATE ngrok URL aici!)
├── Orchestrator_Local.csproj
├── Orchestrator_Local.sln
└── README.md
```

---

## 💡 Tips & Tricks

### Tip: Alias pentru comenzi rapide

Adaugă în `~/.bashrc` sau `~/.zshrc`:

```bash
alias orch-backend="cd ~/path/to/Orchestrator.Local && dotnet run"
alias orch-ngrok="ngrok http http://localhost:8080 --region=eu --host-header=rewrite"
```

Apoi doar:
```bash
orch-backend    # Terminal 1
orch-ngrok      # Terminal 2
```

---

## 🔐 Security Notes

- **NU commit-a `appsettings.json`** cu valori reale în Git
- **Folosește `.gitignore`** pentru fișiere sensibile
- ngrok free expune public backend-ul - OK pentru development, NU pentru production!

---

## 📄 License

MIT License

---

## 📞 Support

Pentru probleme sau întrebări, deschide un issue pe GitHub.