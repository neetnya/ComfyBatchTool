using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using ComfyBatchTool.Services;

namespace ComfyBatchTool;

public partial class MainWindow : Window
{
    private readonly ComfyApiService _api = new();
    private readonly List<(string Path, JsonObject Root)> _workflows = new();
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    // 软件启动时自动读取一次 api 文件夹
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _workflows.Clear();
        string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "api");

        if (!Directory.Exists(targetDir))
        {
            TxtStatus.Text = "未找到 api 目录";
            Log($"目录不存在: {targetDir} (请创建并放入 JSON 文件)");
            return;
        }

        var files = Directory.GetFiles(targetDir, "*.json");
        if (files.Length == 0)
        {
            TxtStatus.Text = "api 目录为空";
            Log($"目录中无 JSON 文件: {targetDir}");
            return;
        }

        foreach (var path in files)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                _workflows.Add((path, root));
            }
            catch (Exception ex)
            {
                Log($"加载失败 {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        TxtStatus.Text = $"已加载 {_workflows.Count} 个 JSON";
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_workflows.Count == 0) { Log("没有可用的工作流 JSON，请检查 api 目录后重启软件。"); return; }
        
        if (!int.TryParse(TxtCount.Text.Trim(), out int perPromptCount) || perPromptCount < 1)
        { 
            Log("每组生成数量必须为大于 0 的整数，请检查。"); 
            return; 
        }

        var promptLines = TxtPromptQueue.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (promptLines.Count == 0) { Log("正向提示词队列为空，请填写内容。"); return; }

        _api.BaseUrl = TxtBaseUrl.Text.Trim();
        _cts = new CancellationTokenSource();
        TxtStatus.Text = "正在生成...";

        try
        {
            int total = promptLines.Count * perPromptCount * _workflows.Count;
            int done = 0;

            foreach (var prompt in promptLines)
            {
                for (int i = 0; i < perPromptCount; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    foreach (var (path, root) in _workflows)
                    {
                        var clone = JsonNode.Parse(root.ToJsonString())!.AsObject();
                        SetPositive(clone, prompt);
                        var id = await _api.QueuePromptAsync(clone);
                        done++;
                        Log($"[{Path.GetFileName(path)}] {done}/{total} " +
                            $"提示词: {Truncate(prompt, 40)} → prompt_id={id}");
                    }
                }
            }
            Log($"全部任务已提交，共 {done} 个。");
            TxtStatus.Text = "排队完成";
        }
        catch (OperationCanceledException) 
        { 
            Log("已停止。"); 
            TxtStatus.Text = "已停止";
        }
        catch (Exception ex) 
        { 
            Log($"出错: {ex.Message}"); 
            TxtStatus.Text = "发生错误";
        }
    }

    private void SetPositive(JsonObject root, string prompt)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            var title = node["_meta"]?["title"]?.GetValue<string>() ?? "";
            if (!title.Contains("Positive", StringComparison.OrdinalIgnoreCase)) continue;
            if (node["inputs"] is not JsonObject inputs ||
                inputs["text"] is not JsonNode) continue;

            inputs["text"] = prompt;
            return;
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    private void Log(string msg)
    {
        TxtLog.AppendText($"{DateTime.Now:HH:mm:ss} {msg}\n");
        TxtLog.ScrollToEnd();
    }
}