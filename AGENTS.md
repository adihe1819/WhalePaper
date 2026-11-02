# Repository guidance

WhalePaper is a local Windows wallpaper application. The production app targets .NET Framework 4.8, x64, with C# 7.3, WPF, WinForms tray integration, Win32 desktop hosting and Windows Media Foundation.

## Build

Run from the repository root on Windows with .NET 10 SDK available:

~~~powershell
.\setup.ps1
dotnet build .\src\WhalePaper\WhalePaper.csproj -c Release --disable-build-servers
dotnet build .\tests\DesktopPlaybackSmoke.csproj -c Release --disable-build-servers
.\build.ps1 -SkipInstaller
~~~

setup.ps1 checks an existing SDK and builds the app and smoke harness; it does not install software. A portable SDK can be selected with -DotNetPath. build.ps1 also accepts -InnoSetupCompiler for installer packaging. setup.sh is a Windows Git Bash wrapper around setup.ps1.

Build outputs are under the projects' bin/Release/net48 directories; distributable packages are under dist. GitHub Actions performs build/package checks only. Media decoding and desktop visibility require Windows runtime checks described in docs/testing.md.

## Code map

- src/WhalePaper/MainWindow.xaml and ViewModels: WPF library/settings UI.
- Services/WallpaperController.cs: application lifecycle and playback policy.
- Services/WorkerWService.cs and WallpaperHostWindow.cs: desktop placement and native host.
- Services/MfSourceReader.cs, MfVideoPlayback.cs, FramePlayback.cs and DibPaint.cs: decoding, timing and GDI presentation.
- Interop/MfInterop.cs: native COM declarations; native vtable layout must match Windows SDK headers.
- Services/AppPaths.cs, LibraryService.cs and SettingsService.cs: local data.
- tests/Verify-Media.ps1: real COM, media/seek and invalid-input regression.
- tests/Verify-Playback.ps1 and --playback-probe: bounded decode-ahead, loop timing and pause/resume regression without desktop attachment.
- tests/DesktopPlaybackSmoke.cs: real desktop host, continuous painting and pause/resume checks.
- installer: Inno Setup scripts; preserve the UTF-8 BOM in the simplified Chinese language file.
- tools: optional asset/sample generators; not needed for ordinary compilation.

## Work safely and report evidence

Keep C# 7.3/net48 compatibility and the offline behavior. Use existing services before adding dependencies. Verify unmanaged memory bounds, row stride and COM cleanup when modifying playback.

Use isolated test data via --data-dir. Do not commit private media, logs, desktop screenshots, user profiles, configuration, tool binaries or generated packages. Keep only reusable public source assets and license files.

A decoded frame, a paint counter, a visible desktop and clickable desktop icons are separate pieces of evidence. Never fabricate runtime or screenshot results. State which checks actually ran; do not infer lifecycle coverage from the presence of handler code. Use docs/testing.md for current validation boundaries.

When changing packaging, include LICENSE, third-party notices and relevant LICENSES files in both distributable forms. Do not commit release binaries; publish them as release assets.

