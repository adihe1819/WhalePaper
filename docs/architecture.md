# 架构

WhalePaper 将本地媒体库、静态壁纸和动态桌面宿主分开处理。界面使用 WPF，托盘使用 WinForms；应用目标为 .NET Framework 4.8、x64。

## 从导入到显示

1. MainWindow / MainViewModel 接收导入和设置操作。
2. LibraryService 将媒体复制到本地库；ThumbnailService / ShellThumbnailService 生成预览。
3. WallpaperController 按所选类型应用壁纸并管理暂停、恢复和退出。
4. 静态图片由 DesktopWallpaperService 交给 Windows。
5. 动态内容交给 WallpaperHostWindow：视频使用 Media Foundation，GIF 使用 GIF 播放器，最终由原生窗口和 GDI 绘制。

视频路径为：

~~~text
媒体文件
  → MfSourceReader（Media Foundation 解码、RGB32 / RGB24）
  → MfVideoPlayback 解码线程（复制 RGB、立即释放 MF sample）
  → 有界预取队列（最多 64 个缓冲，总 RGB 复制缓冲不超过 512MiB）
  → 独立显示线程（跨循环的连续时钟、高精度等待、暂停）
  → DibPaint（行布局、GDI）
  → WallpaperHostWindow（桌面图标后方的 Win32 窗口）
~~~

绘制调度上限为 240fps；较高帧率源按时间戳选择输出帧。实际速率取决于解码、RGB 转换、CPU 复制、GDI 和显示器，不能把上限当作性能保证。当前没有音频输出。

解码线程在到达文件末尾时执行 seek，显示线程继续消费已预取的帧；每一轮媒体时间戳映射到同一条连续时间线，不重置显示时钟。队列按约 400ms 计算容量，再受帧数和内存预算限制，预算包含正在解码和正在显示的帧。复制后的缓冲归播放器所有，MF sample 在入队前释放。暂停保留缓冲；结束后由最后退出的工作线程清理队列和原生内存。

Windows 支持时使用高精度 one-shot waitable timer 和最后约 0.2ms 的短等待，不依赖被遮挡的后台窗口获得 timeBeginPeriod 精度。创建或等待失败时回退到 Sleep；线程退出和 Dispose 后关闭句柄。若持续解码吞吐低于素材帧率，缓冲最终仍会耗尽，应选择较低帧率素材。

## 桌面集成

WorkerWService 与桌面层辅助服务定位 Explorer 的桌面窗口。新版 Windows 桌面模式先准备透明图标层，再把原生宿主放在图标层下方、系统壁纸层上方。兼容性依赖 Windows 桌面窗口行为，并非稳定的公开壁纸视频 API。

挂载过程中执行窗口位置检查和像素探测。播放成功后，还需要分别确认实际动态画面、图标可见和鼠标选择行为。检查失败时保留静态海报。桌面被其他窗口遮住时，像素探测可能无法代表用户看到的桌面。

## 原生媒体边界

Interop/MfInterop.cs 定义 COM 接口。对于继承 IMFAttributes 的接口，声明必须保持完整的原生方法表顺序；C# 接口继承不能替代正确的 COM vtable 声明。

帧复制优先使用 IMF2DBuffer.Lock2D 返回的真实 pitch，回退路径使用连续缓冲区布局。RGB24 需要 DIB 行对齐。指针仅在对应锁定区间内有效，转换、锁定、复制和解锁必须配对。

## 数据

AppPaths 统一生成 Library、Cache、Logs、settings.json 和 library.json 的位置。默认根目录为 %LocalAppData%\WhalePaper；免安装启动脚本使用程序旁的 data，测试可以使用 --data-dir 隔离。

应用运行不依赖账号或在线服务。诊断日志是本地文件，可能包含个人文件路径，公开前需要脱敏。

## 代码入口

| 文件 / 目录 | 作用 |
| --- | --- |
| src/WhalePaper/Program.cs | 启动及诊断命令入口 |
| src/WhalePaper/MainWindow.xaml | 主界面 |
| src/WhalePaper/Services/WallpaperController.cs | 生命周期和播放策略 |
| src/WhalePaper/Services/WallpaperHostWindow.cs | 动态桌面宿主 |
| src/WhalePaper/Services/MfSourceReader.cs | 视频解码和缓冲区转换 |
| src/WhalePaper/Interop/MfInterop.cs | Media Foundation COM 声明 |
| tests/Verify-Media.ps1 | COM、解码、seek 和错误输入回归 |
| tests/DesktopPlaybackSmoke.cs | 实际宿主绘制和暂停恢复测试 |

进一步阅读：[验证与测试](testing.md)、[故障排查](troubleshooting.md)。

原生接口参考：[微软 COM 接口继承说明](https://github.com/dotnet/runtime/blob/main/docs/design/libraries/ComInterfaceGenerator/DerivedComInterfaces.md)、[IMF2DBuffer::Lock2D](https://learn.microsoft.com/windows/win32/api/mfobjects/nf-mfobjects-imf2dbuffer-lock2d)、[MF_MT_DEFAULT_STRIDE](https://learn.microsoft.com/windows/win32/medfound/mf-mt-default-stride-attribute)。桌面层行为参考 [Lively issue 2074](https://github.com/lively-community/lively/issues/2074)。这些参考资料解释兼容性依据，不表示本项目覆盖了所有系统环境。

