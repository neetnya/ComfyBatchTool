namespace ComfyBatchTool.Models;

public class WorkflowParam
{
    public string NodeId { get; set; } = "";
    public string NodeTitle { get; set; } = "";
    public string FieldName { get; set; } = "";
    public string CurrentValue { get; set; } = "";
    public bool IsSelected { get; set; }
    public string NewValue { get; set; } = "";
}