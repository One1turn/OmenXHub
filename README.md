翻译
OMEN X Hub
https://Preview/HubGui.png

一款轻量级、可离线使用的 HP OMEN Gaming Hub (OGH) 替代工具 —— 无广告、无壁纸、无网络连接。

OMEN X Hub (曾用名 OmenSuperHub) 是一款基于 WPF 开发的 HP OMEN / VICTUS 游戏本控制中心。它提供了全面的硬件监控、风扇控制、性能调节、键盘灯光以及系统诊断功能，完全摒弃了官方 OGH 软件的臃肿。

功能特性
📊 硬件监控
实时 CPU/GPU 温度、功耗 (W)、风扇转速 (RPM)

可悬浮点击穿透的监控窗口 —— 支持多显示器、自定义透明度及字体大小

温度颜色渐变：绿色 (<50°C) / 黄色 (50–70) / 橙色 (70–85) / 红色 (>85)

自动 GPU 监控 —— GPU 空闲时自动禁用，连接显示器后自动重新启用

🌀 智能风扇控制
静音模式 (BIOS 默认值的 80%) 与 酷冷模式 (BIOS 默认值的 100%) 风扇表

自定义风扇曲线编辑器，支持交互式拖拽图表

手动风扇转速滑块 (0–100%)

三风扇支持 (CPU、GPU、排风扇)

风扇除尘 (Clean Creek) —— 风扇反转实现清灰功能

高温自动保护 —— CPU 超过 90°C 时自动切换至酷冷+自动模式

⚡ 性能控制
CPU 功耗限制 —— PL1/PL2 可调范围 10W 至 254W

GPU 功耗控制 —— TGP 滑块 + PPAB (动态加速) 开关

GPU 频率锁定 —— 通过 nvidia-smi 实现 (600–2500 MHz)

最大帧率限制器 —— 可选 0/30/60/90/120/144/165/240/300/360/480/1000 FPS

IccMax (CPU 电流限制)、AC Load Line、Tpp (PPAB 增益)、dState (GPU 电源状态)

显卡模式切换 —— 独显直连 / 混合模式 / Advanced Optimus / UMA (仅集显)

热切换 (DDS) —— 通过 NvidiaApi 实现无需重启的动态显卡切换

AMD Smart Access Graphics (SAG) —— AMD 显卡的 MUX 切换

DB Unlock —— 替换 NVIDIA Power Config Framework 驱动，解锁更高的 GPU 功耗限制

GPU 进程管理 —— 列出并终止使用 GPU 的进程，通过 pnputil 重启 GPU

🎮 预设管理
3 个内置预设：极致性能、GPU 优先、轻度使用

3 个可自定义预设，支持重命名标签

保存内容包括：风扇配置、CPU/GPU 功耗、GPU 频率、显卡模式等

基于注册表的持久化存储 (HKCU\Software\OmenXHub\Presets)

💡 灯光控制
四区键盘背光 (支持基础 WMI 或 Dojo WMI 协议)

单键 RGB (实验性功能) —— 通过 HID 实现 (McuSDK2)

灯条控制 (实验性功能)

8 种静态颜色，9 种动态效果 (色彩循环、呼吸、波浪、雨滴、音频脉冲等)

支持动画速度与方向控制，亮度 0–100%

主题：银河、火山、丛林、海洋、自定义

🔑 Omen 键自定义
默认 —— 启动原始 Omen 键功能

切换悬浮窗 —— 显示/隐藏浮动监控窗口

显示主窗口 —— 打开控制面板

禁用 —— 关闭 Omen 键功能

🌐 多语言支持
简体中文、繁體中文、English

可通过托盘菜单或控制面板实时切换

包含 440+ 条本地化 UI 文本

🛠️ 系统集成
系统托盘，悬浮提示显示实时 CPU/GPU 信息

动态托盘图标 —— 以角标形式显示 CPU 温度

自定义托盘图标 —— 可使用自己的 custom.ico

右键上下文菜单，快速切换性能模式

通过 Windows 任务计划程序实现开机自启 (最高权限)

日志系统 (OmenXHub.log)

