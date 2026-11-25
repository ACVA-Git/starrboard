using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

internal static class SetupUi
{
    public static void HideLauncherConsole() => HideConsole();

    public static void ShowDependencyInstaller(string scriptEndpoint)
    {
        HideConsole();
        RunSta(() => Application.Run(new DependencySetupForm(scriptEndpoint)));
    }

    public static void ShowServiceAccount(string scriptEndpoint)
    {
        HideConsole();
        RunSta(() => Application.Run(new ServiceAccountForm(scriptEndpoint)));
    }

    private static void RunSta(ThreadStart action)
    {
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            action();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    internal static async Task<string> DownloadScriptAsync(string endpoint)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        return await http.GetStringAsync(endpoint);
    }

    internal static async Task<int> RunPowerShellAsync(string script, IReadOnlyDictionary<string, string>? environment, Action<string> output)
    {
        var path = Path.Combine(Path.GetTempPath(), $"StarrboardSetup-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(path, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var pair in environment ?? new Dictionary<string, string>())
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) output(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) output("ERROR: " + args.Data); };
            if (!process.Start()) throw new InvalidOperationException("Windows could not start the background setup process.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    internal static Label Heading(string text, int size = 18) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", size, FontStyle.Bold),
        ForeColor = Color.FromArgb(238, 244, 251),
        Margin = new Padding(0, 0, 0, 6)
    };

    internal static Label Copy(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(650, 0),
        Font = new Font("Segoe UI", 10),
        ForeColor = Color.FromArgb(148, 163, 184),
        Margin = new Padding(0, 0, 0, 14)
    };

    internal static Button ActionButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(120, 38),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(79, 156, 249),
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 10, FontStyle.Bold),
        Margin = new Padding(8, 0, 0, 0)
    };

    internal static Form BaseForm(string title, Size size) => new()
    {
        Text = title,
        Size = size,
        MinimumSize = size,
        StartPosition = FormStartPosition.CenterScreen,
        BackColor = Color.FromArgb(12, 17, 24),
        ForeColor = Color.FromArgb(238, 244, 251),
        Font = new Font("Segoe UI", 10),
        FormBorderStyle = FormBorderStyle.FixedDialog,
        MaximizeBox = false
    };

    private static void HideConsole()
    {
        try { FreeConsole(); } catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
}

internal sealed class DependencySetupForm : Form
{
    private readonly string _endpoint;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly TextBox _log;
    private readonly Button _close;
    private QbittorrentCredential? _qbitCredential;

