using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ComfyBatchTool.Models;
using ComfyBatchTool.Services;

namespace ComfyBatchTool;

public partial class MainWindow : Window
{
    private readonly ComfyApiService _api = new();
    private readonly ObservableCollection<WorkflowItem> _workflows = new();
    private readonly DispatcherTimer _saveTimer;
    private static Encoding? _gbEncoding;
    private bool _suppressSave;

    public MainWindow()
    {
        InitializeComponent();
        LstWorkflows.ItemsSource = _workflows;

        // 勾选变化后延迟写盘，避免「全选 / 全不选」时连续写几十次文件
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSelection();
        };

        Loaded += MainWindow_Loaded;
        Closed += (_, _) => SaveSelection();
    }

    // 软件启动时自动读取一次 api 文件夹
    private void MainWindow_Loaded(object sender, RoutedEventArgs e) => ReloadWorkflows();

    private void ReloadWorkflows()
    {
        _workflows.Clear();
        string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "api");

        if (!Directory.Exists(targetDir))
        {
            SetError($"未找到 api 目录: {targetDir}");
            return;
        }

        var files = Directory.GetFiles(targetDir, "*.json")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            SetError($"api 目录中无 JSON 文件: {targetDir}");
            return;
        }

        var selection = AppSettingsStore.Load();

        _suppressSave = true;
        try
        {
            foreach (var path in files)
            {
                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                    var item = new WorkflowItem(path, root);

                    // 保存过设置时用上次的勾选状态；之前没出现过的新文件默认勾选
                    if (selection.HasSavedFile && selection.Known.Contains(item.FileName))
                        item.IsEnabled = selection.Enabled.Contains(item.FileName);

                    item.PropertyChanged += Workflow_PropertyChanged;
                    _workflows.Add(item);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"{Path.GetFileName(path)}\n{ex.Message}",
                        "工作流加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        finally
        {
            _suppressSave = false;
        }

        SetStatus(selection.HasSavedFile
            ? $"已加载 {_workflows.Count} 个 JSON（已恢复上次勾选）"
            : $"已加载 {_workflows.Count} 个 JSON，默认全部勾选");
    }

    private void Workflow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkflowItem.IsEnabled) || _suppressSave) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSelection()
    {
        _saveTimer.Stop();
        // api 目录为空时不要把已保存的勾选状态清掉
        if (_workflows.Count == 0) return;
        AppSettingsStore.Save(
            _workflows.Where(w => w.IsEnabled).Select(w => w.FileName),
            _workflows.Select(w => w.FileName));
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => SetAllWorkflowsEnabled(true);

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e) => SetAllWorkflowsEnabled(false);

    private void SetAllWorkflowsEnabled(bool enabled)
    {
        foreach (var wf in _workflows) wf.IsEnabled = enabled;
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_workflows.Count == 0)
        {
            SetError("没有可用的工作流 JSON，请检查 api 目录后重启软件。");
            return;
        }

        var selected = _workflows.Where(w => w.IsEnabled).ToList();
        if (selected.Count == 0)
        {
            SetError("请至少勾选一个工作流。");
            return;
        }

        if (!int.TryParse(TxtCount.Text.Trim(), out int perPromptCount) || perPromptCount < 1)
        {
            SetError("每组生成数量必须为大于 0 的整数，请检查。");
            return;
        }

        var promptLines = ParsePromptLines(TxtPromptQueue.Text).ToList();

        if (promptLines.Count == 0)
        {
            SetError("正向提示词队列为空，请填写内容或拖入 txt 文件。");
            return;
        }

        bool lockSeed = ChkLockSeed.IsChecked == true;

        _api.BaseUrl = TxtBaseUrl.Text.Trim();
        BtnRun.IsEnabled = false;
        TxtLog.Text = string.Empty;
        SetStatus("正在生成...");

        try
        {
            int done = 0;

            foreach (var prompt in promptLines)
            {
                for (int i = 0; i < perPromptCount; i++)
                {
                    // 锁定种子：同一组提示词（同一副本）只随机一次，所有勾选的工作流共用这个 seed。
                    // 未锁定时保持原逻辑：每个工作流各自随机。
                    long sharedSeed = NextSeed();

                    foreach (var wf in selected)
                    {
                        var clone = JsonNode.Parse(wf.Root.ToJsonString())!.AsObject();
                        SetPositive(clone, prompt);

                        if (lockSeed) ApplySeed(clone, sharedSeed);
                        else RandomizeSeed(clone);

                        await _api.QueuePromptAsync(clone);
                        done++;
                    }
                }
            }
            ShowSubmitted(done);
            SetStatus("排队完成");
        }
        catch (Exception ex)
        {
            SetError($"出错: {ex.Message}");
            MessageBox.Show(this, ex.Message, "提交任务出错",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnRun.IsEnabled = true;
        }
    }

    /// 将队列中的提示词拼接到原有的正向提示词后面（保留模板原本的质量词）
    private void SetPositive(JsonObject root, string prompt)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            var title = node["_meta"]?["title"]?.GetValue<string>() ?? "";
            if (!title.Contains("Positive", StringComparison.OrdinalIgnoreCase)) continue;
            if (node["inputs"] is not JsonObject inputs ||
                inputs["text"] is not JsonNode textNode) continue;

            // 拿到模板里原本的提示词（比如你 json 里的 "best"）
            var originalText = textNode.GetValue<string>().Trim().TrimEnd(',', '，').TrimEnd();

            // 把队列里的提示词拼在后面
            string finalText;
            if (string.IsNullOrWhiteSpace(originalText))
                finalText = prompt;
            else
                finalText = $"{originalText}, {prompt}";

            inputs["text"] = finalText;
            return;
        }
    }

    /// 自动给每个 KSampler 的种子换成随机数（每个节点各自随机）
    private static void RandomizeSeed(JsonObject root)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            var field = SeedFieldName(node);
            if (field is null) continue;

            if (node["inputs"] is JsonObject inputs && inputs.ContainsKey(field))
                inputs[field] = NextSeed();
        }
    }

    /// 把工作流里所有种子节点替换成同一个固定 seed
    private static void ApplySeed(JsonObject root, long seed)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            var field = SeedFieldName(node);
            if (field is null) continue;

            if (node["inputs"] is JsonObject inputs && inputs.ContainsKey(field))
                inputs[field] = seed;
        }
    }

    /// 返回节点里种子字段的名字，非采样节点返回 null
    private static string? SeedFieldName(JsonObject node)
    {
        var classType = node["class_type"]?.GetValue<string>() ?? "";
        return classType switch
        {
            "KSampler" => "seed",
            // KSamplerAdvanced 的输入字段是 noise_seed（不是 seed）
            "KSamplerAdvanced" or "RandomNoise" => "noise_seed",
            _ => null
        };
    }

    private static long NextSeed() => Random.Shared.NextInt64(0, long.MaxValue);

    // ---------------- 拖入 txt 文件 ----------------

    private void Prompt_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Prompt_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var txtFiles = paths
            .Where(p => File.Exists(p) &&
                        string.Equals(Path.GetExtension(p), ".txt", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (txtFiles.Count == 0)
        {
            SetError("请拖入 .txt 文本文件。");
            return;
        }

        var groups = new List<string>();
        var failed = new List<string>();

        foreach (var file in txtFiles)
        {
            try
            {
                groups.AddRange(ParsePromptLines(ReadTextAuto(file)));
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (groups.Count > 0)
        {
            var existing = TxtPromptQueue.Text.TrimEnd();
            var addition = string.Join(Environment.NewLine, groups);
            TxtPromptQueue.Text = existing.Length == 0
                ? addition
                : existing + Environment.NewLine + addition;
            TxtPromptQueue.CaretIndex = TxtPromptQueue.Text.Length;
            TxtPromptQueue.ScrollToEnd();
        }

        if (failed.Count == 0)
        {
            SetStatus($"已读取 {txtFiles.Count} 个 txt，共 {groups.Count} 组提示词");
        }
        else
        {
            SetError($"读取失败 {failed.Count} 个文件");
            MessageBox.Show(this, string.Join("\n", failed), "部分文件读取失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// txt 内容按行拆分，每个非空行当作一组提示词
    private static IEnumerable<string> ParsePromptLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n')
               .Split('\n')
               .Select(l => l.Trim())
               .Where(l => l.Length > 0);

    /// 读取文本文件，自动识别 UTF-8 / UTF-16(BOM) / GB18030(ANSI 中文)
    private static string ReadTextAuto(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        if (bytes.Length == 0) return string.Empty;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Gb18030.GetString(bytes);
        }
    }

    private static Encoding Gb18030
    {
        get
        {
            if (_gbEncoding != null) return _gbEncoding;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gbEncoding = Encoding.GetEncoding(54936); // GB18030
            return _gbEncoding;
        }
    }

    // ---------------- 状态与日志 ----------------

    private void SetStatus(string msg)
    {
        TxtStatus.Text = msg;
        TxtStatus.Foreground = Brushes.Gray;
    }

    private void SetError(string msg)
    {
        TxtStatus.Text = msg;
        TxtStatus.Foreground = Brushes.Firebrick;
    }

    private void ShowSubmitted(int done)
    {
        TxtLog.Text = $"全部任务已提交，共 {done} 个。";
    }
}
