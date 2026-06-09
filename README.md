# OMEN X Hub

![Preview](Preview/HubGui.png)

> A lightweight, offline replacement for HP OMEN Gaming Hub (OGH) — no advertisements, no wallpapers, no network connections.

**OMEN X Hub** (formerly OmenSuperHub) is a WPF-based control center for HP OMEN / VICTUS gaming laptops. It provides comprehensive hardware monitoring, fan control, performance tuning, keyboard lighting, and system diagnostics — all without the bloat of the official OGH software.

---

## Features

### 📊 Hardware Monitoring
- Real-time **CPU/GPU temperature**, **power consumption (W)**, **fan speed (RPM)**
- Floating click-through overlay — multi-monitor support, customizable opacity & font size
- Temperature color gradient: green (<50°C) / yellow (50–70) / orange (70–85) / red (>85)
- Auto GPU monitoring — disables when GPU is idle, re-enables on display connection

### 🌀 Smart Fan Control
- **Silent** (80% of BIOS default) & **Cool** (100% of BIOS default) fan tables
- **Custom fan curve** editor with interactive drag-and-drop chart
- **Manual fan speed** slider (0–100%)
- **Three-fan support** (CPU, GPU, Exhaust)
- **Fan Dust Removal** (Clean Creek) — reverse fan rotation for cleaning
- **High-Temp Auto-Protect** — switches to Cool+Auto when CPU > 90°C

### ⚡ Performance Control
- **CPU Power Limits** — PL1/PL2 adjustable from 10W to 254W
- **GPU Power Control** — TGP slider + PPAB (Dynamic Boost) toggle
- **GPU Frequency Lock** — via nvidia-smi (600–2500 MHz)
- **Max Frame Rate Limiter** — 0/30/60/90/120/144/165/240/300/360/480/1000 FPS
- **IccMax** (CPU current limit), **AC Load Line**, **Tpp** (PPAB gain), **dState** (GPU power state)
- **Graphics Mode Switching** — Discrete / Hybrid / Advanced Optimus / UMA (iGPU only)
- **Hot Switch (DDS)** — dynamic GPU switching without reboot (NvidiaApi)
- **AMD Smart Access Graphics (SAG)** — AMD GPU mux switching
- **DB Unlock** — replace NVIDIA Power Config Framework driver to unlock higher GPU power limits
- **GPU Process Management** — list & kill GPU-using processes, restart GPU via `pnputil`

### 🎮 Preset Management
- **3 built-in presets**: Extreme Performance, GPU Priority, Light Use
- **3 customizable presets** with renameable labels
- Saves: fan config, CPU/GPU power, GPU clock, display mode, and more
- Registry-backed persistence (`HKCU\Software\OmenXHub\Presets`)

### 💡 Lighting Control
- **Four-zone keyboard backlight** (Basic WMI or Dojo WMI protocol)
- **Per-key RGB** (experimental) via HID (McuSDK2)
- **Light bar** control (experimental)
- 8 static colors, 9 animations (Color Cycle, Breathing, Wave, Raindrop, Audio Pulse, etc.)
- Animation speed & direction control, brightness 0–100%
- Themes: Galaxy, Volcano, Jungle, Ocean, Custom

### 🔑 Omen Key Customization
- Default — launch original Omen Key task
- Toggle Overlay — show/hide floating monitoring window
- Show Main Window — open control panel
- Unbound — disable Omen key

### 🌐 Multi-Language
- **简体中文**, **繁體中文**, **English**
- Runtime switching via tray menu or control panel
- 440+ localized UI strings

### 🛠️ System Integration
- System tray with live CPU/GPU info tooltip
- Dynamic tray icon — shows CPU temperature as badge
- Custom tray icon — use your own `custom.ico`
- Right-click context menu for quick mode switching
- Auto-start via Windows Task Scheduler (highest privileges)
- Logging system (`OmenXHub.log`)
- **Data Localization** — writes CPU/GPU temps to text files for external tools (Macro Deck)
- **Temperature smoothing** — 4 response levels (Real-time / High / Medium / Low)
- **Raw/Smoothed display mode** toggle for sensor readings
- **Independent CPU / GPU monitoring** toggles
- **Offline** — zero network connections, no telemetry

