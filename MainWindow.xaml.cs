using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using ComfyBatchTool.Services;

namespace ComfyBatchTool;

public partial class MainWindow : Window
{
    private readonly ComfyApiService _api = new();
    private readonly List<(string Path, JsonObject Root)> _workflows = new();
    private static Encoding? _gbEncoding;

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
            SetError($"未找到 api 目录: {targetDir}");
            return;
        }

        var files = Directory.GetFiles(targetDir, "*.json");
        if (files.Length == 0)
        {
            SetError($"api 目录中无 JSON 文件: {targetDir}");
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
                MessageBox.Show(this, $"{Path.GetFileName(path)}\n{ex.Message}",
                    "工作流加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        SetStatus($"已加载 {_workflows.Count} 个 JSON");
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_workflows.Count == 0)
        {
            SetError("没有可用的工作流 JSON，请检查 api 目录后重启软件。");
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

        _api.BaseUrl = TxtBaseUrl.Text.Trim();
        BtnRun.IsEnabled = false;
        TxtLog.Text = string.Empty;
        SetStatus("正在生成...");

        try
        {
            int total = promptLines.Count * perPromptCount * _workflows.Count;
            int done = 0;

            foreach (var prompt in promptLines)
            {
                for (int i = 0; i < perPromptCount; i++)
                {
                    foreach (var (_, root) in _workflows)
                    {
                        var clone = JsonNode.Parse(root.ToJsonString())!.AsObject();
                        SetPositive(clone, prompt);
                        RandomizeSeed(clone); // 随机替换种子

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

    /// 自动给每个 KSampler 的种子换成随机数
    private void RandomizeSeed(JsonObject root)
    {
        var random = new Random();
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? "";

            // 兼容 KSampler 和 KSamplerAdvanced
            if (classType == "KSampler" || classType == "KSamplerAdvanced")
            {
                if (node["inputs"] is JsonObject inputs && inputs.ContainsKey("seed"))
                {
                    long newSeed = (long)(random.NextDouble() * long.MaxValue);
                    inputs["seed"] = newSeed;
                }
            }
        }
    }

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
