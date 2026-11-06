using System.ComponentModel;
using System.IO;
using System.Text.Json.Nodes;

namespace ComfyBatchTool.Models;

/// <summary>
/// api 目录中的一个工作流 JSON，带一个「是否启用」的勾选状态。
/// </summary>
public class WorkflowItem : INotifyPropertyChanged
{
    private bool _isEnabled = true;

    public WorkflowItem(string path, JsonObject root)
    {
        Path = path;
        Root = root;
    }

    public string Path { get; }

    public JsonObject Root { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>界面上显示的名字，不带 .json 后缀。</summary>
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path);

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