    public DependencySetupForm(string endpoint)
    {
        _endpoint = endpoint;
        var form = SetupUi.BaseForm("Starrboard Dependency Setup", new Size(760, 590));
        SuspendLayout();
        Text = form.Text;
        Size = form.Size;
        MinimumSize = form.MinimumSize;
        StartPosition = form.StartPosition;
        BackColor = form.BackColor;
        ForeColor = form.ForeColor;
        Font = form.Font;
        FormBorderStyle = form.FormBorderStyle;
        MaximizeBox = false;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), RowCount = 6, ColumnCount = 1 };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(SetupUi.Heading("Install media stack dependencies"), 0, 0);
        body.Controls.Add(SetupUi.Copy("Starrboard will install and start Radarr, Sonarr, Prowlarr, qBittorrent, and FlareSolverr. The underlying setup runs silently; progress and any errors appear below."), 0, 1);
        _status = new Label { Text = "Preparing setup...", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(0, 4, 0, 8) };
        body.Controls.Add(_status, 0, 2);
        _progress = new ProgressBar { Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Margin = new Padding(0, 0, 0, 14) };
        body.Controls.Add(_progress, 0, 3);
        _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(15, 23, 34), ForeColor = Color.FromArgb(203, 213, 225), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Cascadia Mono", 9) };
        body.Controls.Add(_log, 0, 4);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 14, 0, 0) };
        _close = SetupUi.ActionButton("Close");
        _close.Enabled = false;
        _close.Click += (_, _) => Close();
        footer.Controls.Add(_close);
        body.Controls.Add(footer, 0, 5);
        Controls.Add(body);
        ResumeLayout(true);
        Shown += async (_, _) => await RunSetupAsync();
        FormClosing += (_, args) => { if (!_close.Enabled) args.Cancel = true; };
    }

    private async Task RunSetupAsync()
    {
        try
        {
            Append("Downloading the signed Starrboard dependency plan...");
            var script = await SetupUi.DownloadScriptAsync(_endpoint);
            _status.Text = "Installing and starting dependencies";
            var exitCode = await SetupUi.RunPowerShellAsync(script, null, HandleSetupOutput);
            if (exitCode != 0) throw new InvalidOperationException($"Dependency setup exited with code {exitCode}.");
            Append("Discovering installed services and importing local API keys...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var discovered = false;
            for (var attempt = 1; attempt <= 18 && !discovered; attempt++)
            {
                using var response = await http.PostAsync("http://127.0.0.1:8686/api/onboarding/discover?apply=true", null);
                var body = await response.Content.ReadAsStringAsync();
                discovered = response.IsSuccessStatusCode && RequiredServicesReady(body);
                if (!discovered && attempt < 18)
                {
                    Append($"Services are still starting; retrying discovery ({attempt}/18)...");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }

            if (discovered)
            {
                Append("Radarr, Sonarr, and Prowlarr API settings were imported.");
                Append("Connecting Prowlarr, Radarr, Sonarr, and qBittorrent...");
                using var wireResponse = await http.PostAsync("http://127.0.0.1:8686/api/onboarding/wire", null);
                var wireBody = await wireResponse.Content.ReadAsStringAsync();
                AppendAutomationItems(wireBody);
                if (!wireResponse.IsSuccessStatusCode) Append("Some links need attention. Starrboard preserved existing configuration; review the listed result in onboarding.");
            }
            else
            {
                Append("Automatic discovery did not finish before the timeout. Use Refresh Installed Services in the dependency step.");
            }
            _status.Text = "Dependency setup complete";
            _status.ForeColor = Color.FromArgb(61, 220, 151);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;
            if (_qbitCredential != null)
            {
                using var credentials = new QbittorrentCredentialForm(_qbitCredential);
                credentials.ShowDialog(this);
            }
        }
        catch (Exception ex)
        {
            Append("ERROR: " + ex.GetBaseException().Message);
            _status.Text = "Setup needs attention";
            _status.ForeColor = Color.FromArgb(255, 101, 119);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        }
        finally { _close.Enabled = true; }
    }

    private void Append(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Append(text)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    private void HandleSetupOutput(string text)
    {
        const string marker = "STARRBOARD_QBIT_CREDENTIAL|";
        if (!text.StartsWith(marker, StringComparison.Ordinal))
        {
            Append(text);
            return;
        }
        var parts = text.Split('|', 4);
        if (parts.Length == 4)
        {
            _qbitCredential = new QbittorrentCredential(
                parts[1].Equals("CREATED", StringComparison.OrdinalIgnoreCase),
                string.IsNullOrWhiteSpace(parts[2]) ? "admin" : parts[2],
                parts[3]);
            Append(_qbitCredential.Created
                ? "qBittorrent administrator credentials were generated and saved. A credential dialog will open when setup completes."
                : "An existing qBittorrent administrator password was detected. A required-action dialog will open when setup completes.");
        }
    }

    private static bool RequiredServicesReady(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var required = new HashSet<string>(new[] { "Radarr", "Sonarr", "Prowlarr" }, StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("ok").GetBoolean()) required.Remove(item.GetProperty("name").GetString() ?? "");
            }
            return required.Count == 0;
        }
        catch { return false; }
    }

    private void AppendAutomationItems(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                var state = item.GetProperty("ok").GetBoolean() ? "Ready" : "Needs attention";
                Append($"{state} - {item.GetProperty("name").GetString()}: {item.GetProperty("message").GetString()}");
            }
        }
        catch { Append("Integration result could not be parsed; validate the stack from Starrboard."); }
    }
}

