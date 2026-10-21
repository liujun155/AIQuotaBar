# AI Quota Bar

Windows 任务栏 AI 配额监控工具。在通知区域旁，用双行胶囊查看剩余额度，点击展开账户详情与重置倒计时。

基于 **WPF + .NET 10**，集成 OpenAI Codex 与 Gemini（通过 Antigravity）的配额查询，并提供 Claude Code 演示界面。

[快速开始](#快速开始) · [平台支持](#平台支持与数据来源) · [使用说明](#使用说明) · [构建与发布](#构建与发布) · [常见问题](#常见问题)

## 界面预览

<p align="center">
  <img src="docs/images/capsule-preview.png" alt="任务栏双行胶囊，显示配额标签、进度条与剩余百分比" width="300" />
</p>

<table>
  <tr>
    <td align="center" valign="top">
      <b>配额详情</b><br />
      <sub>平台切换、账户信息与重置倒计时</sub><br /><br />
      <img src="docs/images/flyout-preview.png" alt="配额详情浮窗" width="280" />
    </td>
    <td align="center" valign="top">
      <b>设置中心</b><br />
      <sub>刷新策略、颜色阈值、平台开关与开机自启动</sub><br /><br />
      <img src="docs/images/settings-preview.png" alt="暗色设置中心" width="520" />
    </td>
  </tr>
</table>

## 主要功能

- **任务栏双行显示**：通过进度条与百分比展示剩余额度，支持短期、周度或模型配额标签。
- **快速切换平台**：在胶囊上滚动鼠标滚轮，或在详情浮窗中切换平台。
- **配额详情与倒计时**：查看账户、套餐、配额状态和重置时间；倒计时在详情浮窗可见时每秒更新。
- **可调刷新策略**：默认每 120 秒查询当前平台；任一配额低于 20% 时，加速至每 60 秒查询一次。
- **限流退避**：默认遵循接口返回的 `Retry-After`，延后下一次自动查询。
- **颜色分级**：默认剩余 ≥ 50% 为绿色，20% ≤ 剩余 < 50% 为黄色，剩余 < 20% 为红色；阈值可调整。
- **暗色设置中心**：配置刷新周期、低额度加速、平台开关和 Windows 开机自启动。
- **资源占用优化**：采用 WPF 软件渲染与工作集修整。实际内存占用、程序体积取决于运行环境和发布方式。

## 平台支持与数据来源

| 平台 | 当前状态 | 数据来源与前提 |
| --- | --- | --- |
| OpenAI Codex | 已接入配额查询 | 读取本机登录凭证，请求 ChatGPT 配额接口，解析 `5h` / `7d` 窗口 |
| Gemini · Antigravity | 已接入配额查询 | 自动发现运行中的 Antigravity 本地语言服务，读取 Gemini 模型额度及可用的周期额度 |
| Claude Code | 演示实现 | 当前返回固定示例额度；即使检测到凭证，也不会查询真实 Claude 配额 |

### Codex

请先在本机完成 Codex 登录。程序按以下顺序查找凭证：

1. `%USERPROFILE%\.codex\credentials.json`
2. `%USERPROFILE%\.codex\auth.json`
3. `%APPDATA%\codex\credentials.json`

当前实现需要凭证文件中包含访问令牌。未找到可用凭证时，程序显示演示数据；登录失效时，需要在 Codex 中重新登录后刷新。

### Gemini（通过 Antigravity）

启动 Antigravity 并登录账号，保持其本地语言服务运行。程序自动探测进程与监听端口，通过本地 RPC 接口获取数据，无需手动填写端口。

- 接口返回明确的周期信息时，显示 `5h` / `7d` 配额。
- 未提供周期额度时，显示 `Pro` / `Flash` 等模型额度；同一模型系列取已知的最低剩余值。
- 没有检测到本地服务或接口未返回额度时，显示错误状态。

此接入方式依赖 Antigravity 本地接口，不能通过单独设置 `GEMINI_API_KEY` 使用，也不代表 Gemini API 的计费余额。

> **数据准确性**：Claude 当前仅用于界面演示。Codex 在缺少凭证时使用演示数据；当前解析逻辑在响应缺少可识别的配额窗口时，也会使用预设值。涉及实际可用额度时，请同时核对对应平台。

## 快速开始

### 运行现成程序

1. 准备 Windows 10 / Windows 11 环境，并安装 **.NET 10 Desktop Runtime（Windows 桌面运行时，x64）**。
2. 在本机登录需要查询的服务；使用 Gemini 时，启动 Antigravity 并保持登录。
3. 双击仓库中的 `dist/AIQuotaBar.exe`，在任务栏通知区域旁查看胶囊。
4. 右键胶囊打开设置，按需调整平台开关、刷新策略和开机自启动，点击「保存并应用」。

当前单文件发布方式依赖系统安装的桌面运行时。仅安装普通 .NET Runtime 不满足 WPF 应用的运行需求。开发环境安装 .NET 10 SDK 后，可直接从源码运行。

### 从源码运行

在项目根目录执行：

```powershell
dotnet run --project src/AIQuotaBar.App/AIQuotaBar.App.csproj
```

## 使用说明

| 操作 | 行为 |
| --- | --- |
| 鼠标悬停胶囊 | 显示配额提示 |
| 左键单击胶囊 | 打开或隐藏详情浮窗 |
| 在胶囊上滚动滚轮 | 在设置中已启用的平台之间循环切换 |
| 右键单击胶囊 | 打开菜单，可立即刷新、切换平台、打开设置或退出 |
| 点击详情浮窗中的平台标签 | 切换当前平台并触发查询 |

胶囊中的百分比表示**剩余额度**。标签随接口返回的数据变化，不一定始终为 `5h` / `7d`；只有一个配额窗口时，第二行隐藏。查询失败且没有配额数据时，显示 `--`。

自动轮询针对**当前选中的平台**，切换平台会触发查询。详情中的倒计时为本地显示更新，不会每秒请求接口。

### 配置位置与默认值

设置保存在 `%APPDATA%\AIQuotaBar\settings.json`，建议通过设置中心修改。

| 设置项 | 默认值 |
| --- | --- |
| 常规刷新间隔 | 120 秒 |
| 低额度加速 | 开启 |
| 加速触发条件 | 任一配额剩余 < 20% |
| 加速刷新间隔 | 60 秒 |
| 遵循 `Retry-After` | 开启 |
| 绿色 / 黄色阈值 | 50% / 20% |
| 启用的平台 | Codex、Claude、Gemini |

开机自启动可在设置中心的常规选项中开启或关闭。若移动了可执行文件，请重新保存自启动设置以更新路径。

## 构建与发布

### 开发环境

- Windows 10 / Windows 11
- .NET 10 SDK

以下命令均在项目根目录执行：

```powershell
# 恢复依赖并构建解决方案
dotnet build

# 运行自动化测试
dotnet test
```

测试涵盖刷新调度、限流退避、Gemini 配额解析和任务栏布局等逻辑。

### 发布为单文件

生成依赖系统桌面运行时的 Windows x64 可执行文件：

```powershell
dotnet publish src/AIQuotaBar.App/AIQuotaBar.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -o dist
```

输出文件为 `dist/AIQuotaBar.exe`。如需将 .NET 运行时一并打包，将 `--self-contained false` 改为 `--self-contained true`；发布体积会相应增加。

### 目录结构

```text
AIQuotaBar/
├── docs/images/                 # README 界面截图
├── src/
│   ├── AIQuotaBar.Core/          # 配额模型、查询与刷新逻辑
│   │   ├── Credentials/         # 登录凭证读取、Antigravity 本地服务发现
│   │   ├── Models/              # 配额窗口与快照
│   │   ├── Providers/           # Codex、Gemini 与 Claude 演示提供商
│   │   ├── Scheduling/          # 自动刷新、低额度加速与限流退避
│   │   └── Settings/            # 配置读取与保存
│   ├── AIQuotaBar.App/           # WPF 应用
│   │   ├── Assets/              # 平台图标
│   │   ├── Converters/          # 配额颜色转换
│   │   ├── Interop/             # Win32 任务栏定位、鼠标输入与自启动
│   │   ├── Themes/              # 暗色控件样式
│   │   └── Views/               # 胶囊、悬停提示、详情与设置窗口
│   └── AIQuotaBar.Tests/         # 自动化测试
├── dist/                        # 发布输出
├── AIQuotaBar.slnx              # 解决方案
├── LICENSE
└── README.md
```

## 常见问题

**为什么 Codex 显示演示额度？**

程序未在支持的路径中找到可用访问令牌。先在 Codex 中完成登录，再右键胶囊选择立即刷新。若提示登录状态过期，请重新登录。

**为什么 Gemini 提示未检测到本地服务？**

确认 Antigravity 已启动、已登录，且本地语言服务正在运行。重启 Antigravity 后可手动刷新，程序会重新发现端口。

**为什么 Gemini 显示 Pro / Flash，而不是 5h / 7d？**

当前接口只返回了模型额度，没有可识别的周期额度。程序保留模型标签，不根据重置时间推测配额周期。

**为什么 Claude 有百分比，却与实际使用情况不同？**

Claude 提供商目前返回固定示例数据，尚未接入真实配额查询。可在设置中关闭该平台。

**为什么出现限流后自动刷新变慢？**

启用「遵循 Retry-After」时，程序会按接口要求延后下一次自动查询，以减少重复请求。

**配置和登录信息存在哪里？**

应用设置保存在 `%APPDATA%\AIQuotaBar\settings.json`。Codex 登录信息从上文所列的本机凭证路径读取；Antigravity 本地服务的 CSRF 令牌仅保存在内存中。

## 致谢

感谢 [upstream-ray/codex-usage-monitor](https://github.com/upstream-ray/codex-usage-monitor) 提供的灵感与任务栏吸附思路。

## 许可证

本项目基于 [MIT License](LICENSE) 开源。
