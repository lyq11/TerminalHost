# TerminalHost 与 Codex / Claude 原生终端调用对比

> 本文所说的“原生终端调用”，是指 Codex 自带的 Shell/集成终端能力，以及 Claude Code 自带的 Bash 工具；不是在比较模型本身的代码能力。产品能力会持续变化，本文按 2026-07-18 的公开文档和 TerminalHost v1.2.2 整理。

## 一句话结论

- **一次性执行命令、修改代码、跑测试：**优先使用 Codex 或 Claude Code 的原生终端工具，路径最短。
- **需要独立可见的 Windows 终端、跨客户端复用、持久 Session ID 或人与 Agent 共用会话：**使用 TerminalHost。
- **二者可以同时使用：**原生终端负责常规开发命令，TerminalHost 负责需要持续交互、共享或观察的会话。

## 核心差异

| 维度 | Codex 原生终端 | Claude Code 原生 Bash | TerminalHost MCP |
| --- | --- | --- | --- |
| 执行层 | Codex 自己的 Shell、PTY 和命令执行层 | Claude Code 自己的 Bash 工具 | 独立 WPF 程序，通过 ConPTY 承载 PowerShell/CMD |
| 调用协议 | Codex 内部工具协议 | Claude Code 内部工具协议 | 标准 MCP 工具；支持 stdio、Streamable HTTP，内部连接 WebSocket API |
| 是否需要额外程序 | 不需要 | 不需要 | 需要运行 TerminalHost；便携包已包含 MCP Server 和 Node.js |
| 会话所有者 | Codex 客户端/运行环境 | Claude Code 客户端/运行环境 | TerminalHost 进程；Agent 通过 Session ID 操作 |
| 长时间运行 | 原生执行层可以管理长命令或 PTY，具体行为由客户端决定 | Bash 工具可以运行命令，具体生命周期由 Claude Code 决定 | 会话在 TerminalHost 中持续存在，直到退出、停止或应用关闭 |
| 断线后读取 | 取决于客户端的会话和输出保留机制 | 取决于 Claude Code 的会话机制 | 可重新列出 Session，并用 `terminal_snapshot` 读取最近约 1 MB 输出 |
| 跨客户端 | 工具和进程状态通常属于 Codex | 工具和进程状态通常属于 Claude Code | 任何兼容 MCP 的本机客户端可使用同一工具接口；HTTP 模式可连接同一 TerminalHost 实例 |
| GUI 可见性 | Codex 执行记录或集成终端 | Claude Code 所在终端 | 每个 GUI/MCP/API 会话都有独立标签页，输出实时可见 |
| 人机协作 | 人主要审核 Agent 的命令和结果 | 人主要审核 Claude Code 的调用 | 人可在 TerminalHost 标签页直接观察和输入；Agent 同时读写同一会话 |
| 交互能力 | 由 Codex 的 PTY/终端实现决定 | 由 Bash 工具及运行终端决定 | 原始终端输入、VT 输出、Ctrl+C/D、Enter、Escape、resize、交互程序 |
| Windows Shell | 由 Codex 当前 Windows 环境和配置决定 | 原生 Windows 通常依赖 Git Bash，或运行在 WSL | 原生 Windows ConPTY；支持 PowerShell 7、Windows PowerShell 5.1、CMD |
| 安全模型 | Codex 的沙箱、权限配置和审批流程 | Claude Code 的 allowed/disallowed tools 和权限模式 | 本机令牌、MCP 工具白名单、目录规则、危险命令确认、审计日志 |
| 单次调用开销 | 最低 | 最低 | 多一层 MCP + WebSocket；不以单条命令的最低延迟为目标 |

## TerminalHost 具体多了什么

### 1. 终端属于独立服务，而不是某个 Agent

TerminalHost 创建的 Shell 由独立进程管理。Codex、Claude、LM Studio 或其他 MCP 客户端操作的是 `sessionId`，而不是把终端进程隐藏在自己的工具实现里。

这适合以下情况：

- Agent 切换或 MCP 客户端重连后继续读取已有会话。
- 一个 Agent 启动开发服务器，另一个客户端读取日志或执行诊断。
- 人在 GUI 里观察 Agent 正在操作的真实终端。
- 需要把“创建、写入、快照、调整尺寸、停止”做成稳定的工具协议。

同一会话允许多个调用方写入，但 TerminalHost **不会自动协调并发输入**。多个 Agent 同时操作一个交互式 Shell 可能互相干扰，调用方应自行约定所有权。

### 2. MCP 客户端使用同一套工具

TerminalHost 暴露以下 MCP 工具：

