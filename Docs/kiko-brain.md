# 希狐的脑子：和知序共用记忆

桌面上的希狐通过知序服务器的 `/api/pet/*` 说话、记事，和网页上的她是同一份记忆（知序 PR #111）。

## 配钥匙（只做一次）

1. Cloudflare → Zero Trust → Access → Service Auth → Service Tokens → **Create Service Token**，名字 `kiko-desktop`，有效期选不过期。马上复制 **Client ID** 和 **Client Secret**（Secret 只显示一次）。
2. Access → Applications → 知序 → Policies：Service Auth 那条规则（obsidian-sync 用的）里把 `kiko-desktop` 也加进去。
3. Cloudflare 后台 → Workers & Pages → yank-study-lab → Settings → Variables and Secrets → Add：类型选 **Secret**，名字 `PET_CLIENT_ID`，值是 Client ID（结尾 `.access`）。（知序在 GitHub 上、由 Cloudflare 自动部署，本地不需要知序文件夹；选 Secret 是因为普通变量会在下次部署时被 wrangler.toml 覆盖掉。）
4. 桌宠第一次启动会自动建 `%APPDATA%\ZhixuFox\brain.json`。用记事本打开，填：
   ```json
   {
     "base": "https://yank-study-lab.yank-study-lab.workers.dev",
     "clientId": "xxxx.access",
     "clientSecret": "xxxx"
   }
   ```
   保存后重启桌宠。这个文件只在你电脑上，不进仓库。

## 她会做什么

- 右键「说句话」：头顶弹出输入框，回车发送、Esc 取消。她用知序的人设、今天的课和饮食（网页最近一次整理的）、记仇本、两边合在一起的聊天记录回话，表情跟着变。
- 摸摸头 = 还记仇本的账（每天最多 3 次）；连戳生气、戳跑、睡着被戳醒 = 记账。
- 开机出来、睡着/睡醒、变小去窗口上坐/躺、趴任务栏、被叫回来、被戳跑……都记进共用记忆，网页上的她聊天时也知道。窗口标题不会上传。
- 闲下来自言自语时，三成概率改成「想起前几天的事」（知序长期记忆 /api/pet/muse，每天最多 6 次；知序 PR #112）。
- 日志（pet.log）里 `brain:` 开头的行：online/offline、聊天成败。403 = 钥匙不对或 Access 规则没加。
