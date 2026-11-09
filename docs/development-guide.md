# 从源码构建

源码仓库包含 NadeLab 自有代码和 UI，不含 CS2、地图、运行框架、SDK 安装包或任何人的道具库。Windows 是当前开发与训练的主要平台。

## 1. 不安装 CS2 也能运行的检查

安装 Git、.NET 10 SDK、Python 3.11 或更新版本；Windows PowerShell 5.1 随 Windows 提供。推荐从 PowerShell 7 运行命令。首次构建需要访问 NuGet。

Windows 启动器测试会在系统临时目录下建立并清理隔离实例，并执行真实的短英文路径限制。若当前 `TEMP` / `TMP` 路径包含非 ASCII 字符或过长，请先为本次 PowerShell 进程指定一个可写的短英文临时目录；无需更改系统全局设置。该限制不等于已支持中文玩家安装路径。

在仓库根目录运行：

```powershell
./scripts/build.ps1 -SourceOnly
```

此入口编译六个解决方案项目，并运行核心、生命周期、Python 和 Windows 启动器测试。真实编译 UI 资源检查会明确显示 skipped；模拟资源、安装边界等自动测试仍会执行。此模式不写 `artifacts/release-build.json`，不打包、不安装，不启动 CS2，也不证明画面或手投结果正确。

脚本优先使用可选的 `.tools/dotnet/dotnet.exe`，否则使用 PATH 中的 .NET SDK。NuGet 缓存与 CLI 配置写入忽略的 `.tools`，源码生成文件只在 `bin`、`obj`、`artifacts` 中产生。无需把维护者的本地目录复制过来。

项目结构：

| 路径 | 职责 |
| --- | --- |
| `src/NadeLab.Core` | 投掷模型、碰撞抽象、搜索、操作和状态规则 |
| `src/NadeLab.Plugin` | CounterStrikeSharp 训练服插件及原生引擎接入 |
| `src/NadeLab.Input` | Windows 本地输入模块 |
| `src/NadeLab.Tool` | 离线研究与调试工具 |
| `ui` | NadeLab 自有 Panorama 界面与桥接脚本 |
| `tests` | 核心、生命周期及 Python 回归测试 |
| `scripts` | 构建、安装、训练工作台、诊断与发布脚本 |
| `docs/release` | 玩家和服务器操作文档 |

## 2. 编译界面与完整构建

在 Steam 中安装 CS2 和 Counter-Strike Workshop Tools。将下例路径换成自己的 CS2 安装目录：

```powershell
./scripts/build-panel-ui.ps1 -Cs2Root 'C:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive'
./scripts/build.ps1 -NoPackage
```

UI 编译脚本使用游戏附带的 ResourceCompiler，在该游戏安装下创建专用 `nadelab_panel_ui` 插件目录；它不会改写 Valve 基础 UI 文件。结果和源文件哈希写入 `artifacts/panel-ui`。编译工具及游戏文件从用户合法安装取得，不纳入源码仓库。

完整构建会检查真实 UI 产物，并在成功后记录当前源码和二进制指纹。`-SourceOnly` 成功不能替代这个步骤。UI 或源代码改变后必须重新生成相应资源和验证记录。

## 3. 私人训练服验证

按[服务器指南](release/server-guide.md)或安装脚本的参数准备本地实例，再使用 `工作台.cmd`。项目开发服与玩家安装实例的数据分开；工作台可用 `scripts/workspace.ps1 -InstalledRoot <路径>` 指定玩家工具位置。

先运行构建，再在目标训练服停止且没有待保存现场时安装：

```powershell
./scripts/install-plugin.ps1 -ServerRoot server
```

安装器会拒绝覆盖运行中的目标实例。只有对应的服务端插件、客户端 UI 和输入模块均匹配时，才进行菜单和手投验收。UI 更新后需要重新启动客户端。详细训练步骤见[训练与调试](训练与调试.txt)。

## 4. 制作发布包

完成 UI 编译后运行 `./scripts/build.ps1`，或在完整构建验证后运行 `python -s scripts/package-release.py`。打包器只接受当前源码对应的完整构建记录和匹配 UI；它不代表真人验收已完成。

二进制分发须遵守 GPL-3.0，并提供该版本的完整对应源码及构建脚本。发布时将安装包与准确的源码提交/源码归档关联，保留许可和依赖声明。不得把 `.tools`、游戏资源、玩家数据或本地配置作为对应源码上传。公开前按[发布验收清单](release/release-checklist.md)检查，未实测的功能如实写入[已知限制](release/known-limits.md)。

## 5. 验证的边界

CI 的源码检查不启动游戏。原生碰撞自检不等于实投一致；模型命中不等于烟雾封满或闪光符合战术目标。涉及安装、UI、输入和引擎行为的变更，应分别记录对应环境及证据。
