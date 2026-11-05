# 知序 Fox · 希狐 Kiko（Unity 版）

Tauri 版桌宠（[zhixu-whale-maid-pet](https://github.com/yankcc258/zhixu-whale-maid-pet)）的 3D 希狐搬到 Unity：URP + lilToon + UniVRM + UniWindowController（透明窗口、鼠标穿透）。
在功能追上来之前，Tauri 版照常用。

## 现在是「阶段 1：技术验证」
目标是量出换引擎的 Go/No-go 五条：
1. 画质（lilToon）明显比 Tauri 版好
2. 待机内存 < 400MB，CPU/GPU 低到感觉不到
3. 透明窗口 + 鼠标只在她身上可点
4. 安装包 ≤ 150MB
5. 能自动打包（先在本机用命令行，之后再接 GitHub Actions）

阶段 1 的她能：站着呼吸、眨眼、看鼠标；拖着走（拎起来手脚垂下）、松手掉到任务栏上落地；戳一下缩一下。
**退出：按住 Shift 右键点她。** 日志：`%USERPROFILE%\AppData\LocalLow\yank\ZhixuFox\pet.log`（每 30 秒记一次内存/帧率）。

## 第一次打开（Unity 6000.3.28f1）
包（UniVRM v0.131.4、lilToon 2.4.1、UniWindowController v0.9.8）直接放在 `Packages/` 里（嵌入式包），不需要装 Git。

### 编辑器里（推荐）
Unity Hub → 添加 → 从磁盘添加项目 → 选这个文件夹 → 编辑器版本选 6000.3.28f1。打开后菜单栏 **Kiko** 依次点 1、2、3、4。
（编辑器里看不到透明效果，要打包出来看。）

### 命令行
⚠️ 在 Codex 之类的沙盒里跑会连不上 Unity 许可证服务（`LicenseClient refused`），要在普通 PowerShell 里跑。
```powershell
cd <仓库目录>
$U = "C:\Program Files\Unity\Hub\Editor\6000.3.28f1\Editor\Unity.exe"
& $U -batchmode -projectPath . -executeMethod Kiko.EditorTools.KikoSetup.Step1 -quit -logFile step1.log   # URP、播放器设置
& $U -batchmode -projectPath . -executeMethod Kiko.EditorTools.KikoSetup.Step2 -quit -logFile step2.log   # lilToon 材质、场景、打包
.\Builds\ZhixuFox\ZhixuFox.exe
```
日志里搜 `[Kiko]` 看每一步的结果；报错就把 step*.log 里 `error` 附近几行发给露娜。
跑完以后把 Unity 生成的 `.meta`、`ProjectSettings/`、`Assets/Kiko/Settings|Materials|Scenes` 提交上来（`Library/`、`Builds/` 不提交，.gitignore 已排除）。

## 目录
- `Assets/Kiko/Models/kiko.vrm`：和 Tauri 版 b18 同一个模型（kiko_white_v12）
- `Assets/Kiko/Scripts/`：`KikoPet`（行为）、`KikoPose`（姿势表，数值和 vrm.js 一样，three.js 欧拉角自动换算）、`Win32`（窗口位置/任务栏/鼠标）
- `Assets/Kiko/Editor/KikoSetup.cs`：一键设置/换材质/生成场景/打包
- `Assets/Kiko/Textures/prop_note.png`：特蕾莎画的狐狸便签（递便签动作用）

## 第三方
- `Packages/` 里原样嵌入：UniVRM（MIT）、lilToon（MIT）、UniWindowController（MIT），各自的 LICENSE 在包目录里
- 模型：知序原创角色希狐（VRoid Studio 制作）