internal sealed record QbittorrentCredential(bool Created, string Username, string Password);

internal sealed class QbittorrentCredentialForm : Form
{
    public QbittorrentCredentialForm(QbittorrentCredential credential)
    {
        var form = SetupUi.BaseForm(
            credential.Created ? "qBittorrent credential created" : "qBittorrent credential required",
            new Size(760, credential.Created ? 680 : 640));
        Text = form.Text; Size = form.Size; MinimumSize = form.MinimumSize; StartPosition = form.StartPosition;
        BackColor = form.BackColor; ForeColor = form.ForeColor; Font = form.Font; FormBorderStyle = form.FormBorderStyle; MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(720, 600);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), AutoScroll = true, AutoSize = false, RowCount = 5, ColumnCount = 1 };
        for (var index = 0; index < 5; index++) body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(SetupUi.Heading(credential.Created ? "Save your qBittorrent login" : "Existing qBittorrent login detected"));
        var explanation = SetupUi.Copy(credential.Created
            ? "Starrboard has already saved this credential and used it to configure Radarr and Sonarr. Keep it somewhere secure because you need it when signing directly into the qBittorrent Web UI. The password is only shown in this dialog."
            : "qBittorrent stores its existing password as a one-way hash, so Starrboard cannot recover or import it. Enter the current password in the qBittorrent onboarding step. The existing account was not changed.");
        explanation.MaximumSize = new Size(680, 0);
        body.Controls.Add(explanation);
        body.Controls.Add(CredentialRow("Username", credential.Username));
        body.Controls.Add(CredentialRow("Password", credential.Created ? credential.Password : "Not recoverable - enter the existing password", credential.Created));

        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(570, 0),
            ForeColor = credential.Created ? Color.FromArgb(61, 220, 151) : Color.FromArgb(245, 185, 66),
            Text = credential.Created
                ? "No manual import is required. Continue onboarding and use Test qBittorrent Connection to confirm it."
                : "Required: provide the current password in Starrboard before automatic Radarr/Sonarr download-client setup can be validated.",
            Margin = new Padding(0, 10, 0, 12)
        };
        body.Controls.Add(note);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(20, 12, 20, 12), FlowDirection = FlowDirection.RightToLeft };
        var done = SetupUi.ActionButton(credential.Created ? "I've saved it" : "Continue onboarding");
        done.Click += (_, _) => Close();
        footer.Controls.Add(done);
        if (!credential.Created)
        {
            var open = SetupUi.ActionButton("Open qBittorrent");
            open.BackColor = Color.FromArgb(23, 34, 49);
            open.Click += (_, _) => Process.Start(new ProcessStartInfo { FileName = "http://127.0.0.1:18080", UseShellExecute = true });
            footer.Controls.Add(open);
        }
        Controls.Add(body);
        Controls.Add(footer);
        footer.BringToFront();
    }

    private static Control CredentialRow(string label, string value, bool allowCopy = true)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 8) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2 };
        stack.Controls.Add(new Label { Text = label, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        var valueBox = new TextBox { Text = value, ReadOnly = true, Dock = DockStyle.Top, Width = 560, BackColor = Color.FromArgb(15, 23, 34), ForeColor = Color.FromArgb(238, 244, 251) };
        stack.Controls.Add(valueBox);
        panel.Controls.Add(stack, 0, 0);
        if (allowCopy)
        {
            var copy = SetupUi.ActionButton("Copy");
            copy.MinimumSize = new Size(80, 34);
            copy.Margin = new Padding(10, 21, 0, 0);
            copy.Click += (_, _) => Clipboard.SetText(value);
            panel.Controls.Add(copy, 1, 0);
        }
        return panel;
    }
}

internal sealed class ServiceAccountForm : Form
{
    private readonly string _endpoint;
    private readonly TextBox _username;
    private readonly TextBox _password;
    private readonly TextBox _confirm;
    private readonly Label _status;
    private readonly TextBox _log;
    private readonly Button _apply;
    private readonly Button _close;

