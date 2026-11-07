using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

    /// <summary>工作流 JSON 所在目录：程序目录下的 api 文件夹。</summary>
    private static string WorkflowDirectory =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "api");

    private void ReloadWorkflows()
    {
        _workflows.Clear();
        string targetDir = WorkflowDirectory;

        if (!Directory.Exists(targetDir))
        {
            SetStatus($"未找到 api 目录，可把 .json 拖进左侧列表创建: {targetDir}");
            return;
        }

        var files = Directory.GetFiles(targetDir, "*.json")
            .OrderBy(p => Path.GetFileNameWithoutExtension(p), FileNameComparer.Instance)
            .ToArray();
        if (files.Length == 0)
        {
            SetStatus($"api 目录中暂无 JSON，可拖入 .json 文件添加: {targetDir}");
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
            ? $"已加载 {_workflows.Count} 个 JSON，已恢复上次勾选"
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

    /// <summary>全局锁定时「锁定 seed」失去意义，置灰避免误解（XAML 初始化时会先于控件创建触发）。</summary>
    private void ChkGlobalLockSeed_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkLockSeed is null || ChkGlobalLockSeed is null) return;
        ChkLockSeed.IsEnabled = ChkGlobalLockSeed.IsChecked != true;
    }

    private void BtnReloadWorkflows_Click(object sender, RoutedEventArgs e) => ReloadWorkflows();

    /// <summary>在资源管理器中打开 api 工作流目录，目录不存在时先创建。</summary>
    private void BtnOpenWorkflowFolder_Click(object sender, RoutedEventArgs e)
    {
        string dir = WorkflowDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
            SetStatus($"已打开工作流目录: {dir}");
        }
        catch (Exception ex)
        {
            SetError($"无法打开工作流目录: {ex.Message}");
        }
    }

    // ---------------- 工作流管理：拖入 ----------------

    private void WorkflowList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedJsonFiles(e.Data).Count > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static List<string> GetDroppedJsonFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
        if (data.GetData(DataFormats.FileDrop) is not string[] paths) return new List<string>();

        return paths
            .Where(p => File.Exists(p) &&
                        string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把拖入的 .json 复制到 api 目录（先校验能否解析，避免放进坏文件）。</summary>
    private void WorkflowList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var jsonFiles = GetDroppedJsonFiles(e.Data);
        if (jsonFiles.Count == 0)
        {
            SetError("请拖入 .json 工作流文件。");
            return;
        }

        string dir = WorkflowDirectory;
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            SetError($"无法创建 api 目录: {ex.Message}");
            return;
        }

        var added = new List<string>();
        var failed = new List<string>();
        int skipped = 0;

        foreach (var file in jsonFiles)
        {
            var target = Path.Combine(dir, Path.GetFileName(file));

            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target),
                    StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            try
            {
                // 能解析成 JSON 对象才收，防止拖进非 ComfyUI 的 json 把列表搞乱
                JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}: 不是有效的 JSON - {ex.Message}");
                continue;
            }

            try
            {
                if (File.Exists(target))
                {
                    var answer = MessageBox.Show(this,
                        $"{Path.GetFileName(target)} 已存在，是否覆盖？",
                        "同名工作流", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes)
                    {
                        skipped++;
                        continue;
                    }
                }

                File.Copy(file, target, true);
                added.Add(Path.GetFileName(target));
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (added.Count > 0)
        {
            ReloadWorkflows();
            // 刚加进来的默认勾选
            foreach (var wf in _workflows.Where(w => added.Contains(w.FileName, StringComparer.OrdinalIgnoreCase)))
                wf.IsEnabled = true;
        }

        if (failed.Count > 0)
        {
            SetError($"已添加 {added.Count} 个，失败 {failed.Count} 个");
            MessageBox.Show(this, string.Join("\n", failed), "部分工作流添加失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var extra = skipped > 0 ? $"，跳过 {skipped} 个" : "";
        SetStatus(added.Count > 0 ? $"已添加 {added.Count} 个工作流{extra}" : $"没有新增工作流{extra}");
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
        bool globalLockSeed = ChkGlobalLockSeed.IsChecked == true;

        // 全局锁定：整批任务共用一个 seed，只在点击「开始批量排队」时随机这一次
        long? batchSeed = globalLockSeed ? NextSeed() : null;

        // 一批任务共用一个日期后缀，避免跨零点时同一批图片被分到两个日期
        string dateSuffix = DateTime.Now.ToString("yyMMdd");

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
                    // 全局锁定时整批共用一个 seed；否则勾选「锁定 seed」时同一组提示词（同一副本）
                    // 的所有工作流共用一个 seed，未勾选时每个工作流各自随机。
                    long sharedSeed = batchSeed ?? NextSeed();

                    foreach (var wf in selected)
                    {
                        var clone = JsonNode.Parse(wf.Root.ToJsonString())!.AsObject();
                        SetPositive(clone, prompt);
                        AppendDateToFilenamePrefix(clone, dateSuffix);

                        if (lockSeed || batchSeed.HasValue) ApplySeed(clone, sharedSeed);
                        else RandomizeSeed(clone);

                        await _api.QueuePromptAsync(clone);
                        done++;
                    }
                }
            }
            ShowSubmitted(done, batchSeed);
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

    /// <summary>
    /// 模板 text 里用来标记「队列提示词插在这里」的占位词。
    /// 例："Positive Prompt, masterpiece, very aesthetic, ..." →
    ///     "队列提示词, masterpiece, very aesthetic, ..."
    /// </summary>
    private const string PositivePlaceholder = "Positive Prompt";

    /// 把队列里的提示词写进正向提示词节点。
    /// 1. 文本里带占位符（如 "Positive Prompt"）时：只替换占位那一段，模板原有的质量词保留；
    /// 2. 没有占位符时：保持旧行为，拼接到模板提示词后面。
    private void SetPositive(JsonObject root, string prompt)
    {
        if (TrySetPositiveNode(root, prompt)) return;

        // 兜底：标题里没写 Positive，但文本里写了占位符的编码节点（工作流被改过名时不至于漏掉）
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            if (node["inputs"] is not JsonObject inputs ||
                inputs["text"] is not JsonNode textNode) continue;

            if (TryReplacePlaceholder(textNode, prompt)) return;
        }
    }

    /// <summary>在标题含 Positive 的节点里写入提示词，写入成功返回 true。</summary>
    private static bool TrySetPositiveNode(JsonObject root, string prompt)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            var title = node["_meta"]?["title"]?.GetValue<string>() ?? "";
            if (!title.Contains("Positive", StringComparison.OrdinalIgnoreCase)) continue;
            if (node["inputs"] is not JsonObject inputs ||
                inputs["text"] is not JsonNode textNode) continue;

            // 模板写了占位符：替换占位那一段
            if (TryReplacePlaceholder(textNode, prompt)) return true;

            // 没有占位符：拿到模板里原本的提示词（比如你 json 里的 "best"），把队列提示词拼在后面
            string originalText;
            try
            {
                originalText = textNode.GetValue<string>();
            }
            catch (Exception)
            {
                return false; // text 不是字符串（比如被连成了节点）就不碰
            }

            originalText = originalText.Trim().TrimEnd(',', '，').TrimEnd();
            inputs["text"] = string.IsNullOrWhiteSpace(originalText)
                ? prompt
                : $"{originalText}, {prompt}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 把文本里的占位符替换成队列提示词；没有占位符返回 false。
    /// 占位符出现多次时每处都替换。
    /// </summary>
    private static bool TryReplacePlaceholder(JsonNode textNode, string prompt)
    {
        string text;
        try
        {
            text = textNode.GetValue<string>();
        }
        catch (Exception)
        {
            return false;
        }

        int index = text.IndexOf(PositivePlaceholder, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;

        var builder = new StringBuilder(text.Length + prompt.Length);
        int start = 0;
        while (index >= 0)
        {
            builder.Append(text, start, index - start);
            builder.Append(prompt);
            start = index + PositivePlaceholder.Length;
            index = text.IndexOf(PositivePlaceholder, start, StringComparison.OrdinalIgnoreCase);
        }
        builder.Append(text, start, text.Length - start);

        // 占位符在开头/结尾时会多出逗号，收一下
        string finalText = builder.ToString().Trim();
        finalText = finalText.TrimStart(',', '，').Trim();
        finalText = finalText.TrimEnd(',', '，').Trim();

        textNode.ReplaceWith(finalText);
        return true;
    }

    /// 给保存节点的 filename_prefix 末尾追加日期（yyMMdd），方便按天归档。
    /// 例：模板里的 "boleromix/" 会变成 "boleromix/251106"，输出 boleromix/251106_00001_.png。
    private static void AppendDateToFilenamePrefix(JsonObject root, string dateSuffix)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            if (node["inputs"] is not JsonObject inputs) continue;

            // 只动带 filename_prefix 的节点（SaveImage 等），其它节点原样保留
            if (inputs["filename_prefix"] is not JsonNode prefixNode) continue;

            string prefix;
            try
            {
                prefix = prefixNode.GetValue<string>();
            }
            catch (Exception)
            {
                continue; // 意外类型（比如被改成节点连线）就不碰
            }

            // 模板自己已经写了当天日期时不重复追加
            if (prefix.EndsWith(dateSuffix, StringComparison.Ordinal)) continue;

            inputs["filename_prefix"] = prefix + dateSuffix;
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

    private void ShowSubmitted(int done, long? batchSeed)
    {
        TxtLog.Text = batchSeed is long seed
            ? $"全部任务已提交，共 {done} 个。本批全局 seed: {seed}"
            : $"全部任务已提交，共 {done} 个。";
    }
}
