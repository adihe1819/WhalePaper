鲸鱼壁纸 WhalePaper 1.0.8
==========================

把本地图片、GIF 和视频设为 Windows 桌面壁纸。
应用离线使用，无账号、无遥测。视频目前静音，只在主显示器播放。

下载与源码
----------
https://github.com/adihe1819/WhalePaper
https://github.com/adihe1819/WhalePaper/releases/latest

使用
----
1. 安装版安装后启动“鲸鱼壁纸”。
   免安装版先解压，再运行 WhalePaper-portable.cmd。
2. 点击“导入壁纸”，或把文件拖进窗口。
3. 选中壁纸，选择显示方式，再点击“应用为壁纸”。
4. 关闭主窗口会隐藏到系统托盘，壁纸继续播放。
5. 右键托盘图标可以暂停、继续或退出。
6. 如需退出时恢复原壁纸，在设置中开启对应选项。

系统与格式
----------
运行需要 64 位 Windows 10/11 和 .NET Framework 4.8，不需要开发 SDK。
当前在 Windows 11 25H2 上测试；Windows 10 尽力兼容，尚未完成同等验收。

图片：JPG、JPEG、PNG、BMP、TIFF；WebP 依赖系统解码器。
动图：GIF。
视频：MP4、M4V、WMV、AVI、MOV，实际支持取决于系统 Media Foundation
解码器，建议先试 H.264 MP4。绘制调度最高 240fps，按媒体时间戳播放；
实际帧率取决于素材、设备和显示器，不保证达到 240fps。
不支持 HTML/WebGL、Wallpaper Engine 私有格式或 DRM 视频。

高帧率与连续运行
----------------
本版使用有界解码预取、连续循环时钟和高精度后台等待，减少循环停顿。
如果设备解码不够快，优先使用 60/120fps 素材。RGB 预取缓冲最多
64 帧、512MiB；系统解码器和窗口等内存另计。
希望使用网页/全屏应用时仍连续播放，可关闭“设置 → 全屏应用时暂停”。
关闭主窗口到托盘不会停止播放；电池和锁屏等策略独立保留。

壁纸素材
--------
清华园雪夜和月下动漫人物视频作为 Release 附件单独下载，未捆绑在程序中：
https://github.com/adihe1819/WhalePaper/blob/main/docs/wallpapers.md
下载 MP4 后，通过“导入壁纸”添加。素材不自动适用代码的 MIT 许可。

数据与隐私
----------
默认数据：%LocalAppData%\WhalePaper
壁纸库：Library；缓存：Cache；日志：Logs；设置：settings.json。
免安装启动脚本将数据放在程序旁的 data 文件夹。
应用本身不发起网络请求，不上传壁纸，不收集使用数据。
导入时会复制一份文件；删除库内条目不会删除原文件。

验证范围
--------
历史版本已实测视频/GIF 解码与绘制、视频循环、暂停恢复、实际桌面图标可见，
用户已确认图标可以正常选中；正常退出恢复原壁纸也已验证。
1.0.8 播放实现已执行循环/暂停/恢复回归和单一桌面的高帧率开发验证；
验证范围及重新打包的发布程序复核方法见仓库 docs/testing.md。
全屏、锁屏、电池、远程会话、Explorer 重启、安装/升级/卸载尚未完整验收。
自动暂停和恢复逻辑的存在，不等于这些场景均已测试。
主窗口应用动态壁纸时可能短暂隐藏数秒，以检查桌面挂载和实际像素。
系统更新或第三方桌面工具可能影响兼容性。失败时会保留静态海报。

排查
----
先通过“设置 → 运行环境检测”生成诊断报告。
可运行：
WhalePaper.exe --media-probe "D:\wallpapers\video.mp4" --quiet
报告位于数据目录下的 Logs\media-probe.txt。
更多步骤见 GitHub 仓库 docs/troubleshooting.md。
公开日志或截图前，请移除个人路径、媒体名称和桌面信息。

许可证
------
项目采用 MIT，见 LICENSE；第三方声明见 THIRD-PARTY-NOTICES.txt
和 LICENSES。自行导入的壁纸不随项目发布。

