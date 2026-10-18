using System.Diagnostics;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PrintBridge.Agent;

internal static class Program
{
    [STAThread]
    static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
}

record PairRequest(string id, string deviceName, long created);
record AgentDevice(string id, string name, long created);
record PrintJob(string id, string filename, string mime, int size, int copies, string orientation, string status, long created);

static class AgentSecret
{
    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    static byte[] Transform(byte[] bytes, bool protect)
    {
        var inputMemory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, inputMemory, bytes.Length);
            var input = new DataBlob { Size = bytes.Length, Data = inputMemory };
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, "PrintBridge Agent", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(inputMemory); }
    }
}

sealed class MainForm : Form
{
    const string HostedServer = "https://printbridge-agney05.onrender.com";
    readonly TextBox serverBox = new() { Text = HostedServer, Width = 280, Height = 38, BorderStyle = BorderStyle.FixedSingle };
    readonly ComboBox printerBox = new() { Width = 280, Height = 38, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    readonly CheckBox autoPrint = new() { Text = "Print automatically (no local approval)", AutoSize = true };
    readonly CheckBox startWithWindows = new() { Text = "Start this window when I sign in", AutoSize = true };
    readonly Label codeLabel = new() { Text = "— — —   — — —", AutoSize = true, Font = new Font("Segoe UI", 30, FontStyle.Bold), ForeColor = Color.FromArgb(20, 59, 54) };
    readonly Label codeExpiryLabel = new() { Text = "Start the Agent to create a temporary pairing code.", AutoSize = true, ForeColor = Color.FromArgb(98, 113, 110) };
    readonly Label stateLabel = new() { Text = "Offline", AutoSize = true, ForeColor = Color.FromArgb(194, 203, 200), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    readonly Label topStatusLabel = new() { Text = "OFFLINE", AutoSize = true, ForeColor = Color.FromArgb(98, 113, 110), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    readonly ListView requestList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Segoe UI", 10), HeaderStyle = ColumnHeaderStyle.Nonclickable };
    readonly ListView deviceList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Segoe UI", 10), HeaderStyle = ColumnHeaderStyle.Nonclickable };
    readonly ListBox jobList = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Segoe UI", 10) };
    readonly Button startButton = new() { Text = "Start Agent", Width = 280, Height = 46, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(39, 132, 110), ForeColor = Color.White, Font = new Font("Segoe UI", 11, FontStyle.Bold), Cursor = Cursors.Hand };
    readonly Button approveButton = new() { Text = "Approve phone", Width = 150, Height = 40, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(39, 132, 110), ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
    readonly Button rejectButton = new() { Text = "Decline", Width = 100, Height = 40, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(64, 79, 75), Font = new Font("Segoe UI", 10), Cursor = Cursors.Hand };
    readonly Button revokeButton = new() { Text = "Disconnect selected", Width = 170, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(161, 65, 61), Font = new Font("Segoe UI", 9, FontStyle.Bold), Cursor = Cursors.Hand };
    readonly Button refreshCodeButton = new() { Text = "New code", Width = 112, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(39, 103, 89), Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    bool running, busy;
    string? code;
    string agentId = Guid.NewGuid().ToString("D");
    string agentToken = NewAgentToken();
    string WorkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintBridge", "jobs");
    string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintBridge", "agent.json");

    public MainForm()
    {
        Text = "PrintBridge Agent"; Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application; Width = 1240; Height = 850; MinimumSize = new Size(1000, 700); StartPosition = FormStartPosition.CenterScreen; WindowState = FormWindowState.Maximized;
        BackColor = Color.FromArgb(244, 247, 246); Font = new Font("Segoe UI", 10); Padding = Padding.Empty; DoubleBuffered = true;
        BuildInterface();
        var printers = PrinterSettings.InstalledPrinters.Cast<string>().ToArray(); printerBox.Items.AddRange(printers);
        var def = new PrinterSettings().PrinterName; if (printerBox.Items.Contains(def)) printerBox.SelectedItem = def; else if (printers.Length > 0) printerBox.SelectedIndex = 0;
        autoPrint.CheckedChanged += (_, _) => { if (autoPrint.Checked && MessageBox.Show("Automatic printing lets any approved phone send jobs straight to this printer. Only enable it for people you trust.", "Enable automatic printing?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) autoPrint.Checked = false; };
        requestList.Columns.Add("PHONE", 180); requestList.Columns.Add("REQUESTED", 100);
        deviceList.Columns.Add("CONNECTED PHONE", 190); deviceList.Columns.Add("ADDED", 90);
        startButton.Click += async (_, _) => { if (running) StopAgent(); else await StartAgent(); };
        approveButton.Click += async (_, _) => await PairAction("approve"); rejectButton.Click += async (_, _) => await PairAction("reject");
        revokeButton.Click += async (_, _) => await RevokeDevice();
        refreshCodeButton.Click += async (_, _) => await GenerateCode();
        startWithWindows.CheckedChanged += (_, _) => SetStartup(startWithWindows.Checked);
        printerBox.SelectedIndexChanged += async (_, _) => { if (running) await Heartbeat(); };
        timer.Tick += async (_, _) => { await Heartbeat(); await Poll(); }; LoadSettings(); FormClosing += (_, _) => { timer.Stop(); SaveSettings(); };
        Shown += async (_, _) => { if (startWithWindows.Checked) await StartAgent(); };
    }

    static string NewAgentToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    void BuildInterface()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.FromArgb(244, 247, 246), Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Controls.Add(root);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.White, Padding = new Padding(28, 12, 30, 12) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 7, 0, 0), BackColor = Color.White };
        brand.Controls.Add(new Label { Text = "P", TextAlign = ContentAlignment.MiddleCenter, Size = new Size(42, 42), BackColor = Color.FromArgb(39, 132, 110), ForeColor = Color.White, Font = new Font("Segoe UI", 20, FontStyle.Bold), Margin = new Padding(0, 0, 12, 0) });
        var brandText = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Size = new Size(300, 48), BackColor = Color.White, Margin = Padding.Empty };
        brandText.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); brandText.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        brandText.Controls.Add(new Label { Text = "PrintBridge Agent", Font = new Font("Segoe UI", 17, FontStyle.Bold), ForeColor = Color.FromArgb(23, 48, 44), AutoSize = true, Margin = Padding.Empty });
        brandText.Controls.Add(new Label { Text = "WIRELESS PRINTING FOR WINDOWS", Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(112, 132, 126), AutoSize = true, Margin = Padding.Empty }, 0, 1);
        brand.Controls.Add(brandText); header.Controls.Add(brand, 0, 0);
        var statusPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 18, 0, 0), BackColor = Color.White };
        statusPanel.Controls.Add(topStatusLabel); header.Controls.Add(statusPanel, 1, 0); root.Controls.Add(header, 0, 0);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.FromArgb(244, 247, 246), Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.Controls.Add(body, 0, 1);
        body.Controls.Add(BuildSidebar(), 0, 0); body.Controls.Add(BuildDashboard(), 1, 0);
    }

    Control BuildSidebar()
    {
        var side = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 48, 44), Padding = new Padding(26, 28, 26, 24) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = side.BackColor };
        side.Controls.Add(flow);
        flow.Controls.Add(SideTitle("COMPUTER SETUP"));
        flow.Controls.Add(new Label { Text = "Connect this PC to your PrintBridge workspace.", ForeColor = Color.FromArgb(182, 202, 196), Font = new Font("Segoe UI", 10), Width = 280, Height = 48, Margin = new Padding(0, 7, 0, 22) });
        flow.Controls.Add(SideTitle("BACKEND ADDRESS")); serverBox.Width = 280; serverBox.Margin = new Padding(0, 8, 0, 20); flow.Controls.Add(serverBox);
        flow.Controls.Add(SideTitle("WINDOWS PRINTER")); printerBox.Width = 280; printerBox.Margin = new Padding(0, 8, 0, 20); flow.Controls.Add(printerBox);
        startButton.Margin = new Padding(0, 2, 0, 18); flow.Controls.Add(startButton);
        autoPrint.ForeColor = Color.FromArgb(231, 239, 236); autoPrint.Margin = new Padding(0, 4, 0, 12); flow.Controls.Add(autoPrint);
        startWithWindows.ForeColor = Color.FromArgb(231, 239, 236); startWithWindows.Margin = new Padding(0, 4, 0, 26); flow.Controls.Add(startWithWindows);
        flow.Controls.Add(new Panel { Width = 280, Height = 1, BackColor = Color.FromArgb(66, 91, 84), Margin = new Padding(0, 0, 0, 18) });
        flow.Controls.Add(SideTitle("AGENT STATUS")); stateLabel.Margin = new Padding(0, 10, 0, 5); flow.Controls.Add(stateLabel);
        flow.Controls.Add(new Label { Text = "A private computer identity is created automatically. No shared Agent key is needed.", ForeColor = Color.FromArgb(182, 202, 196), Font = new Font("Segoe UI", 9), Width = 280, Height = 58, Margin = new Padding(0, 10, 0, 12) });
        flow.Controls.Add(new Label { Text = "Keep this app open while your phone sends print jobs. Files are removed from this PC when printing finishes.", ForeColor = Color.FromArgb(153, 177, 169), Font = new Font("Segoe UI", 9), Width = 280, Height = 60, Margin = new Padding(0, 10, 0, 8) });
        return side;
    }

    static Label SideTitle(string text) => new() { Text = text, ForeColor = Color.FromArgb(150, 183, 173), Font = new Font("Segoe UI", 8, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 4, 0, 0) };

    Control BuildDashboard()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(26, 20, 26, 22), BackColor = Color.FromArgb(244, 247, 246) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var pageTitle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        pageTitle.RowStyles.Add(new RowStyle(SizeType.Percent, 62)); pageTitle.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        pageTitle.Controls.Add(new Label { Text = "Computer dashboard", Font = new Font("Segoe UI", 22, FontStyle.Bold), ForeColor = Color.FromArgb(24, 48, 44), AutoSize = true, Margin = Padding.Empty });
        pageTitle.Controls.Add(new Label { Text = "Pair a phone, approve requests, and manage print jobs.", Font = new Font("Segoe UI", 10), ForeColor = Color.FromArgb(104, 120, 115), AutoSize = true, Margin = Padding.Empty }, 0, 1);
        layout.Controls.Add(pageTitle, 0, 0);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(-10) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 44)); cards.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        var codeBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White, Padding = new Padding(4, 4, 4, 0) };
        codeBody.RowStyles.Add(new RowStyle(SizeType.Percent, 58)); codeBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); codeBody.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        codeBody.Controls.Add(codeLabel, 0, 0); codeBody.Controls.Add(codeExpiryLabel, 0, 1); refreshCodeButton.Anchor = AnchorStyles.Left; codeBody.Controls.Add(refreshCodeButton, 0, 2);
        cards.Controls.Add(CreateCard("Phone pairing", "A temporary code lets your phone request access.", codeBody), 0, 0);
        var requestsBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(0, 4, 0, 0) };
        requestsBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); requestsBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); requestsBody.Controls.Add(requestList, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.White, Padding = new Padding(0, 5, 0, 0) };
        approveButton.Margin = new Padding(0, 0, 10, 0); rejectButton.Margin = Padding.Empty; actions.Controls.Add(approveButton); actions.Controls.Add(rejectButton); requestsBody.Controls.Add(actions, 0, 1);
        cards.Controls.Add(CreateCard("Phone requests", "Approve only phones you recognize.", requestsBody), 1, 0);
        var devicesBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(0, 4, 0, 0) };
        devicesBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); devicesBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); devicesBody.Controls.Add(deviceList, 0, 0);
        var deviceActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.White, Padding = new Padding(0, 3, 0, 0) };
        revokeButton.Margin = Padding.Empty; deviceActions.Controls.Add(revokeButton); devicesBody.Controls.Add(deviceActions, 0, 1);
        cards.Controls.Add(CreateCard("Connected phones", "Manage phones approved for this computer.", devicesBody), 0, 1);
        var jobsBody = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(0, 4, 0, 0) }; jobsBody.Controls.Add(jobList);
        cards.Controls.Add(CreateCard("Recent print jobs", "Files are downloaded only when the Agent accepts a job.", jobsBody), 1, 1);
        layout.Controls.Add(cards, 0, 1); return layout;
    }

    static Panel CreateCard(string title, string subtitle, Control content)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(10), Padding = new Padding(22) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White, Margin = Padding.Empty };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI", 14, FontStyle.Bold), ForeColor = Color.FromArgb(27, 51, 47), AutoSize = true, Margin = Padding.Empty });
        table.Controls.Add(new Label { Text = subtitle, Font = new Font("Segoe UI", 9), ForeColor = Color.FromArgb(109, 126, 120), AutoSize = true, Margin = Padding.Empty }, 0, 1);
        content.Margin = Padding.Empty; table.Controls.Add(content, 0, 2); panel.Controls.Add(table); return panel;
    }

    void SetStartup(bool enabled) { try { using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (enabled) run.SetValue("PrintBridgeAgent", $"\"{Application.ExecutablePath}\""); else run.DeleteValue("PrintBridgeAgent", false); } catch { MessageBox.Show("Windows could not update the sign-in startup setting."); } }
    void LoadSettings() { try { if (File.Exists(SettingsFile)) { using var d = JsonDocument.Parse(File.ReadAllText(SettingsFile)); var root = d.RootElement; if (root.TryGetProperty("server", out var s)) { var saved=s.GetString(); serverBox.Text=string.IsNullOrWhiteSpace(saved)||saved=="http://localhost:3001"?HostedServer:saved; } if (root.TryGetProperty("printer", out var p) && printerBox.Items.Contains(p.GetString())) printerBox.SelectedItem = p.GetString(); if (root.TryGetProperty("agentId", out var id) && Guid.TryParse(id.GetString(), out _)) agentId=id.GetString()!; if (root.TryGetProperty("agentTokenProtected", out var token) && token.GetString() is { Length: > 0 } protectedToken) agentToken=AgentSecret.Unprotect(protectedToken); else { agentId=Guid.NewGuid().ToString("D"); agentToken=NewAgentToken(); } } using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); startWithWindows.Checked = run?.GetValue("PrintBridgeAgent") != null; } catch { agentId=Guid.NewGuid().ToString("D"); agentToken=NewAgentToken(); } }
    void SaveSettings() { try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!); File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { server = serverBox.Text, printer = printerBox.Text, agentId, agentTokenProtected = AgentSecret.Protect(agentToken) })); } catch { } }
    HttpRequestMessage Request(HttpMethod method, string route, object? body = null) { var m = new HttpRequestMessage(method, serverBox.Text.TrimEnd('/') + route); m.Headers.Add("X-Agent-ID", agentId); m.Headers.Add("X-Agent-Token", agentToken); if (body != null) m.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"); return m; }
    async Task<JsonDocument> Send(HttpMethod method, string route, object? body = null) { using var req = Request(method, route, body); using var res = await http.SendAsync(req); var text = await res.Content.ReadAsStringAsync(); if (!res.IsSuccessStatusCode) throw new Exception(text); return JsonDocument.Parse(text); }
    async Task StartAgent()
    {
        if (printerBox.SelectedItem == null) { MessageBox.Show("Install a Windows printer before starting the agent."); return; }
        if (!Uri.TryCreate(serverBox.Text.Trim(), UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) { MessageBox.Show("Enter a valid HTTP or HTTPS backend address."); return; }
        try { Directory.CreateDirectory(WorkDir); foreach (var old in Directory.GetFiles(WorkDir)) try { File.Delete(old); } catch { } SaveSettings(); using var registration = await Send(HttpMethod.Post, "/api/agent/register", new { id = agentId, token = agentToken, name = Environment.MachineName }); using var _ = await Send(HttpMethod.Post, "/api/agent/printer", new { name = printerBox.Text, online = true }); running = true; startButton.Text = "Stop Agent"; SetStatus($"ONLINE · {printerBox.Text}", true); timer.Start(); await GenerateCode(); await Poll(); }
        catch (Exception ex) { running = false; timer.Stop(); SetStatus("CONNECTION ISSUE", false); MessageBox.Show($"Could not connect to the PrintBridge service. Check the backend address and internet connection.\n\n{ex.Message}", "Connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    async Task GenerateCode() { if (!running) return; try { using var d = await Send(HttpMethod.Post, "/api/agent/pairing-code"); code = d.RootElement.GetProperty("code").GetString(); codeLabel.Text = code is { Length: 6 } ? $"{code[..3]}   {code[3..]}" : "— — —   — — —"; codeExpiryLabel.Text = "Valid for 5 minutes · share with your phone"; } catch { codeExpiryLabel.Text = "Could not refresh code. Check the connection."; } }
    void SetStatus(string text, bool online) { stateLabel.Text = text; stateLabel.ForeColor = online ? Color.FromArgb(132, 225, 188) : Color.FromArgb(255, 188, 165); topStatusLabel.Text = online ? "●  ONLINE" : "●  OFFLINE"; topStatusLabel.ForeColor = online ? Color.FromArgb(39, 132, 110) : Color.FromArgb(145, 158, 153); }
    void StopAgent() { running = false; timer.Stop(); _ = Send(HttpMethod.Post, "/api/agent/printer", new { name = printerBox.Text, online = false }); startButton.Text = "Start Agent"; SetStatus("OFFLINE", false); code = null; codeLabel.Text = "— — —   — — —"; codeExpiryLabel.Text = "Start the Agent to create a temporary pairing code."; }
    async Task Heartbeat() { if (!running) return; try { using var _ = await Send(HttpMethod.Post, "/api/agent/printer", new { name = printerBox.Text, online = true }); } catch { } }
    async Task Poll()
    {
        if (!running || busy) return;
        try
        {
            var selectedRequestId = requestList.SelectedItems.Count > 0 ? requestList.SelectedItems[0].Tag is PairRequest selectedRequest ? selectedRequest.id : null : null;
            using var d = await Send(HttpMethod.Get, "/api/agent/pairing-requests"); requestList.Items.Clear();
            foreach (var e in d.RootElement.EnumerateArray())
            {
                var p = new PairRequest(e.GetProperty("id").GetString()!, e.GetProperty("deviceName").GetString()!, e.GetProperty("created").GetInt64());
                var item = new ListViewItem(p.deviceName) { Tag = p }; item.SubItems.Add(FormatTime(p.created)); requestList.Items.Add(item);
                if (p.id == selectedRequestId) item.Selected = true;
            }
            if (requestList.Items.Count == 0) AddPlaceholder(requestList, "No phone is waiting for approval", "New requests appear here");
            var selectedDeviceId = deviceList.SelectedItems.Count > 0 ? deviceList.SelectedItems[0].Tag is AgentDevice selectedDevice ? selectedDevice.id : null : null;
            using var connected = await Send(HttpMethod.Get, "/api/devices"); deviceList.Items.Clear();
            foreach (var e in connected.RootElement.EnumerateArray())
            {
                var phone = new AgentDevice(e.GetProperty("id").GetString()!, e.GetProperty("name").GetString()!, e.GetProperty("created").GetInt64());
                var item = new ListViewItem(phone.name) { Tag = phone }; item.SubItems.Add(FormatDate(phone.created)); deviceList.Items.Add(item);
                if (phone.id == selectedDeviceId) item.Selected = true;
            }
            if (deviceList.Items.Count == 0) AddPlaceholder(deviceList, "No phones connected yet", "Approve a request to connect one");
            using var jobs = await Send(HttpMethod.Get, "/api/agent/jobs"); var first = jobs.RootElement.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Undefined) { var job = JsonSerializer.Deserialize<PrintJob>(first.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); if (job != null) await HandleJob(job); }
        }
        catch (Exception ex) { SetStatus("CONNECTION ISSUE · RETRYING", false); Debug.WriteLine(ex.Message); }
    }
    static void AddPlaceholder(ListView list, string title, string detail)
    {
        var item = new ListViewItem(title) { ForeColor = Color.FromArgb(125, 139, 134) }; item.SubItems.Add(detail); list.Items.Add(item);
    }
    static string FormatTime(long timestamp) => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("t");
    static string FormatDate(long timestamp) => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("d");
    async Task PairAction(string action)
    {
        if (requestList.SelectedItems.Count == 0 || requestList.SelectedItems[0].Tag is not PairRequest p) { MessageBox.Show("Select a phone request first."); return; }
        try { using var _ = await Send(HttpMethod.Post, $"/api/agent/pairing-requests/{p.id}/{action}"); await Poll(); }
        catch { MessageBox.Show("Could not update this pairing request. Try again."); }
    }
    async Task RevokeDevice()
    {
        if (deviceList.SelectedItems.Count == 0 || deviceList.SelectedItems[0].Tag is not AgentDevice phone) { MessageBox.Show("Select a connected phone first."); return; }
        if (MessageBox.Show($"Disconnect {phone.name}? It will no longer be able to send print jobs until it is paired again.", "Disconnect phone", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { using var _ = await Send(HttpMethod.Delete, $"/api/devices/{phone.id}"); await Poll(); }
        catch { MessageBox.Show("Could not disconnect this phone. Try again."); }
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
        // Use A4 and a small safety margin instead of the driver's usual 1-inch margins.
        // This keeps the complete image on the sheet while making it as large as possible.
        var a4 = doc.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.A4 || p.PaperName.Equals("A4", StringComparison.OrdinalIgnoreCase));
        if (a4 != null) doc.DefaultPageSettings.PaperSize = a4;
        doc.DefaultPageSettings.Margins = new Margins(15, 15, 15, 15);
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
