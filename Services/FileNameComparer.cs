using System.Runtime.InteropServices;

namespace ComfyBatchTool.Services;

/// <summary>
/// 文件名排序：直接调用 Windows 资源管理器的 StrCmpLogicalW，
/// 这样「副本 (2)」排在「副本 (10)」前面，和资源管理器里看到的顺序一致。
/// </summary>
public sealed class FileNameComparer : IComparer<string?>
{
    public static readonly FileNameComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        try
        {
            return StrCmpLogicalW(x, y);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 极端环境下退回普通忽略大小写比较
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
    }
}
