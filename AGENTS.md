# 仓库规则

这些规则给维护者和 AI 助手用。发版流程必须按这个来。

## 发版规则：每次发新版本

- 每次发 release 一律发新版本。版本号自动加一（默认补丁号 +1）。
- 已经发布的包不动、不覆盖、不重传。tag 已存在就停手。
- 发版走脚本：`powershell -File tools\release.ps1`。它按顺序做版本号、日志、构建、自检、
  界面快照、提交、打 tag、建 Release、传 zip。
- 想先看会发什么：`powershell -File tools\release.ps1 -DryRun`。
- 想只构建不发布：`powershell -File tools\release.ps1 -SkipPush`。
- 动过裁剪设置或升级依赖之后，加 `-TrimParity`，比对裁剪版与不裁剪版的快照。

## 更新日志必须有历史记录

- 累积日志在 `MidiKeyPlayer/docs/更新日志.txt`。版本从新到旧排。
- 每次发新版，只在最上面加一节。旧版本的记录不删、不改、不覆盖。
- 脚本会用上个 tag 以来的提交标题当日志草稿。措辞要更好，就自己写一份给 `-Notes`。
- 说明：`docs/更新说明.txt` 是当前版本的功能说明，不是日志。两份文件不要合并。
- 发布包（zip）里必须带 `更新日志.txt`，内容与源文件一致。
- `build-win.sh` 会核对：日志里没有当前版本号那一节，就拒绝打包。

## 手工发版步骤（脚本不能跑时照这个来）

1. 改 `MidiKeyPlayer/MidiKeyPlayer.csproj` 里的 `<Version>`，补丁号加一。
2. 在 `MidiKeyPlayer/docs/更新日志.txt` 最上面加当前版本一节。
3. 改 `MidiKeyPlayer/docs/更新说明.txt` 第一行的版本号。
4. 跑 `bash MidiKeyPlayer/build-win.sh`。产物在 `MidiKeyPlayer/release/` 下。
5. 跑 `tools/run-selftest.ps1 -ExePath <新 exe>`。退出码必须是 0。
6. 跑一次界面快照，确认界面正常（做法见 README 的「自己打包」）。
7. 提交并推送 main。打 tag `v<版本>`，发 Release，上传 zip。

## 发布包

- 只放两样东西：`MidiKeyPlayer.exe` 与 `更新日志.txt`。示例曲目不进包。
- 开裁剪（`PublishTrimmed`）。反射相关的程序集用 `TrimmerRootAssembly` 钉住：
  `MidiKeyPlayer`、`Avalonia` 系列、`Melanchall.DryWetMidi`。
  动裁剪设置或升级依赖之后，必须重跑自检与界面快照比对。
- 体积基线：exe 23.0 MB，zip 17.4 MB。明显变大就查原因。

## 其它

- 这个程序会发送模拟按键。文档与界面不写具体游戏名或软件名。
