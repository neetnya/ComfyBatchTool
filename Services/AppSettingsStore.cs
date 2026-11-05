using System.IO;
using System.Text.Json;

namespace ComfyBatchTool.Services;

/// <summary>上次保存下来的工作流勾选状态。</summary>
public sealed class WorkflowSelection
{
    public bool HasSavedFile { get; init; }

    /// <summary>上次保存时处于勾选状态的工作流文件名。</summary>
    public HashSet<string> Enabled { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上次保存时 api 目录中出现过的工作流文件名。</summary>
    public HashSet<string> Known { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 便携设置：与 exe 同目录的 settings.json，只记录工作流勾选状态。
/// 文件丢失或损坏时按「第一次运行」处理（默认全选）。
/// </summary>
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public static WorkflowSelection Load()
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(SettingsPath))
            return new WorkflowSelection { HasSavedFile = false, Enabled = enabled, Known = known };

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = doc.RootElement;
            ReadNames(root, "enabledWorkflows", enabled);
            ReadNames(root, "knownWorkflows", known);
        }
        catch
        {
            // 配置损坏时按「第一次运行」处理，避免启动报错
            enabled.Clear();
            known.Clear();
            return new WorkflowSelection { HasSavedFile = false, Enabled = enabled, Known = known };
        }

        return new WorkflowSelection { HasSavedFile = true, Enabled = enabled, Known = known };
    }

    public static void Save(IEnumerable<string> enabledFileNames, IEnumerable<string> knownFileNames)
    {
        var payload = new
        {
            enabledWorkflows = enabledFileNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            knownWorkflows = knownFileNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch
        {
            // 目录只读等情况忽略，不影响批量提交
        }
    }

    private static void ReadNames(JsonElement root, string propertyName, HashSet<string> target)
    {
        if (!root.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array) return;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var name = item.GetString();
            if (!string.IsNullOrWhiteSpace(name))
                target.Add(Path.GetFileName(name));
        }
    }
}
