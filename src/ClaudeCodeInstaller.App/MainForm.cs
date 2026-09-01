using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeInstaller.Core;

namespace ClaudeCodeInstaller.App;

public class MainForm : Form
{
    private readonly TextBox _apiKeyBox = new() { UseSystemPasswordChar = true, PlaceholderText = "sk-..." };
    private readonly ComboBox _modelBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown,   // 可手输
    };
    private readonly CheckBox _ccSwitchCheck = new() { Text = "安装 cc-switch 切换工具", Checked = true };
    private readonly Button _testButton = new() { Text = "测试连接" };
    private readonly Button _startButton = new() { Text = "▶ 开始安装", Enabled = false };
    private readonly ProgressBar _progressBar = new() { Minimum = 0, Maximum = 100 };
    private readonly RichTextBox _logBox = new() { ReadOnly = true, BackColor = Color.FromArgb(20, 20, 28), ForeColor = Color.LightGray };
    private readonly Button _launchButton = new() { Text = "启动 Claude Code", Enabled = false };
    private readonly Button _closeButton = new() { Text = "关闭", DialogResult = DialogResult.Cancel };
    private readonly Label _progressLabel = new() { Text = "" };
    private InstallationEngine? _engine;
    private bool _installing;

    public MainForm()
    {
        Text = "Claude Code 一键安装器";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(540, 660);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;

        _modelBox.Items.AddRange(new object[] { "deepseek-v4-flash", "deepseek-chat", "deepseek-reasoner" });
        _modelBox.Text = VersionInfo.DefaultModel;
        _apiKeyBox.TextChanged += (_, _) => _startButton.Enabled = !_installing && _apiKeyBox.Text.Trim().Length > 0;
        _testButton.Click += async (_, _) => await TestConnectionAsync();
        _startButton.Click += async (_, _) => await StartInstallAsync();

        BuildLayout();
        _launchButton.Click += LaunchClaude;
        Log("请填写 DeepSeek API Key 并选择模型，然后点击「开始安装」。");
    }

    private void BuildLayout()
    {
        var y = 24;
        AddRow("DeepSeek API Key", _apiKeyBox, ref y);
        AddRow("模型名称", _modelBox, ref y);

        _ccSwitchCheck.Location = new Point(130, y); _ccSwitchCheck.Width = 320; y += 40;
        Controls.Add(_ccSwitchCheck);

        _testButton.Location = new Point(130, y); _testButton.Width = 120; _testButton.Height = 34;
        _startButton.Location = new Point(280, y); _startButton.Width = 170; _startButton.Height = 34;
        Controls.Add(_testButton); Controls.Add(_startButton);
        y += 60;

        _progressLabel.Location = new Point(24, y); _progressLabel.Size = new Size(490, 22); y += 26;
        Controls.Add(_progressLabel);

        _progressBar.Location = new Point(24, y); _progressBar.Size = new Size(490, 22); y += 40;
        Controls.Add(_progressBar);

        _logBox.Location = new Point(24, y); _logBox.Size = new Size(490, 300); y += 320;
        Controls.Add(_logBox);

        _launchButton.Location = new Point(24, y); _launchButton.Width = 150; _launchButton.Height = 36;
        _closeButton.Location = new Point(190, y); _closeButton.Width = 100; _closeButton.Height = 36;
        Controls.Add(_launchButton); Controls.Add(_closeButton);
    }

    private void AddRow(string label, Control input, ref int y)
    {
        Controls.Add(new Label { Text = label, Location = new Point(24, y + 6), Width = 100 });
        input.Location = new Point(130, y);
        input.Width = 360;
        Controls.Add(input);
        y += 46;
    }

    private void Log(string line)
    {
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
    }

    private async Task TestConnectionAsync()
    {
        var key = _apiKeyBox.Text.Trim();
        if (key.Length == 0) { MessageBox.Show("请先填写 API Key。"); return; }
        SetBusy(true);
        Log("正在测试连接…");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var resp = await client.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Log("✘ API Key 无效（401）。"); MessageBox.Show("API Key 无效，请检查后重试。", "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var list = (JsonNode.Parse(json)?["data"] as JsonArray)?
                .Select(m => m?["id"]?.GetValue<string>()).Where(x => x is not null).ToList();
            var modelOk = list is not null && list.Contains(_modelBox.Text.Trim());
            Log(modelOk ? $"✔ 连接成功，模型 {_modelBox.Text} 存在。" : $"⚠ 连接成功，但模型「{_modelBox.Text}」不在列表中（可能仍可用，或需换名）。");
            MessageBox.Show(modelOk ? "连接成功 ✔" : "连接成功，但模型名需确认。", "测试结果");
        }
        catch (Exception ex)
        {
            Log("✘ 连接失败: " + ex.Message);
            MessageBox.Show("连接失败：" + ex.Message, "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task StartInstallAsync()
    {
        if (_installing) return;
        _installing = true;
        SetBusy(true);
        _launchButton.Enabled = false;
        _logBox.Clear();
        _progressBar.Value = 0;
        _progressLabel.Text = "";

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _engine = new InstallationEngine(new NodeInstaller(new DownloadHelper(), new ProcessRunner(), new PathManager()),
            new ClaudeInstaller(new ProcessRunner()),
            new CcSwitchInstaller(new DownloadHelper(), new ProcessRunner()),
            new ConfigWriter(), profile);
        _engine.Log += Log;
        _engine.Progress += p => _progressBar.Value = p;
        _engine.StepStarted += (step, desc) => Log($"── {desc}");
        _engine.Finished += (message, success) =>
        {
            Log(success ? "==== 完成 ====" : "==== 失败 ====");
            foreach (var line in message.Split('\n')) Log(line);
            _installing = false;
            SetBusy(false);
            _launchButton.Enabled = success;
            if (success) MessageBox.Show(message, "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else MessageBox.Show(message, "安装未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        await _engine.RunAsync(new InstallOptions
        {
            ApiKey = _apiKeyBox.Text.Trim(),
            Model = _modelBox.Text.Trim(),
            InstallCcSwitch = _ccSwitchCheck.Checked,
        }, CancellationToken.None);
    }

    private void SetBusy(bool busy)
    {
        _testButton.Enabled = !busy;
        _startButton.Enabled = !busy && _apiKeyBox.Text.Trim().Length > 0;
        _apiKeyBox.Enabled = !busy;
        _modelBox.Enabled = !busy;
        _ccSwitchCheck.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void LaunchClaude(object? sender, EventArgs e)
    {
        var claudeCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
        if (!File.Exists(claudeCmd)) claudeCmd = "claude";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{claudeCmd}\"") { UseShellExecute = true });
    }
}