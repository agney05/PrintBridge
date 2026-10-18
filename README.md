# PrintBridge

PrintBridge is a local network print bridge. A phone browser sends a document to the Node service running on the Windows computer. The Windows Agent polls that service, asks for approval, and sends supported documents to the installed Windows printer.

## What is implemented

- Mobile first React and TypeScript website with phone pairing, image/PDF preview, copies, orientation, upload, and job status.
- Express and SQLite API with six digit, five minute pairing codes, approval on Windows, one time device tokens, rate limits, private file storage, content checks, job ownership, cancellation before agent pickup, and retention cleanup.
- .NET 8 Windows desktop agent. Confirm before print is the default. It enumerates installed printers, supports a local approve/decline queue, remembers settings, and has an explicitly enabled automatic print mode.
- Real Windows printing: images and text use `PrintDocument`; PDFs use SumatraPDF; DOCX files use LibreOffice conversion followed by SumatraPDF. The latter tools are optional and must be installed on the Windows computer.
- Automated backend validation tests (`npm test`).

This is a functional MVP, not a signed consumer installer or hosted production service. It has not been exercised against your specific Windows printer or mobile devices in this environment. Windows printing, driver behavior, and optional renderer installations must be verified on the target computer.

## Architecture

```text
Phone browser ── HTTP on trusted Wi-Fi ──> Vite / hosted web frontend
                                             │ /api proxy in development
                                             ▼
                                      Express + SQLite
                                             ▲
                                authenticated polling over HTTP(S)
                                             │
                                  Windows PrintBridge Agent
                                             │
                                       Windows spooler
                                             ▼
                                         Printer
```

The backend creates a short lived code. The user enters it on the phone, then approves the phone locally in the Windows Agent. A random 256 bit phone token is issued after approval. The Agent authenticates separately with `AGENT_KEY`. Jobs are never made public. For local development the phone and computer must share a trusted Wi-Fi network. The code and job endpoints are not intended to be exposed directly to the public Internet.

The job state `submitted` means the Windows printing command/API accepted the job. It does **not** prove that paper physically came out. The OS/driver does not provide a consistent confirmation across printer models. PrintBridge therefore does not claim physical completion. On service restart, in-flight jobs become failed with a check-before-retry message to avoid duplicate copies.

## Requirements