---

## Supported Hardware

| Status | Models |
|--------|--------|
| ✅ **Confirmed working** | 暗影精灵 8 Plus, 8 Plus Plus, 9, 9 Plus, 10 · 光影精灵 10 · 光影精灵 10 (Victus) · OMEN 16 (Ryzen) · OMEN 15 · OMEN Phantom Gaming |
| ❌ **Not supported** | 暗影精灵 6 |

> Designed primarily for **OMEN 10 Intel (i7-13650HX + RTX 4070)**. Compatibility not guaranteed on all platforms.

### Requirements
- HP OMEN / VICTUS gaming laptop with WMI BIOS interface
- Windows 10/11 64-bit · .NET Framework 4.8
- Administrative privileges (required for WMI, fan control, driver installation)

---

## Architecture

```
OmenXHub/
├── App.xaml(.cs)         — WPF entry, single-instance, startup init
├── OmenHardware.cs       — Core WMI: BIOS, fan, power, graphics, lighting
├── AmdGpuSwitcher.cs     — AMD Smart Access Graphics switching
├── App/
│   ├── Strings.cs        — 440+ multilingual UI strings
│   ├── Logger.cs         — File-based logging
│   ├── GpuAppManager.cs  — nvidia-smi wrappers, GPU process mgmt
│   └── OmenLighting.cs   — Keyboard/light bar HID + WMI control
├── Services/
│   ├── ConfigService.cs  — Registry persistence (75+ settings, presets)
│   ├── FanService.cs     — Fan curves, interpolation, custom curves
│   ├── HardwareService.cs— LibreHardwareMonitor wrapper
│   ├── ThemeService.cs   — Windows light/dark theme detection
│   └── TrayService.cs    — System tray, Omen key, DB unlock logic
├── Views/
│   ├── MainWindow(.xaml) — 6-tab control panel
│   ├── FloatingWindow    — Multi-monitor hardware overlay
│   └── HelpWindow        — Version info & documentation
└── Themes/               — Dark/light color palettes & WPF styles
```

---

## Getting Started

1. **Close OGH** — shut down `OmenCommandCenterBackground.exe` or uninstall OGH to avoid conflicts.
2. **Run as Administrator** — all hardware control requires elevated privileges.
3. **Launch** `OmenXHub.exe` — the app runs from the system tray.
4. **Right-click tray icon** to switch performance modes or open the control panel.
5. **Enable auto-start** in settings for long-term OGH replacement.

> ⚠️ DB (Dynamic Boost) Unlock requires NVIDIA driver version ≥ 537.42 and < 610.47. 50-series GPUs are not supported for unlock.

---

## Build

```cmd
# Restore NuGet packages
dotnet restore OmenSuperHub.csproj

# Build for x64 Release
set MSBuildSDKsPath=C:\Program Files\dotnet\sdk\8.0.418\Sdks
set MSBuildEnableWorkloadResolver=false
MSBuild.exe OmenSuperHub.csproj /t:Build /p:Configuration=Release /p:Platform=x64
```

Output: `bin\x64\Release\OmenXHub.exe` (single-file — all DLLs embedded via Costura.Fody)

---

## Acknowledgments

- **MasonDye** — GUI design & WPF front-end development
- **breadeding** — [OmenSuperHub](https://github.com/breadeding/OmenSuperHub) (original framework & code)
- **GeographicCone** — [OmenMon](https://github.com/GeographicCone) / [OmenHwCtl](https://github.com/GeographicCone) (inspiration & OGH interaction research)
- **OpenHardwareMonitor** — [OHM](https://openhardwaremonitor.org/) / [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) / [hexagon-oss fork](https://github.com/hexagon-oss/openhardwaremonitor) (hardware monitoring core)

---

## Disclaimer

OMEN X Hub is **not affiliated with HP or OMEN**. Brand names are used for reference only. This software interacts directly with hardware and may carry potential risks. **Use at your own risk.**
