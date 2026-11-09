# 独立训练服务器部署

适用于 0.2.0-rc.4。已有独立服务器的管理员使用插件包；单机玩家优先使用 [Windows 本地工具](install-and-recovery.md)。本版发布验收面向 Windows。

## 准备

- 一套可以正常启动的独立 CS2 训练服务器。
- 兼容的 Metamod:Source，以及 **CounterStrikeSharp 1.0.378 with-runtime**（修复 CS2 1.41.9.2 进服崩溃）；本插件面向 .NET 10，不能用旧 .NET 8 运行时代替。
- 插件包所带的全部 NadeLab 文件、同版客户端 UI 和按键文件。

从框架官方渠道准备运行环境。本地工具会按锁定的版本和校验值准备依赖；独立服务器由管理员安装框架。兼容性以本版验收记录为准，不承诺任意新旧版本可混用。

## 安装插件

1. 停止目标训练服务器。备份原有 NadeLab 插件目录、训练配置和 NadeLab 专用 UI 资源。
2. 将插件包的 `game/` 合并到**训练服务器根目录**，保留原有 `data/` 和自定义 `physics.json`。发布包只提供 `physics.example.json`，不会提供研发者的训练数据。
3. 确认以下文件位于对应位置：

```text
game/csgo/addons/counterstrikesharp/plugins/NadeLab/NadeLab.dll
game/csgo/addons/counterstrikesharp/plugins/NadeLab/NadeLab.Core.dll
game/csgo/addons/counterstrikesharp/plugins/NadeLab/NadeLab.deps.json
game/csgo/cfg/nadelab_practice.cfg
game/csgo/cfg/nadelab_keys.cfg
game/csgo/panorama/styles/custom_game/nadelab_panel.vcss_c
game/csgo/panorama/layout/custom_game/nadelab_panel.vxml_c
game/csgo/maps/scripts/nadelab_panel_bridge.vjs_c
```

4. 以独立训练实例启动，使用 `-dedicated -insecure`，加载本版支持的地图，例如 `de_mirage`。在服务器控制台执行：

```text
exec nadelab_practice
```

训练配置会启用作弊训练、无限热身与 NadeLab 训练开关。**插件默认关闭训练功能**；仅加载插件，或只有 `sv_cheats 1`，均不等于开启完整训练。

5. 服务器控制台检查框架和插件：

```text
meta list
css_plugins list
css_nl_selftest
```

原生自检通过说明地图碰撞及基础模拟管线可用，不等于所有投法已校准。出现加载失败时保留完整错误，不要仅靠 `connect` 反复重试。

服务器控制台执行 `css_nl_training 0` 可关闭训练能力，并清理训练状态、恢复玩家正常伤害开关。它不会替你恢复所有服务器比赛配置；需要切回其他模式时加载对应服务器配置。

## 玩家端界面与按键

只在服务器安装 DLL 不足以显示完整界面。每位玩家关闭 CS2 后，将包中的三个 NadeLab 专用 UI 资源按相同相对路径放入客户端 `game/csgo/`，并将 `nadelab_keys.cfg` 放入客户端 `game/csgo/cfg/`。先备份同名旧文件。

优先使用 Windows 本地工具的客户端安装/恢复流程管理 UI 备份。默认 `nadelab_keys.cfg` 不含绑定命令，执行它不会安装 F 键或滚轮快捷键，也不需要修改日常 `autoexec.cfg`。旧快捷键兼容模式仅在明确选择后安装，备份和恢复见 [安装与恢复](install-and-recovery.md)。

本轮 Windows 输入模块只连接同一电脑、同一用户的本地训练服，不支持远程独立服务器。连接模块后使用 F1 菜单、滚轮/↑↓选择、E确认、R返回；WASD保留移动，菜单关闭后滚轮恢复原功能。Shift+R换站位、Shift+E重选目标，F3沿用研究道具切换；菜单内不使用Shift组合键导航。

远程服务器或模块未连接时，聊天 `!nl` 打开菜单，并按画面显示的降级操作提示使用；原生降级模式按游戏的使用、换弹、静步动作识别，不要求个人键位是E/R/Shift。本地输入模块的实测结果不能代替远程客户端验收。安装或更新 UI 后重开 CS2，避免继续使用旧布局缓存。`css_nl_inputstatus` 可查看实际收到的菜单输入；诊断直接调用不计入真人输入计数。

## 地图与多人

支持十张地图，只提供本实例已安装的地图选项。Mirage 的 VIP 预设不会出现在其他图；各图记录和版本独立。

游戏内 F1 → 切换地图。单人可自行确认；多人时需要 CounterStrikeSharp 的切图权限 `@css/changemap` 或根权限。切图会清除当前训练状态和假人，保留已保存数据。

全服最多八个效果假人，一次只执行一个搜索。其他玩家正在搜索时稍后重试。多人辅助线隔离、并发及第三方插件组合尚未完成完整发布验收；参见 [已知限制](known-limits.md)。

## 更新与回退

先停服，完整备份插件和数据，再替换同版程序及 UI；更新完成后冷启动。客户端也应更新配套 UI。不要在有人训练时热重载原生接口相关插件。

回退时恢复上一个完整插件版本和对应 UI，保留新数据副本。不要把“程序可以加载”当作新旧记录完全兼容的证明。卸载插件不需要删除玩家数据；移除前保存自己的投法和记录。
