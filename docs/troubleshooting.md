# 故障排查

先记录 WhalePaper 版本、Windows 版本及系统内部版本、媒体格式/编码，以及使用的是安装版还是免安装版。

## 视频只显示海报

1. 先确认文件能在 Windows 自带播放器中正常播放。建议用短 H.264 MP4 排除编码问题。
2. 在设置中运行“运行环境检测”，检查数据目录下 Logs/self-test.txt。
3. 单独运行媒体探测：

~~~powershell
Start-Process -FilePath '.\WhalePaper.exe' -ArgumentList @('--media-probe', '"D:\wallpapers\test-video.mp4"', '--quiet') -Wait
~~~

报告位于默认数据目录下 Logs/media-probe.txt；帧图位于 Logs/media-probe。免安装启动脚本使用 data，直接双击 EXE 则使用默认数据目录。

如果媒体探测失败，先检查系统解码器和文件本身。如果媒体探测成功而桌面仍不播放，则继续检查桌面挂载、图标层和是否被自动暂停。解码成功不等于实际桌面绘制成功。

## 图标不见或动态画面被挡住

先退出 WhalePaper，确认桌面恢复。避免同时运行其他动态壁纸软件或桌面替换工具，然后重新启动。

运行桌面探测：

~~~powershell
Start-Process -FilePath '.\WhalePaper.exe' -ArgumentList '--desktop-probe' -Wait
~~~

报告位于 Logs/desktop-probe.txt。采样时将遮挡桌面的窗口最小化；被遮挡的屏幕不能证明壁纸图层实际可见。请记录图标是否可见、单击能否选中，以及系统内部版本。系统更新可能改变桌面窗口行为。

## 关闭窗口后仍在播放

关闭主窗口会隐藏到系统托盘。右键托盘图标，选择暂停或退出。若需要退出时恢复之前的静态壁纸，先在设置中开启对应选项；默认不会启用恢复。

## 免安装版没有保存设置

完整解压 ZIP 到当前用户可写的文件夹，然后运行 WhalePaper-portable.cmd。启动脚本将数据放在程序旁边的 data 文件夹；直接运行 WhalePaper.exe 使用默认的 %LocalAppData%\WhalePaper。

如果数据根目录不可写，应用会尝试使用临时目录。请优先选择可写位置，不要把免安装版留在压缩包内运行。

## 安装、升级或卸载异常

安装程序默认使用当前用户目录。先退出托盘中的应用，确认目标文件未被占用，再重试。若失败，可以使用免安装版，并附上已经脱敏的错误信息。安装、升级和卸载尚未完成完整场景验收，请勿仅凭构建成功判断流程可靠。

安装包尚未代码签名。可先核对 GitHub 同次 Release 发布的 SHA256.txt；校验和用于确认文件一致性，不等同于发布者身份签名。

## 日志在哪里？

默认位置：

~~~text
%LocalAppData%\WhalePaper\Logs\app.log
%LocalAppData%\WhalePaper\Logs\self-test.txt
%LocalAppData%\WhalePaper\Logs\desktop-probe.txt
%LocalAppData%\WhalePaper\Logs\media-probe.txt
~~~

使用 --data-dir 或免安装脚本时，位置随数据根目录变化。提交 Issue 前仅保留相关片段，并移除个人路径、文件名、账号信息和桌面内容。私人媒体与完整桌面截图无需上传。

