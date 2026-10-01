# LoL 简体中文配置工具

Windows 桌面工具，将 League of Legends / Riot 的指定语言配置改为 `zh_CN`。提供持续检测保护版、普通图形界面和命令行版，源代码采用 [MIT 许可证](LICENSE)。

## 下载与运行

前往 [Releases](https://github.com/xiegaoxiao/lol-zh-cn-tool/releases/latest) 下载 `LoL-ZhCN-Tool-v1.0.0-win.zip`，解压到可写目录后运行：

| 程序 | 用途 |
|---|---|
| `LoL-ZhCN-Guard.exe` | 持续检测、修复并保护单个产品配置，推荐从这里开始 |
| `LoL-ZhCN-GUI.exe` | 普通配置工具，可同步游戏和 Riot 启动器语言，默认不持续锁定文件 |
| `LoL语言工具-cli.exe` | 普通配置工具的命令行版本 |

需要 Windows 10/11 与 .NET Framework 4.x。编译使用 Framework 4 编译器；实际验证范围见下文。

## 持续检测保护版

界面只有“改为简体并保护”和“停止保护”两个按钮。开启时结束 Riot 客户端，备份并修改以下唯一目标的 `default_locale` 和 `settings.locale`：

```text
C:\ProgramData\Riot Games\Metadata\league_of_legends.live\league_of_legends.live.product_settings.yaml
```

后台每秒核验目标文件、两个语言字段以及当前路径和保护句柄的 NTFS 文件标识。发现语言被改回或旧锁失效时，重新修复并加锁。文件或目录消失时，利用工具目录中的 `product-recovery.snapshot` 重建这个 YAML，不重建其他 Riot 元数据文件。

快照保存已经核验的完整配置，绑定目标路径并校验 SHA-256。格式错误、快照损坏或权限不足时显示异常并重试，不编造配置；没有快照且目标已消失时，需要先让 Riot 重建配置。异常期间按 1～10 秒间隔重试，“正在自动修复”不表示保护正常。

关闭控制窗口后仍在后台运行；重启电脑或结束后台进程后需重新开启。停止保护会停止检测和自动修复、释放锁并取消目标文件只读属性，保留当前语言和备份。权限不足时主按钮显示“授权并重试”。不安装 Windows 服务。

**文件保护可能妨碍 Riot 保存配置、启动或更新；遇到问题先点击“停止保护”。** 配置为 `zh_CN` 不保证客户端实际使用简体中文或具备全部语言资源。本工具不修改游戏程序、Vanguard 或 NTFS 权限。

命令行入口：`LoL-ZhCN-Guard.exe --guard` 开启后台保护，`LoL-ZhCN-Guard.exe --stop` 停止。

## 普通图形界面与命令行版

“应用简体中文”会结束 Riot 客户端及其子进程，检测到对局进程时拒绝操作。备份、修改并验证所选配置，默认不设置只读、不持续锁文件。游戏配置和 Riot 启动器配置可分别取消勾选。

| 配置 | 修改字段 |
|---|---|
| 上述产品配置 | `default_locale`、`settings.locale` |
| 游戏安装目录下 `Config\LeagueClientSettings.yaml` | `locale` |
| 游戏安装目录下 `system.yaml` | `default_locale` |
| `%LOCALAPPDATA%\Riot Games\Riot Client\Config\RiotClientSettings.yaml` | 两处 `locale` |

只修改语言字段，保留编码、换行、注释和其他设置。字段数量不符合预期时停止；多文件应用失败时尝试回滚本次修改。默认取消产品配置只读，其他配置保留原有属性。“额外设置产品配置只读”是默认关闭的实验选项，可能妨碍启动或更新。

“允许启动/更新”只取消产品配置只读，保留语言和备份；若保护版仍在后台持锁，需要先在保护版中停止保护。

```powershell
.\LoL语言工具-cli.exe --status
.\LoL语言工具-cli.exe --apply --sync --riot
.\LoL语言工具-cli.exe --allow-updates
.\LoL语言工具-cli.exe --restore --sync --riot
```

`--sync` 同步两处游戏配置，`--riot` 同步 Riot 启动器配置；省略时只操作产品配置。`--readonly` 启用实验性只读模式。可指定 `--path "配置路径"` 和 `--install "游戏目录"`。权限不足时以同一账号管理员身份运行。

## 备份与恢复

每个修改的配置旁创建 `.lol-zh-cn.bak` 和 `.lol-zh-cn.bak.meta`，保存首次备份、原始属性和 SHA-256；两文件必须一起保留，重复应用不会覆盖首次备份。普通工具中的“恢复原始配置”会先结束客户端，再逐个恢复所选备份；若途中失败，已恢复文件保持恢复状态，日志会报告错误。

若保护版正在运行，恢复前先停止保护。更新后需要以新配置作为基准时，先恢复、完成官方更新，再归档旧备份并重新应用。

## 从源代码构建

使用 Windows 自带的 .NET Framework C# 编译器，不需要 NuGet 依赖。在项目根目录执行：

```powershell
.\build-guard.ps1 -OutputName 'LoL-ZhCN-Guard.exe'
.\build.ps1 -GuiName 'LoL-ZhCN-GUI.exe'
.\test.ps1
```

产物位于 `dist`。源码包含配置引擎、普通界面、持续检测保护和自动测试。日志、用户配置快照、备份、编译产物和本机测试记录不纳入 Git。

现有测试仅操作 `tests` 下的临时目录，覆盖字段修改、编码与换行、备份校验、恢复属性、多文件回滚、锁定行为、语言自动修复、文件及目录丢失后的恢复、损坏快照拒绝。测试通过不等于 Riot 实际启动兼容性验证，也不代表已验证所有 Windows 版本。

本项目是独立工具，与 Riot Games 无隶属关系。
