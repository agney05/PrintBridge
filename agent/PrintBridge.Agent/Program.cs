using System.Diagnostics;
using System.Drawing.Printing;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PrintBridge.Agent;

internal static class Program
{
    [STAThread]
    static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
}

record PairRequest(string id, string deviceName);
record PrintJob(string id, string filename, string mime, int size, int copies, string orientation, string status, long created);

sealed class MainForm : Form
{
    readonly TextBox serverBox = new() { Text = "http://localhost:3001", Width = 310 };
    readonly TextBox keyBox = new() { PlaceholderText = "Paste the AGENT_KEY from backend .env", Width = 310, UseSystemPasswordChar = true };
    readonly ComboBox printerBox = new() { Width = 310, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox autoPrint = new() { Text = "Print automatically (no local approval)", AutoSize = true };
    readonly CheckBox startWithWindows = new() { Text = "Start this window when I sign in", AutoSize = true };
    readonly Label codeLabel = new() { Text = "Pairing code: start agent to generate", AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold) };
    readonly Label stateLabel = new() { Text = "Stopped", AutoSize = true };
    readonly ListBox requestList = new() { Width = 390, Height = 100 };
    readonly ListBox jobList = new() { Width = 390, Height = 130 };
    readonly Button startButton = new() { Text = "Start agent", Width = 130, Height = 38 };
    readonly Button approveButton = new() { Text = "Approve selected phone", Width = 180, Height = 34 };
    readonly Button rejectButton = new() { Text = "Decline", Width = 90, Height = 34 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    bool running, busy;
    string? code;
    string WorkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintBridge", "jobs");
    string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintBridge", "agent.json");

    public MainForm()
    {
        Text = "PrintBridge Agent"; Width = 470; Height = 680; MinimumSize = new Size(470, 680); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 249, 247); Font = new Font("Segoe UI", 10); Padding = new Padding(18);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(5) };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "PrintBridge", Font = new Font("Segoe UI", 22, FontStyle.Bold), ForeColor = Color.FromArgb(26, 94, 82), AutoSize = true });
        layout.Controls.Add(new Label { Text = "Keep this window open while your phone sends print jobs.", AutoSize = true, ForeColor = Color.DimGray });
        AddSection(layout, "Backend address", serverBox); AddSection(layout, "Agent key", keyBox);
        var printers = PrinterSettings.InstalledPrinters.Cast<string>().ToArray(); printerBox.Items.AddRange(printers);
        var def = new PrinterSettings().PrinterName; if (printerBox.Items.Contains(def)) printerBox.SelectedItem = def; else if (printers.Length > 0) printerBox.SelectedIndex = 0;
        AddSection(layout, "Windows printer", printerBox);
        autoPrint.CheckedChanged += (_, _) => { if (autoPrint.Checked && MessageBox.Show("Automatic printing lets any approved phone send jobs straight to this printer. Only enable it for people you trust.", "Enable automatic printing?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) autoPrint.Checked = false; };
        layout.Controls.Add(autoPrint); layout.Controls.Add(startWithWindows); layout.Controls.Add(codeLabel); layout.Controls.Add(stateLabel); layout.Controls.Add(startButton);
        requestList.DisplayMember = "deviceName";
        layout.Controls.Add(new Label { Text = "Phones waiting for your approval", Font = new Font("Segoe UI", 11, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 12, 0, 4) }); layout.Controls.Add(requestList);
        var actions = new FlowLayoutPanel { Width = 400, Height = 42 }; actions.Controls.Add(approveButton); actions.Controls.Add(rejectButton); layout.Controls.Add(actions);
        layout.Controls.Add(new Label { Text = "Recent print jobs", Font = new Font("Segoe UI", 11, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 12, 0, 4) }); layout.Controls.Add(jobList);
        layout.Controls.Add(new Label { Text = "Files are deleted from this computer after printing or when the agent restarts. Automatic printing is shown above.", AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = Color.DimGray });
        startButton.Click += async (_, _) => { if (running) StopAgent(); else await StartAgent(); };
        approveButton.Click += async (_, _) => await PairAction("approve"); rejectButton.Click += async (_, _) => await PairAction("reject");
        startWithWindows.CheckedChanged += (_, _) => SetStartup(startWithWindows.Checked);
        printerBox.SelectedIndexChanged += async (_, _) => { if (running) await Heartbeat(); };
        timer.Tick += async (_, _) => { await Heartbeat(); await Poll(); }; LoadSettings(); FormClosing += (_, _) => SaveSettings();
        Shown += async (_, _) => { if (startWithWindows.Checked) await StartAgent(); };
    }

    void AddSection(Control host, string title, Control control) { host.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), Margin = new Padding(0, 10, 0, 3) }); host.Controls.Add(control); }
    void SetStartup(bool enabled) { try { using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (enabled) run.SetValue("PrintBridgeAgent", $"\"{Application.ExecutablePath}\""); else run.DeleteValue("PrintBridgeAgent", false); } catch { MessageBox.Show("Windows could not update the sign-in startup setting."); } }
    void LoadSettings() { try { if (!File.Exists(SettingsFile)) return; using var d = JsonDocument.Parse(File.ReadAllText(SettingsFile)); var root = d.RootElement; if (root.TryGetProperty("server", out var s)) serverBox.Text = s.GetString() ?? serverBox.Text; if (root.TryGetProperty("printer", out var p) && printerBox.Items.Contains(p.GetString())) printerBox.SelectedItem = p.GetString(); if (root.TryGetProperty("agentKey", out var k)) keyBox.Text = k.GetString() ?? ""; using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); startWithWindows.Checked = run?.GetValue("PrintBridgeAgent") != null; } catch { } }
    void SaveSettings() { try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!); File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { server = serverBox.Text, printer = printerBox.Text, agentKey = keyBox.Text })); } catch { } }
    HttpRequestMessage Request(HttpMethod method, string route, object? body = null) { var m = new HttpRequestMessage(method, serverBox.Text.TrimEnd('/') + route); m.Headers.Add("X-Agent-Key", keyBox.Text); if (body != null) m.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"); return m; }
    async Task<JsonDocument> Send(HttpMethod method, string route, object? body = null) { using var req = Request(method, route, body); using var res = await http.SendAsync(req); var text = await res.Content.ReadAsStringAsync(); if (!res.IsSuccessStatusCode) throw new Exception(text); return JsonDocument.Parse(text); }
    async Task StartAgent()
    {
        if (keyBox.Text.Length < 32) { MessageBox.Show("Set the AGENT_KEY from the backend .env file. It must be at least 32 characters."); return; }
        if (printerBox.SelectedItem == null) { MessageBox.Show("Install a Windows printer before starting the agent."); return; }
        try { Directory.CreateDirectory(WorkDir); foreach (var old in Directory.GetFiles(WorkDir)) try { File.Delete(old); } catch { } using var d = await Send(HttpMethod.Post, "/api/agent/pairing-code"); code = d.RootElement.GetProperty("code").GetString(); using var _ = await Send(HttpMethod.Post, "/api/agent/printer", new { name = printerBox.Text, online = true }); running = true; startButton.Text = "Stop agent"; stateLabel.Text = $"Running · {printerBox.Text}"; codeLabel.Text = $"Pairing code: {code}  (expires in 5 minutes)"; timer.Start(); SaveSettings(); await Poll(); }
        catch { MessageBox.Show("Could not connect to the PrintBridge backend. Check its address, firewall, and agent key.", "Connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    void StopAgent() { running = false; timer.Stop(); _ = Send(HttpMethod.Post, "/api/agent/printer", new { name = printerBox.Text, online = false }); startButton.Text = "Start agent"; stateLabel.Text = "Stopped"; codeLabel.Text = "Pairing code: stopped"; }
    async Task Heartbeat() { if (!running) return; try { using var _ = await Send(HttpMethod.Post, "/api/agent/printer", new { name = printerBox.Text, online = true }); } catch { } }
    async Task Poll()
    {
        if (!running || busy) return;
        try
        {
            using var d = await Send(HttpMethod.Get, "/api/agent/pairing-requests"); requestList.Items.Clear();
            foreach (var e in d.RootElement.EnumerateArray()) requestList.Items.Add(new PairRequest(e.GetProperty("id").GetString()!, e.GetProperty("deviceName").GetString()!));
            using var jobs = await Send(HttpMethod.Get, "/api/agent/jobs"); var first = jobs.RootElement.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Undefined) { var job = JsonSerializer.Deserialize<PrintJob>(first.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); if (job != null) await HandleJob(job); }
        }
        catch (Exception ex) { stateLabel.Text = "Connection issue · retrying"; Debug.WriteLine(ex.Message); }
    }
    async Task PairAction(string action)
    {
        if (requestList.SelectedItem is not PairRequest p) { MessageBox.Show("Select a phone request first."); return; }
        try { using var _ = await Send(HttpMethod.Post, $"/api/agent/pairing-requests/{p.id}/{action}"); requestList.Items.Remove(p); }
        catch { MessageBox.Show("Could not update this pairing request. Try again."); }
    }
    async Task HandleJob(PrintJob job)
    {
        busy = true; string local = Path.Combine(WorkDir, Guid.NewGuid() + Path.GetExtension(job.filename));
        try
        {
            Directory.CreateDirectory(WorkDir);
            using (var req = Request(HttpMethod.Get, $"/api/agent/jobs/{job.id}/file")) using (var res = await http.SendAsync(req)) { res.EnsureSuccessStatusCode(); await using var fs = File.Create(local); await res.Content.CopyToAsync(fs); }
            using var claim = await Send(HttpMethod.Post, $"/api/agent/jobs/{job.id}/claim"); if (!claim.RootElement.GetProperty("claimed").GetBoolean()) { File.Delete(local); return; }
            jobList.Items.Insert(0, $"{DateTime.Now:t} · {job.filename} · approved for printing");
            if (!autoPrint.Checked && MessageBox.Show($"Print this document?\n\n{job.filename}\nPrinter: {printerBox.Text}\nCopies: {job.copies}", "Confirm printing", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { await SetJob(job.id, "cancelled", "Declined on the Windows computer."); return; }
            await Print(local, job);
            await SetJob(job.id, "submitted", "Windows accepted the job for printing. Physical completion cannot be confirmed.");
            jobList.Items[0] = $"{DateTime.Now:t} · {job.filename} · submitted to printer";
        }
        catch (Exception ex) { await SetJob(job.id, "failed", "The document could not be sent to the printer. Check the printer and required PDF/Office tools."); jobList.Items.Insert(0, $"{DateTime.Now:t} · {job.filename} · failed"); Debug.WriteLine(ex); }
        finally { try { if (File.Exists(local)) File.Delete(local); } catch { } busy = false; }
    }
    async Task SetJob(string id, string status, string error) { try { using var _ = await Send(HttpMethod.Post, $"/api/agent/jobs/{id}/status", new { status, error }); } catch { } }
    async Task Print(string input, PrintJob job)
    {
        string ext = Path.GetExtension(job.filename).ToLowerInvariant(); string document = input;
        if (ext == ".docx")
        {
            var soffice = FindExecutable("soffice.exe"); if (soffice == null) throw new Exception("LibreOffice is not installed.");
            string outDir = Path.GetDirectoryName(input)!; await Run(soffice, $"--headless --convert-to pdf --outdir \"{outDir}\" \"{input}\""); document = Path.ChangeExtension(input, ".pdf"); if (!File.Exists(document)) throw new Exception("DOCX conversion failed.");
        }
        if (ext is ".pdf" or ".docx")
        {
            var sumatra = FindExecutable("SumatraPDF.exe"); if (sumatra == null) throw new Exception("Install SumatraPDF to print PDF and Word documents.");
            string settings = $"{job.copies}x,{job.orientation},fit";
            await Run(sumatra, $"-print-to \"{printerBox.Text}\" -print-settings \"{settings}\" -silent \"{document}\""); return;
        }
        if (ext is ".jpg" or ".jpeg" or ".png") { PrintImage(document, job); return; }
        if (ext == ".txt") { PrintText(document, job); return; }
        throw new Exception("Unsupported document.");
    }
    static string? FindExecutable(string name)
    {
        var dirs = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SumatraPDF"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SumatraPDF"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program") };
        foreach (var d in dirs) { var p = Path.Combine(d, name); if (File.Exists(p)) return p; }
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists); return path;
    }
    static async Task Run(string file, string args) { using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true }) ?? throw new Exception("Unable to start printer tool."); await p.WaitForExitAsync(); if (p.ExitCode != 0) throw new Exception("Printer tool returned an error."); }
    void PrintImage(string file, PrintJob job)
    {
        using var image = Image.FromFile(file); using var doc = new PrintDocument(); doc.PrinterSettings.PrinterName = printerBox.Text; doc.PrinterSettings.Copies = (short)job.copies; doc.DefaultPageSettings.Landscape = job.orientation == "landscape";
        doc.PrintPage += (_, e) => { if (e.Graphics == null) return; var bounds = e.MarginBounds; float scale = Math.Min((float)bounds.Width / image.Width, (float)bounds.Height / image.Height); int w = (int)(image.Width * scale), h = (int)(image.Height * scale); e.Graphics.DrawImage(image, bounds.Left + (bounds.Width - w) / 2, bounds.Top + (bounds.Height - h) / 2, w, h); };
        doc.Print();
    }
    void PrintText(string file, PrintJob job)
    {
        string text = File.ReadAllText(file); int at = 0; using var doc = new PrintDocument(); doc.PrinterSettings.PrinterName = printerBox.Text; doc.PrinterSettings.Copies = (short)job.copies; doc.DefaultPageSettings.Landscape = job.orientation == "landscape";
        doc.PrintPage += (_, e) => { if (e.Graphics == null) return; using var font = new Font("Arial", 10); var area = e.MarginBounds; int end = text.Length; while (end > at && e.Graphics.MeasureString(text[at..end], font, area.Size).Height > area.Height) end = at + (end - at) / 2; while (end < text.Length && end > at && text[end - 1] != '\n' && text[end] != ' ') end++; if (end <= at) end = Math.Min(at + 1, text.Length); e.Graphics.DrawString(text[at..end], font, Brushes.Black, area); at = end; e.HasMorePages = at < text.Length; };
        doc.Print();
    }
}
