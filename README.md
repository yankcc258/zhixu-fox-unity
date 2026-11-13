# 知序 Fox · 希狐 Kiko（Unity 版）

Tauri 版桌宠（[zhixu-whale-maid-pet](https://github.com/yankcc258/zhixu-whale-maid-pet)）的 3D 希狐搬到 Unity：URP + lilToon + UniVRM + UniWindowController（透明窗口、鼠标穿透）。
当前代码已包含聊天气泡、睡觉、换装、与知序共用的聊天记忆及本机 GPT-SoVITS 语音。下面保留早期迁移目标作为背景。

## 早期技术验证目标（历史）
目标是量出换引擎的 Go/No-go 五条：
1. 画质（lilToon）明显比 Tauri 版好
2. 待机内存 < 400MB，CPU/GPU 低到感觉不到
3. 透明窗口 + 鼠标只在她身上可点
4. 安装包 ≤ 150MB
5. 能自动打包（先在本机用命令行，之后再接 GitHub Actions）

阶段 1 的她能：站着呼吸、眨眼、看鼠标；拖着走（拎起来手脚垂下）、松手掉到任务栏上落地；戳一下缩一下。
**退出：按住 Shift 右键点她。** 日志：`%USERPROFILE%\AppData\LocalLow\yank\ZhixuFox\pet.log`（每 30 秒记一次内存/帧率）。

## 阶段 2（进行中）：把 Tauri 版的本事搬过来
- ✅ 心情 / 关键帧动作 / 表情：`Tools/export_acts.js` 从 vrm.js 导出到 `Resources/kiko_acts.json`（40 个动作、25 种表情），求值规则和 evalClip 一样
- ✅ 待机小动作：没人理她时每 6–15 秒随机来一个
- ✅ 真人动捕：`Tools/bake_vrma.py` 把 Tauri 版的 .vrma 烘成 `Resources/kiko_motions.json`（走路、掉落、落地、跳、挥手、加油、玩手机、喝水…），
  换算和 three-vrm 完全一样，幅度/「收着点」照 blendMotion 做好；手指、肩膀、脚也跟着动
  ```
  node Tools/export_acts.js <zpm>/src/vrm.js Assets/Kiko/Resources/kiko_acts.json
  python3 Tools/bake_vrma.py <zpm>/src/vrm/motion Assets/Kiko/Resources/kiko_acts.json Assets/Kiko/Resources/kiko_motions.json
  ```
- 编辑器里试玩：Game 视图里按 **0–9** 换心情（idle/happy/angry/surprised/smug/sleepy/pat/wave/pout/excited），**F1** 依次播每个动作
- 已接入：对话气泡/台词、睡觉、贴边休息、换装、知序聊天与 GPT-SoVITS 语音（见 `KikoTalk` / `KikoBrain` / `KikoVoice`）。自动更新等迁移工作仍单独推进。

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

### GitHub Actions 自动打包
`.github/workflows/build.yml`（game-ci）：Actions 页面手动点 Run workflow，或推 `v*` 标签自动发 Release。
第一次要在仓库 Settings → Secrets 里加 `UNITY_EMAIL`、`UNITY_PASSWORD`、`UNITY_LICENSE`（`C:\ProgramData\Unity\Unity_lic.ulf` 全文），只能主人自己加。

## 目录
- `Assets/Kiko/Models/kiko.vrm`：和 Tauri 版 b18 同一个模型（kiko_white_v12）
- `Assets/Kiko/Scripts/`：`KikoPet`（行为）、`KikoPose`（姿势表，数值和 vrm.js 一样，three.js 欧拉角自动换算）、`Win32`（窗口位置/任务栏/鼠标）
- `Assets/Kiko/Editor/KikoSetup.cs`：一键设置/换材质/生成场景/打包
- `Assets/Kiko/Textures/prop_note.png`：特蕾莎画的狐狸便签（递便签动作用）

## 第三方
- `Packages/` 里原样嵌入：UniVRM（MIT）、lilToon（MIT）、UniWindowController（MIT），各自的 LICENSE 在包目录里
- 模型：知序原创角色希狐（VRoid Studio 制作）
- 动捕：fumi2kick「使いどころに困るモーションセット」（CC0，BOOTH 5527394）、Josie 动画重定向包的走路（CC0，BOOTH 7861818）；
  掉落/落地/跳/待机两段来自 Mixamo（用 Tauri 版 tools/vrm3d/fbx2vrma.py 重定向），只随本程序一起分发


## v1.1.0 · Agent 提醒接入（2026-11-13）

每分钟通过现有服务身份检查知序的「待你决定」。只在空闲时显示一条问题，可沿用现有语音读出；静音（Chatty=0）、隐藏、睡觉、全屏、正在输入/聊天/说话时不插话。气泡实际显示后才发送回执，断网重启只补回执，不再重复展示，十五分钟内不接着问下一题。

聊天里的学习记忆问题（`offered`）不会另外播报；收到 `answered` 时显示「回答已记下，稍后批改」。评分沿用网页或后台处理，不在桌面编造分数。编号和回执存在按站点/服务身份隔离的 PlayerPrefs 中，不保存问题正文、回答或服务密钥。

后端先合并知序 #161 → #162 → #163 → #164 → #165 → [#166（幂等回执）](https://github.com/yankcc258/yank-study-lab/pull/166)，再合并本次桌面接线。`brain.json` 继续在本机配置，不提交仓库。

验证：`.NET 8` 执行 `dotnet run --project Tests/Attention/Attention.csproj`。它编译实际 `KikoBrain.cs` 与回执状态类，用替身提供 Unity UI/网络，覆盖静音、睡觉、全屏、输入、请求中变忙、断网重启及聊天去重；不代表 Unity/Windows 或语音引擎实测。Actions 有同一套无需 Unity 许可证的检查。

Windows 接力：打开 Unity 编译；配置现有本机凭据；检验全屏/睡觉不插话、断网显示后重启不重复、聊天回答在网页待批改、语音关闭仍能看到气泡。没有改自动发布流程，也没有触发正式安装包发布。