- Node.js 22.13 or later and npm (the backend uses Node's built-in SQLite module).
- Windows 10/11 and .NET 8 SDK to build the Windows Agent.
- A printer installed in Windows and able to print from Windows first.
- SumatraPDF installed for PDF and DOCX printing.
- LibreOffice installed for DOCX conversion.

## Run locally

1. Open PowerShell in the `PrintBridge` directory.
2. Install JavaScript dependencies and create a local configuration:

   ```powershell
   npm install
   Copy-Item .env.example .env
   ```

3. Edit `.env`. Set `AGENT_KEY` to a private random string of at least 32 characters. Keep it secret. Set `WEB_ORIGIN` to the exact browser origin used below (for example `http://192.168.1.20:5173`). The API reads `.env` from the project root when started through the workspace scripts.
4. Start both services:

   ```powershell
   npm run dev
   ```

5. Find the Windows computer's Wi-Fi IPv4 address with `ipconfig`. On the phone, open `http://COMPUTER-IP:5173`. Both devices must be on the same trusted Wi-Fi. Allow Node/Vite through Windows Firewall only on the Private network profile. Do not port-forward these development ports to the Internet.
6. Build and launch the Windows Agent:

   ```powershell
   dotnet build .\agent\PrintBridge.Agent\PrintBridge.Agent.csproj -c Release
   dotnet run --project .\agent\PrintBridge.Agent\PrintBridge.Agent.csproj
   ```

   The Agent address is `http://localhost:3001` on the same computer. Paste the same `AGENT_KEY` from `.env` into the Agent. Choose the Windows printer and click **Start agent**.
7. Enter the Agent's six digit code on the phone. In the Agent, select the phone and click **Approve selected phone**. Select a PDF or image on the phone, set copies/orientation, and submit. Confirm the print on Windows.

### Phone connection note

The Vite development server must be reachable from the phone. If the Windows firewall blocks it, add a Private-network inbound rule for the Vite development port (5173). Do not allow public-network access. This HTTP development setup is suitable only for a trusted local network; modern mobile browsers may restrict camera APIs on HTTP. Manual code entry works without camera permission.

## Build the frontend and API

```powershell
npm run build
```

The frontend build is in `frontend/dist`; the API build is in `backend/dist`. For local use, the Vite dev server provides the `/api` proxy. A production deployment must serve the frontend and API behind one HTTPS reverse proxy, route `/api` to the backend, and configure `WEB_ORIGIN` to the exact HTTPS origin. Set `DATA_DIR` and `UPLOAD_DIR` to private directories outside the web root. Use a long random `AGENT_KEY`; do not commit `.env`.

For hosted use, deploy the backend on a private service and expose it only through a TLS terminating reverse proxy. The Agent should use the HTTPS backend URL. Use network access controls and a managed secret store for the agent credential. Back up SQLite securely; uploaded documents are removed after the configured retention window. Do not expose port 3001 or the Agent key to arbitrary clients. A public hosted deployment needs operational hardening, monitoring, backups, and an installer/signing process before broad use.

## Windows Agent packaging

Build a self contained x64 executable on Windows:

```powershell
dotnet publish .\agent\PrintBridge.Agent\PrintBridge.Agent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The output is under `agent\PrintBridge.Agent\bin\Release\net8.0-windows\win-x64\publish`. This repo does not yet include a signed MSI/installer. The Agent stores its server, printer, and shared key in the current user's `%LOCALAPPDATA%\PrintBridge\agent.json`; protect that Windows account. The optional **Start this window when I sign in** control adds or removes a per-user Windows Run entry. The temporary document folder is `%LOCALAPPDATA%\PrintBridge\jobs` and is cleaned as jobs finish. Add an installer and code signing before distributing the application widely.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | `3001` | API listener port |
| `WEB_ORIGIN` | `http://localhost:5173` | Allowed web origin(s), comma separated |
| `AGENT_KEY` | required | Secret shared by this backend and Windows Agent (32+ characters) |
| `DATA_DIR` | `./data` | SQLite database directory |
| `UPLOAD_DIR` | `./uploads` | Private temporary document storage |
| `MAX_FILE_MB` | `25` | Maximum accepted upload size |
| `RETENTION_HOURS` | `24` | Upper bound for stored jobs and documents |

## API overview

Phone endpoints require a `Bearer` device token except health and initial pairing. Agent endpoints require `X-Agent-Key`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/health` | Health check |
| POST | `/api/agent/pairing-code` | Create a five minute code |
| POST | `/api/agent/printer` | Publish the selected Windows printer and heartbeat |
| POST | `/api/pairing/request` | Phone requests pairing with six digits |
| GET | `/api/agent/pairing-requests` | Agent checks approval requests |
| POST | `/api/agent/pairing-requests/:id/approve` | Approve phone locally |
| POST | `/api/agent/pairing-requests/:id/reject` | Decline phone locally |
| POST | `/api/pairing/verify` | Phone receives its device token after approval |
| DELETE | `/api/devices/me` | Revoke this phone token |
| POST | `/api/jobs` | Authenticated multipart upload with `file`, `copies`, `orientation` |
| GET | `/api/jobs` / `/api/jobs/:id` | List and inspect this phone's jobs |
| GET | `/api/printers` | Show the agent-selected printer and connection state |
| POST | `/api/jobs/:id/cancel` | Cancel while still queued |
| GET | `/api/agent/jobs` | Agent polls available jobs |
| GET | `/api/agent/jobs/:id/file` | Agent downloads a queued document |
| POST | `/api/agent/jobs/:id/claim` | Agent atomically claims a job |
| POST | `/api/agent/jobs/:id/status` | Agent reports submitted, failed, or completed status |

## Supported files and print options

PDF, JPG/JPEG, PNG, TXT, and DOCX are accepted, up to the configured file limit. Server-side content sniffing supplements extension checks. DOCX must be a valid ZIP-based Office file. Conversion is performed by the installed LibreOffice executable; macros are not run. PDFs/DOCX use SumatraPDF. Image and text printing uses Windows GDI+ `PrintDocument`.

Current settings exposed to the phone are copies (1–5) and orientation. The printer, paper size, color, duplex, page range, and scaling settings are not surfaced yet; this MVP uses driver defaults except orientation and copies. Uploads are single-file, and the browser does not show transfer progress or offer mid-upload cancellation yet. The agent queue supports local approval, but pause/resume, clearing history, printer capability discovery, and a system tray UI are not included. Format and driver behavior differs across printers. The PDF preview uses the phone browser's built-in PDF viewer and is not a guarantee of printed fidelity. Camera capture is provided through the mobile file chooser where the browser supports it.

Automatic printing is off by default and can be enabled in the Windows Agent after a warning. Anyone with an approved paired phone can then initiate printing without local review; approve only people you trust and revoke phones you no longer recognize. This codebase uses a single configured agent key for the local installation; rotate it if the Windows profile or backend configuration is compromised.

## Tests and manual check

Run the automated validation tests:

```powershell
npm test
```

The tests cover pairing code deadline behavior, queued-only cancellation, and supported type/signature matching. Before relying on the system, use a real printer to verify: print a Windows test page; pair and approve a phone; upload a PDF and image; confirm one copy; check the job reports submission; disconnect the PC and try again; try a renamed or unsupported file; install LibreOffice and SumatraPDF and test DOCX; enable automatic printing only with a trusted paired phone. Verify printer settings with the actual driver.

## Troubleshooting

- **Phone cannot open the site:** use the computer's current Wi-Fi IP, check both devices are on the same private network, and allow the Vite port on Windows Private firewall only.
- **Pairing code rejected:** codes expire in five minutes and allow five attempts. Start the Agent again to show a fresh code.
- **Agent cannot connect:** check backend address (`http://localhost:3001` on this PC), `AGENT_KEY`, and API health at `http://localhost:3001/api/health`.
- **Printer absent:** install the printer and confirm a Windows test page works, then restart the Agent.
- **PDF/DOCX fails:** install SumatraPDF; for DOCX also install LibreOffice. Check the printer can print from a local Windows application.
- **Job says submitted but paper did not print:** check the Windows queue, printer status, paper and driver. The application cannot reliably detect physical output on all models.
- **Upload rejected:** verify extension and actual file type, file size, and that the document is not corrupt.
- **Agent restarted during a job:** check the printer before retrying. PrintBridge marks interrupted jobs as failed rather than replaying them and risking duplicate pages.

## Security checklist

- Keep the development server on a trusted private LAN; never port-forward its HTTP ports.
- Use HTTPS with a maintained reverse proxy for hosted deployment.
- Keep `.env`, the agent key, database, uploads, and agent settings private.
- Approve pairing requests only when the Windows user recognizes the phone.
- Leave automatic printing off unless all paired users are trusted.
- Revoke lost phones and rotate `AGENT_KEY` after suspected exposure.
- Keep Node, .NET, Windows, SumatraPDF, LibreOffice, printer drivers, and the reverse proxy patched.
- Set restrictive file retention and backups; do not log document contents.
- Protect the Windows login because the agent credential is stored in the user's profile.
