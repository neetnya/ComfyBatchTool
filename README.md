# ComfyUI Batch Prompt Tool

一个基于 WPF (.NET 8) 开发的轻量级 ComfyUI API 批量排队工具。无需在 ComfyUI 中反复拖拽节点，只需将 API JSON 放入指定目录，即可通过纯文本队列批量覆盖正向提示词并自动提交生成任务。

## ✨ 特性

- **极简工作流**：只负责替换 `Positive Prompt` 并提交队列，不干扰模型、负向提示词、种子等其他参数。
- **免导入设计**：程序自动读取运行目录下 `api` 文件夹内的所有 `.json` 工作流文件，支持同时加载多个工作流。
- **队列化生成**：支持多行文本输入，每行作为一组独立的正向提示词，自动按顺序追加到 ComfyUI 队列。
- **自动折行防呆**：文本框的视觉自动换行不会被视为新的一组提示词，只有手动回车才会触发分组。
- **多工作流并发支持**：如果你在 `api` 文件夹放了多个 JSON，工具会自动将每组提示词依次应用到所有 JSON 上。

## 🚀 快速开始

### 1. 环境要求
- Windows 系统
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高版本

### 2. 编译与运行
克隆本仓库到本地，在项目根目录打开终端（或 PowerShell）：

```bash
dotnet build
dotnet run