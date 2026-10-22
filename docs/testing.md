# 验证与测试

此文档记录 1.0.7 的实际验证范围，并给出可复现的检查方法。原始个人媒体、桌面截图、机器日志和本地开发工具不随仓库发布。

## 已完成的本地验证

2026-10-22，在 Windows 11 25H2、64 位普通用户桌面会话中执行：

| 检查项 | 结果 |
| --- | --- |
| Release / x64 构建 | .NET SDK 10.0.402；0 警告、0 错误 |
| 原生 COM 回归 | Media type、sample 时间戳、AddBuffer、Convert/Lock 通过 |
| MP4 | 2560×1440 / 60fps 素材解码、帧导出及 seek 后继续解码通过 |
| 方向与行布局 | 检查真实 pitch、导出帧方向和 RGB24 行对齐 |
| 持续绘制与循环 | 实际宿主持续绘制，10 秒视频完整循环 3 次 |
| 暂停与恢复 | 暂停时绘制计数不增长，恢复后继续增长 |
| GIF | 解码、宿主绘制、暂停与恢复通过 |
| 无效媒体 | 非视频内容的 MP4 返回非零退出码，不误报成功 |
| 主界面 | 启动、导入 MP4/GIF/JPG、选择并应用通过 |
| 桌面与图标 | 真实桌面截图确认动态画面、图标和任务栏同时可见；用户确认图标可以正常选中 |
| 正常退出 | 开启恢复选项后，恢复使用前的系统壁纸通过 |
| 原交付包内容回归 | 解压后的应用与构建输出哈希一致，COM、媒体和错误输入回归通过 |

上述是单一环境下的验证记录。它不代表所有 Windows 版本、媒体编码、显卡或桌面扩展均兼容。原交付包的检查不等于安装、升级和卸载流程已完整验收；开源发布包重新构建后应按同样步骤检查。

## 尚未完整验收

- 全屏应用、锁屏、睡眠、电池供电和远程会话中的完整自动暂停/恢复流程。
- Explorer 重启后的桌面重新挂载。
- 多显示器和每屏独立播放；当前动态画面只面向主显示器。
- Windows 10 和不同 Windows 11 构建版本的兼容性。
- 安装、升级、卸载及长期资源稳定性。

实现中存在对应处理，不应据此报告测试通过。短时资源采样也不能证明长期无泄漏。

## 构建检查

~~~powershell
.\setup.ps1
~~~

或分别执行：

~~~powershell
dotnet build .\src\WhalePaper\WhalePaper.csproj -c Release --disable-build-servers
dotnet build .\tests\DesktopPlaybackSmoke.csproj -c Release --disable-build-servers
~~~

GitHub Actions 在 Windows 上构建这两个项目并检查免安装打包。CI 不运行交互式桌面探测，也不验证媒体播放或图标可见。

## 媒体回归

准备自己有权使用的短视频，至少 30 帧；可选一份 GIF。在仓库根目录运行：

~~~powershell
.\tests\Verify-Media.ps1 -Executable '.\src\WhalePaper\bin\Release\net48\WhalePaper.exe' -Video 'D:\wallpapers\test-video.mp4' -Gif 'D:\wallpapers\test-animation.gif' -OutputDirectory '.\testdata-verification'
~~~

该脚本使用独立数据目录，依次检查真实 Media Foundation COM sample/buffer、视频帧导出、seek 后解码、GIF 解码和无效输入退出码。脚本通过后仍需打开导出帧，确认方向、颜色与内容；它不会证明桌面实际可见。

可选的 tools/make_samples.py 使用 Python 与 Pillow 生成合成图片和 GIF。普通构建不需要 Python；仓库不附带私人媒体或 Python 环境。

## 真实桌面宿主测试

在本地可见桌面会话中，先构建测试程序，再准备媒体和一张海报：

~~~powershell
.\tests\bin\Release\net48\DesktopPlaybackSmoke.exe '.\src\WhalePaper\bin\Release\net48\WhalePaper.exe' 'D:\wallpapers\test-video.mp4' 'D:\wallpapers\test-poster.png' '.\testdata-desktop' 30
~~~

参数依次为正式应用、视频或 GIF、海报、独立数据目录、额外持续测试秒数。可选第六个参数为真实屏幕截图的输出路径；截图目录需预先存在。

测试直接调用正式宿主，检查首帧实际绘制、连续帧计数、绘制上限、桌面父窗口/Z 序、暂停与恢复。它会临时在桌面放置播放窗口，结束后清理；不主动改系统静态壁纸、重启 Explorer 或锁屏。

将遮挡桌面的窗口最小化后，分别检查：

1. 桌面确实在播放，画面方向正常。
2. 桌面图标和任务栏可见。
3. 单击桌面图标可以选中，桌面操作正常。
4. 正常退出后宿主窗口消失；若设置了恢复选项，原壁纸恢复。

程序的绘制计数与自动像素检查不能替代这四项人工验收。可选截图使用真实屏幕采集；不得把解码帧合成为桌面截图并当作证据。

## 发布前

构建后检查安装版与免安装包包含程序、配置、说明和许可证；核对发布的 SHA256.txt。使用干净用户环境执行安装、运行、升级和卸载，并将尚未执行的检查明确列出。仅将已有权发布的源代码、原创素材和许可证提交到仓库。