    public ServiceAccountForm(string endpoint)
    {
        _endpoint = endpoint;
        var form = SetupUi.BaseForm("Starrboard Service Account Setup", new Size(760, 760));
        Text = form.Text; Size = form.Size; MinimumSize = form.MinimumSize; StartPosition = form.StartPosition;
        BackColor = form.BackColor; ForeColor = form.ForeColor; Font = form.Font; FormBorderStyle = form.FormBorderStyle; MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), RowCount = 8, ColumnCount = 1 };
        for (var index = 0; index < 6; index++) body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(SetupUi.Heading("Configure the Windows service account"));
        body.Controls.Add(SetupUi.Copy("Enter the account that Radarr, Sonarr, Prowlarr, and optionally Starrboard should use. Credentials are passed only to the elevated background process and are not written to a script or Starrboard configuration."));
        _username = AddField(body, "Account name", @"Examples: media-service, COMPUTER\media-service, DOMAIN\media-service, or user@domain", false);
        _password = AddField(body, "Password", "The current password for this Windows or domain account", true);
        _confirm = AddField(body, "Confirm password", "Enter the same password again", true);
        _status = new Label { AutoSize = true, Text = "Ready", Margin = new Padding(0, 10, 0, 6), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        body.Controls.Add(_status);
        _log = new TextBox { Dock = DockStyle.Fill, MinimumSize = new Size(0, 130), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(15, 23, 34), ForeColor = Color.FromArgb(203, 213, 225), Font = new Font("Cascadia Mono", 9) };
        body.Controls.Add(_log);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        _apply = SetupUi.ActionButton("Apply account");
        _apply.Click += async (_, _) => await ApplyAsync();
        _close = SetupUi.ActionButton("Close");
        _close.BackColor = Color.FromArgb(23, 34, 49);
        _close.Click += (_, _) => Close();
        footer.Controls.Add(_apply);
        footer.Controls.Add(_close);
        body.Controls.Add(footer);
        Controls.Add(body);
    }

    private static TextBox AddField(TableLayoutPanel body, string title, string hint, bool password)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, RowCount = 3, Margin = new Padding(0, 5, 0, 8) };
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) });
        var input = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = password, Margin = new Padding(0, 5, 0, 3) };
        panel.Controls.Add(input);
        panel.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Color.FromArgb(148, 163, 184) });
        body.Controls.Add(panel);
        return input;
    }

    private async Task ApplyAsync()
    {
        if (string.IsNullOrWhiteSpace(_username.Text)) { MessageBox.Show(this, "Enter the service account name first.", "Account required", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (string.IsNullOrEmpty(_password.Text) || _password.Text != _confirm.Text) { MessageBox.Show(this, "Enter the password twice and make sure both values match.", "Password mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        SetBusy(true);
        try
        {
            Append("Downloading the Starrboard service-account plan...");
            var script = await SetupUi.DownloadScriptAsync(_endpoint);
            var env = new Dictionary<string, string>
            {
                ["STARRBOARD_SERVICE_ACCOUNT"] = _username.Text.Trim(),
                ["STARRBOARD_SERVICE_PASSWORD"] = _password.Text
            };
            var exitCode = await SetupUi.RunPowerShellAsync(script, env, Append);
            if (exitCode != 0) throw new InvalidOperationException($"Service-account setup exited with code {exitCode}.");
            _status.Text = "Service account applied successfully";
            _status.ForeColor = Color.FromArgb(61, 220, 151);
            _password.Clear();
            _confirm.Clear();
        }
        catch (Exception ex)
        {
            Append("ERROR: " + ex.GetBaseException().Message);
            _status.Text = "Setup needs attention";
            _status.ForeColor = Color.FromArgb(255, 101, 119);
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _apply.Enabled = !busy;
        _close.Enabled = !busy;
        _username.Enabled = !busy;
        _password.Enabled = !busy;
        _confirm.Enabled = !busy;
        if (busy) { _status.Text = "Validating and applying account..."; _status.ForeColor = ForeColor; }
    }

    private void Append(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Append(text)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }
}
