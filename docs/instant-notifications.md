# 右下角即时验证码提示卡

本派生版本在上游 v1.0.5 基础上增加以下功能：

- 验证码由后台直接绘制为右下角提示卡，不依赖 Windows 原生横幅的展示队列，不需要允许“重要通知”。
- 提示卡停留 8 秒，新验证码替换旧卡片；点击复制，显示时不激活窗口，不自动向其他程序输入。
- 自动复制默认关闭，可通过设置开关启用；提示卡始终保留手动复制入口。
- 无验证码的手机通知仍通过普通 Windows 通知转发；原通知保留在通知中心。若要避免重复横幅，可在 Windows 通知设置中关闭手机连接短信的横幅和声音，保留通知中心。
- 修正后台等待消息的方式，使用 `MsgWaitForMultipleObjectsEx` 和 `MWMO_INPUTAVAILABLE` 处理已被检查但未消费的窗口消息。
- 开发包采用独立的启动任务 ID，按需启动界面；关闭界面后后台继续工作。

## 从源码部署

在 Windows 上安装 .NET 8/9 SDK，按 [开发指南](development.md) 构建并使用 `scripts/Restart-Dev.ps1` 注册开发包。需要 MSIX 包身份与通知访问授权。

当前 x64 发布配置依赖系统 .NET 8 Runtime；应用清单显式声明 `Microsoft.WindowsAppRuntime.2`，最低版本 `2.5.1.0`。项目使用拆分的 Windows App SDK NuGet 包，避免引入未使用的 AI 组件。请先准备匹配的 Windows App Runtime，再部署。此派生版本尚未提供新的安装程序，上游 v1.0.5 安装包不含上述修改。

## 延迟诊断

包的 `LocalState/delivery-timing.jsonl` 记录以下阶段：

| 阶段 | 含义 |
| --- | --- |
| `listener-callback` | 收到 Windows 通知事件 |
| `dispatcher-ready` | 后台开始处理事件，并记录调度等待时间 |
| `phone-notification-read` | 读取并筛选通知，记录距系统创建时间的耗时 |
| `relay-start` | 开始显示验证码提示 |
| `card-shown` | 已同步绘制提示卡，记录窗口显示调用耗时 |
| `toast-accepted` | 原生通知 API 接受通知，不表示横幅已经展示 |
| `clipboard-complete` | 自动复制完成 |
| `clipboard-skipped-or-failed` | 自动复制关闭或失败，具体状态见应用记录 |
| `card-copy-complete` / `card-copy-failed` | 用户点击提示卡后的复制结果 |

日志不包含短信正文、验证码、手机号或账号，文件超过 1 MiB 时轮换，最多保留当前文件与上一份；可删除日志而不影响用户设置。

开发机本地模拟短信测试中，提示卡显示调用约 4–6 毫秒，发生在自动复制完成之前。该数字仅表示程序侧调用耗时，不是所有设备的屏幕呈现保证；真实短信的端到端延迟还包含手机连接转发时间。

## 验证清单

1. 关闭主界面，确认托盘与通知监听仍在运行。
2. 自动复制关闭时发送虚构短信：应显示卡片，剪贴板保持原值；点击卡片后才能粘贴新验证码。
3. 自动复制开启时再测试：应显示卡片并更新剪贴板。
4. 在另一应用输入时接收短信：提示卡显示不应中断输入；不会自动填入或提交验证码。
5. 连续发送两个验证码：卡片显示最新值，旧生命周期计时器不能提前关闭新卡片。
6. 用真实手机短信确认显示位置、复制按钮和端到端体验。

模拟通知脚本请加 `-KeepInActionCenter`，避免其默认清理动作删除该来源的通知历史：

```powershell
powershell -NoProfile -File scripts/Send-TestToast.ps1 -Chinese -Code 246810 -KeepInActionCenter
```

## 清理构建缓存

可以删除源码项目的 `bin/`、`obj/`，以及 `.local/` 内下载包、临时文件和构建日志。正在注册并运行的开发包位于 `.local/development-layout/`，不能作为缓存删除。不要删除包的用户设置或通知访问授权。