数据本地化 —— 将 CPU/GPU 温度写入文本文件，供外部工具使用 (如 Macro Deck)

温度平滑 —— 4 档响应级别 (实时 / 高 / 中 / 低)

原始/平滑显示模式切换，用于传感器读数

独立的 CPU / GPU 监控开关

离线可用 —— 零网络连接，无任何遥测数据

支持硬件
状态	型号
✅ 确认可用	暗影精灵 8 Plus, 8 Plus Plus, 9, 9 Plus, 10 · 光影精灵 10 · 光影精灵 10 (Victus) · OMEN 16 (锐龙版) · OMEN 15 · OMEN Phantom Gaming
❌ 不支持	暗影精灵 6
主要针对 OMEN 10 英特尔版 (i7-13650HX + RTX 4070) 设计。无法保证在所有平台上的兼容性。

系统要求
配备 WMI BIOS 接口的 HP OMEN / VICTUS 游戏本

Windows 10/11 64 位 · .NET Framework 4.8

管理员权限 (WMI、风扇控制、驱动程序安装所需)

项目架构
text
OmenXHub/
├── App.xaml(.cs)         — WPF 入口、单例模式、启动初始化
├── OmenHardware.cs       — 核心 WMI 模块：BIOS、风扇、功耗、显卡、灯光
├── AmdGpuSwitcher.cs     — AMD Smart Access Graphics 切换
├── App/
│   ├── Strings.cs        — 440+ 条多语言 UI 文本
│   ├── Logger.cs         — 基于文件的日志记录
│   ├── GpuAppManager.cs  — nvidia-smi 封装、GPU 进程管理
│   └── OmenLighting.cs   — 键盘/灯条 HID + WMI 控制
├── Services/
│   ├── ConfigService.cs  — 基于注册表的持久化存储 (75+ 项设置、预设)
│   ├── FanService.cs     — 风扇曲线、插值计算、自定义曲线
│   ├── HardwareService.cs— LibreHardwareMonitor 封装
│   ├── ThemeService.cs   — Windows 亮色/暗色主题检测
│   └── TrayService.cs    — 系统托盘、Omen 键、DB Unlock 逻辑
├── Views/
│   ├── MainWindow(.xaml) — 6 标签页控制面板
│   ├── FloatingWindow    — 多显示器硬件监控悬浮窗
│   └── HelpWindow        — 版本信息与文档
└── Themes/               — 深色/浅色调色板与 WPF 样式
快速开始
关闭 OGH —— 结束 OmenCommandCenterBackground.exe 进程，或卸载 OGH 以避免冲突。

以管理员身份运行 —— 所有硬件控制功能均需提升权限。

启动 OmenXHub.exe —— 程序将在系统托盘中运行。

右键点击托盘图标 可切换性能模式或打开控制面板。

在设置中 启用开机自启，以便长期替代 OGH。

⚠️ DB (Dynamic Boost) Unlock 功能需要 NVIDIA 显卡驱动版本 ≥ 537.42 且 < 610.47。50 系列显卡不支持解锁。

编译构建
cmd
# 还原 NuGet 包
dotnet restore OmenSuperHub.csproj

# 构建 x64 Release 版本
set MSBuildSDKsPath=C:\Program Files\dotnet\sdk\8.0.418\Sdks
set MSBuildEnableWorkloadResolver=false
MSBuild.exe OmenSuperHub.csproj /t:Build /p:Configuration=Release /p:Platform=x64
输出文件：bin\x64\Release\OmenXHub.exe (单文件 —— 通过 Costura.Fody 嵌入所有 DLL)

致谢
MasonDye —— GUI 设计与 WPF 前端开发

breadeding —— OmenSuperHub (原始框架与代码)

GeographicCone —— OmenMon / OmenHwCtl (灵感启发与 OGH 交互研究)

OpenHardwareMonitor —— OHM / LibreHardwareMonitor / hexagon-oss 分支 (硬件监控核心)

免责声明
OMEN X Hub 并非 HP 或 OMEN 的官方产品。文中提及的品牌名称仅作参考。本软件直接与硬件交互，可能存在潜在风险。使用本软件需自行承担风险。
