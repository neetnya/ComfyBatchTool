# ComfyUI Batch Prompt Tool

一个基于 WPF (.NET 8) 开发的轻量级 ComfyUI API 批量排队工具。无需在 ComfyUI 中反复拖拽节点，只需将 API JSON 放入指定目录，即可通过纯文本队列批量覆盖正向提示词并自动提交生成任务。

## ✨ 特性

- **极简工作流**：只负责替换 `Positive Prompt` 并提交队列，不干扰模型、负向提示词、种子等其他参数。
- **占位符替换**：如果正向提示词的 `text` 里写了 `Positive Prompt` 占位词（例如 `Positive Prompt, masterpiece, best quality, ...`），队列里的提示词会**替换这段占位词**，前后保留模板自带的质量词；没有占位词时仍按旧逻辑追加在原文本后面。
- **免导入设计**：程序自动读取运行目录下 `api` 文件夹内的所有 `.json` 工作流文件，支持同时加载多个工作流。
- **队列化生成**：支持多行文本输入，每行作为一组独立的正向提示词，自动按顺序追加到 ComfyUI 队列。
- **自动折行防呆**：文本框的视觉自动换行不会被视为新的一组提示词，只有手动回车才会触发分组。
- **拖入 txt 文件**：支持一次拖入多个 `.txt` 文件，自动读取内容（每行一组），自动识别 UTF-8 / UTF-16 / GB18030(ANSI 中文) 编码。
- **多工作流并发支持**：如果你在 `api` 文件夹放了多个 JSON，工具会自动将每组提示词依次应用到所有 JSON 上。
- **工作流勾选**：读取到的 JSON 会以复选框列出，默认全选；取消勾选即可只提交选中的工作流（仍可用「全选 / 全不选」一键切换）。勾选状态会保存在程序目录的 `settings.json` 中，下次启动自动恢复；之后新增的工作流默认勾选。
- **打开目录**：工作流列表上方有「打开目录」按钮，可直接在资源管理器中打开 `api` 文件夹，方便丢入或整理 JSON。
- **锁定 seed**：勾选「锁定 seed」后，同一条提示词在所有已选工作流中使用同一个随机 seed，方便横向对比；不同提示词之间仍是各自随机。
- **全局锁定 seed**：勾选「🌐 全局锁定 seed」后，只有在点击「开始批量排队」时才随机一次 seed，本批次内**所有提示词、所有工作流**共用这一个 seed；提交完成后的日志会显示本批使用的 seed。与「锁定 seed」同时勾选时以全局锁定为准。

## 🚀 快速开始

### 1. 环境要求
- Windows 系统
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高版本

### 2. 编译与运行
克隆本仓库到本地，在项目根目录打开终端（或 PowerShell）：

```bash
dotnet build
dotnet run
```

### 3. 使用
1. 将 ComfyUI 导出的 API 格式 JSON 放入运行目录下的 `api` 文件夹（可放多个）。
2. 把工作流里的正向提示词节点标题改成包含 `Positive` 的名字，插件会自动覆盖该节点的 `text`。
   - 如果 `text` 里带 `Positive Prompt` 占位词，就只替换占位那一段（例：`Positive Prompt, masterpiece, best quality` → `1girl, smile, masterpiece, best quality`）；
   - 没有占位词时，队列提示词会追加到原有文本后面（例：`masterpiece, best quality` → `masterpiece, best quality, 1girl, smile`）。
3. 在正向提示词队列里每行填写一组提示词，或直接拖入 `.txt` 文件；设置每组生成数量后点击「开始批量排队」。
4. 提交完成后，运行日志区会显示「全部任务已提交，共 x 个。」。