- `terminal_list_sessions`
- `terminal_get_session`
- `terminal_snapshot`
- `terminal_create_session`
- `terminal_write`
- `terminal_execute`
- `terminal_signal`
- `terminal_resize`
- `terminal_stop_session`
- `terminal_ping`

因此接入方不需要分别适配 Codex Shell、Claude Bash 或某个模型厂商的私有函数格式。支持 MCP stdio 或 Streamable HTTP 的客户端可以复用同一层适配。

### 3. Agent 执行过程对人可见

通过 MCP 或 WebSocket API 创建的会话会自动显示到 TerminalHost GUI 标签页。用户可以看到终端的原始 VT 输出，也可以直接输入。这比只看到一条“命令已执行”和截断后的 stdout 更接近传统终端协作。

### 4. Windows 原生 ConPTY

TerminalHost 直接使用 Windows ConPTY，不依赖 `wt.exe`。它适合需要 PowerShell/CMD、ANSI/VT、控制键、终端尺寸变化和交互式控制台程序的 Windows 场景。

## 原生终端工具更合适的场景

TerminalHost 不是所有命令的更优路径。以下情况优先使用 Codex/Claude 原生工具：

- `git status`、`dotnet test`、`npm build` 等一次性命令。
- Agent 只需获得结构化退出码和有限 stdout/stderr。
- 不需要跨客户端共享进程。
- 不需要独立终端窗口或人工实时接管。
- 已依赖 Codex/Claude 自带沙箱、审批和工作区权限模型。
- Linux/macOS 环境；TerminalHost 当前只面向 Windows。

原生工具由 Agent 产品直接管理，少一层服务和协议，通常更简单、更快，也更容易与该产品自己的审批 UI 配合。

## TerminalHost 更合适的场景

- 长时间运行的开发服务器、编译监控、日志跟踪或 REPL。
- 必须使用原生 Windows PowerShell/CMD 和 ConPTY 的程序。
- 希望 LM Studio、Codex、Claude 等不同客户端使用相同终端工具。
- 希望 Agent 创建的终端在 GUI 中可见，并允许人工接管。
- 需要稳定 Session ID、会话列表和输出快照。
- 需要在终端层增加独立的工具白名单、目录规则、危险命令确认及审计。

## 安全边界不是互相替代

Codex 和 Claude Code 各自具有权限或审批机制；TerminalHost 也有自己的 MCP 安全策略。通过 MCP 调用时，两层控制可以同时存在：

```text
Codex / Claude / LM Studio 的工具审批
                │
                ▼
TerminalHost MCP 工具白名单与危险命令规则
                │
                ▼
当前 Windows 用户权限下的 ConPTY Shell
```

TerminalHost 当前提供：

- WebSocket 和 HTTP 服务仅监听 `127.0.0.1`。
- WebSocket 随机访问令牌；MCP HTTP 可配置 Bearer Token。
- MCP 工具允许列表。
- 创建会话时的允许目录列表。
- 可编辑的危险命令规则和 `confirmDangerous=true` 确认。
- MCP 授权、创建、输入和停止操作的审计日志。

但目录规则只限制会话的**初始工作目录**。Shell 启动后拥有当前 Windows 用户本身的权限，不能把 TerminalHost 的规则当作操作系统沙箱。处理不可信代码时，仍应使用低权限账户、虚拟机、容器或产品自带沙箱。

## 选择建议

| 你的需求 | 推荐 |
| --- | --- |
| 让 Codex/Claude 跑一次构建并返回结果 | 原生终端工具 |
| 启动服务并持续看日志 | TerminalHost，或客户端原生长进程能力 |
| 在多个模型客户端之间统一终端接口 | TerminalHost MCP |
| 让用户在独立窗口观察并接管 Agent 终端 | TerminalHost |
| 最低配置、最低单次调用延迟 | 原生终端工具 |
| 强依赖 Agent 产品自己的沙箱和审批体验 | 原生终端工具 |
| Windows 原生 PowerShell/CMD 交互程序 | TerminalHost |

## 官方参考

- [OpenAI Codex：Integrated terminal](https://learn.chatgpt.com/docs/integrated-terminal)
- [OpenAI Codex：Agent approvals & security](https://learn.chatgpt.com/docs/agent-approvals-security)
- [OpenAI Codex：MCP](https://learn.chatgpt.com/docs/extend/mcp)
- [Anthropic Claude Code：CLI reference](https://docs.anthropic.com/en/docs/claude-code/cli-usage)
- [Anthropic Claude Code：MCP](https://docs.anthropic.com/en/docs/claude-code/mcp)
- [Model Context Protocol：Transports](https://modelcontextprotocol.io/specification/draft/basic/transports)
