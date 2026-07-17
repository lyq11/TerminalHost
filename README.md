# TerminalHost

一个 Windows 自定义终端宿主：使用 **ConPTY** 直接承载 PowerShell 7、Windows PowerShell 5.1 或 CMD；使用 **WebView2 + xterm.js** 显示终端；外部程序通过本机 WebSocket 接管输入与输出。

> 本项目不嵌入 `wt.exe`。Windows Terminal 是另一个 ConPTY 宿主，本项目直接取代它的宿主层。

## 架构

```text
WPF + WebView2 + xterm.js
          │ 输入/VT 输出
          ▼
    TerminalSessionManager ───── WebSocket API 127.0.0.1:8765
          │
          ▼
        ConPTY
          │
     pwsh / powershell / cmd
```

## 已实现

- PowerShell 7、Windows PowerShell 5.1、CMD。
- ANSI/VT、颜色、光标、交互程序、方向键、Ctrl+C、窗口尺寸同步。
- GUI 与外部程序同时接收输出、写入输入。
- WebSocket 多会话创建、列出、写入、调整尺寸、发送控制键、停止和获取输出快照；创建响应先于该会话输出事件。
- API 仅监听 `127.0.0.1`，使用 256 位随机令牌。
- 每个会话保留最近约 1 MB 字符输出，供 `snapshot` 获取。

## 环境

- Windows 10 1809 或更高版本；建议 Windows 11。
- .NET 6 SDK（6.0.200 或更高的 .NET 6 SDK）。
- Microsoft Edge WebView2 Runtime。正常安装 Edge 的 Windows 10/11 通常已有。
- 使用 PowerShell 7 时需安装 `pwsh.exe`；否则选择 Windows PowerShell。

## 构建

```powershell
cd TerminalHost
.\scripts\clean-build.ps1
```

或：

```powershell
dotnet run --project .\src\TerminalHost.App\TerminalHost.App.csproj
```

发布为自包含 win-x64：

```powershell
.\scripts\publish-win-x64.ps1
```

## 使用

启动程序后：

1. 选择 `PowerShell 7`、`Windows PowerShell 5.1` 或 `CMD`。
2. 输入工作目录。
3. 点击“启动/重启”。
4. 点击“复制 API 信息”，获得带令牌的 WebSocket 地址。

令牌保存在：

```text
%LOCALAPPDATA%\TerminalHost\settings.json
```

## WebSocket API

连接：

```text
ws://127.0.0.1:8765/ws?token=<TOKEN>
```

连接后首先收到：

```json
{"type":"hello","service":"TerminalHost","protocol":1,"sessions":[]}
```

### 创建会话

```json
{
  "action": "create",
  "requestId": "1",
  "shell": "pwsh",
  "cwd": "C:\\xiaozhi-src",
  "cols": 120,
  "rows": 30
}
```

`shell`：`pwsh`、`powershell`、`cmd`。

### 输入原始终端数据

```json
{
  "action": "write",
  "requestId": "2",
  "sessionId": "会话ID",
  "data": "go build .\\cmd\\server\r"
}
```

注意：Enter 使用 `\r`，Ctrl+C 可直接发送 `\u0003`，也可以使用 `signal`。

### 发送控制键

```json
{
  "action": "signal",
  "sessionId": "会话ID",
  "signal": "ctrlC"
}
```

支持：`ctrlC`、`ctrlD`、`escape`、`enter`。

### 输出事件

```json
{
  "type": "output",
  "sessionId": "会话ID",
  "data": "包含 ANSI/VT 控制序列的 UTF-8 文本"
}
```

### 调整终端尺寸

```json
{
  "action": "resize",
  "sessionId": "会话ID",
  "cols": 160,
  "rows": 45
}
```

### 获取最近输出

```json
{"action":"snapshot","sessionId":"会话ID"}
```

### 停止会话

```json
{
  "action": "stop",
  "sessionId": "会话ID",
  "graceful": false,
  "remove": true
}
```

### 示例客户端

PowerShell：

```powershell
.\clients\powershell-client.ps1 -Token "复制出的令牌" -Cwd "C:\xiaozhi-src"
```

Python：

```powershell
pip install websockets
python .\clients\python-client.py --token "复制出的令牌" --cwd "C:\xiaozhi-src"
```

## 外部 Agent 的推荐方式

- 交互式控制：使用 `write`，完整转发终端按键和 VT 数据。
- 自动化执行：外部 Agent 自己维护命令状态，在命令后追加唯一完成标记，或额外建立结构化执行服务。
- 不要通过识别 PowerShell 提示符判断命令完成，因为提示符可修改，输出中也可能出现相同文本。

PowerShell 完成标记示例：

```powershell
$id = [guid]::NewGuid().ToString('N')
$command = "go build .\\cmd\\server; `$ec=`$LASTEXITCODE; Write-Output '__TH_DONE_${id}__:'`$ec"
```

## 安全边界

这是一个本地代码执行接口。当前版本采取：

- 只监听 IPv4 loopback `127.0.0.1`。
- 使用随机令牌，并以固定时间方式比较。
- 默认按当前用户普通权限启动，不自动 UAC 提权。
- 限制 shell 类型，不允许 API 直接指定任意可执行文件。

生产化建议增加：命令审计、目录白名单、客户端权限分级、令牌轮换、危险命令审批和 Windows ACL Named Pipe 通道。

## 已知边界

- GUI 当前显示一个“活动会话”，WebSocket API 可同时创建多个后台会话。
- 本项目不承载 Windows Terminal 的标签页和配置；它实现的是自己的 Terminal。
- 宿主与被启动的 shell 权限一致。需要管理员 shell 时，应以管理员身份启动整个 TerminalHost。
- ConPTY 输出本来就是 UTF-8 + VT 序列，外部程序若要显示完整终端画面，也需要终端解析器。

## .NET 6 兼容说明

本版本目标框架为 `net6.0-windows`，并将 WebView2 SDK 固定为 `1.0.1518.46`，以适配 .NET SDK 6.0.200 这一代工具链，避免新版 WebView2 与旧版 `WinRT.Runtime` 引用混用。ConPTY 通过 P/Invoke 调用，不依赖 WinRT 投影。

如果之前编译过旧包，请使用 `scripts\clean-build.ps1`，或手动删除 `bin`、`obj` 后重新还原。
