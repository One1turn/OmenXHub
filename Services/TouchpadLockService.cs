// TouchpadLockService.cs - 触摸板锁定服务
// 用 SetupAPI 禁用/启用 HID 精确触摸板设备，对所有触摸板通用、即时生效。
// 需要管理员权限（app.manifest 已设 requireAdministrator）。
//
// ponytail: 参考了 LenovoLegionToolkit 的语义（TouchpadLockState.On = 锁定 = 触摸板禁用），
// 但 LLT 用 Lenovo 专属 WMI (LENOVO_GAMEZONE_DATA.SetTPStatus)，HP 机器无此接口。
// HP 也没暴露等价 WMI（root\HP\InstrumentedBIOS 已按硬约束删除）。
// 因此用 SetupAPI 走标准 Windows 设备栈，匹配名称含 Touch/Tprecision/Tpad 的 Mouse 类设备。
//
// Ceiling:
//   1. 枚举所有设备类(Mouse + HIDClass):精确触摸板通常有两条输入路径 —— Mouse 类的
//      "…Filter Driver" 节点和 HIDClass 的 "…TPFHID Device" 精确触摸板集合。只禁 Mouse 类
//      节点时指针仍走 PTP 栈,表现为"开关点了、触摸板照用"(本机 SYNA32FF 即此结构)。
//   2. 禁用是 DICS_FLAG_GLOBAL,会写进注册表并跨重启保持。所以 LoadState 必须用
//      IsLocked() 回显真实状态,否则重启后 UI 显示"未锁"而触摸板仍是禁用的。
//   3. 需要管理员权限(app.manifest requireAdministrator)。
//   4. 名称匹配是唯一判据 —— 名字里带 mouse 的一律排除,避免把外接鼠标一起禁掉。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OmenSuperHub.Services {
  public static class TouchpadLockService {
    // SetupAPI consts
    const uint DIGCF_PRESENT = 0x00000002;
    // ponytail: 这一位原来是 0x40 —— 错值。SetupAPI 的合法标志只有
    // DEFAULT=0x1 / PRESENT=0x2 / ALLCLASSES=0x4 / PROFILE=0x8 / DEVICEINTERFACE=0x10,
    // 传 0x40 会整体报 ERROR_INVALID_PARAMETER(87)并返回 INVALID_HANDLE_VALUE,
    // 于是 SetEnabled 一路静默返回 0 —— 触摸板锁定之前从未真正生效过。
    const uint DIGCF_ALLCLASSES = 0x00000004;
    const uint DICS_ENABLE = 1;
    const uint DICS_DISABLE = 2;
    const uint DIF_PROPERTYCHANGE = 0x12;
    const uint SPDRP_DEVICEDESC = 0x00000000;
    const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    // CM_Get_DevNode_Status 的 DN_HAS_PROBLEM + CM_PROB_DISABLED
    const uint DN_HAS_PROBLEM = 0x00000400;
    const uint CM_PROB_DISABLED = 22;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA {
      public uint cbSize;
      public Guid ClassGuid;
      public uint DevInst;
      public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_CLASSINSTALL_HEADER {
      public uint cbSize;
      public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_PROPCHANGE_PARAMS {
      public SP_CLASSINSTALL_HEADER ClassInstallHeader;
      public uint StateChange;
      public uint Scope;
      public uint HwProfile;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, ref uint PropertyRegDataType, StringBuilder PropertyBuffer, uint PropertyBufferSize, ref uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiSetClassInstallParams(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref SP_PROPCHANGE_PARAMS ClassInstallParams, uint ClassInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(uint InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    // ponytail: 触摸板名称匹配关键词(小写)。覆盖中英文 + Synaptics/ELAN/Alps 厂商。
    // 同时用于 Mouse 类(Filter Driver)和 HIDClass(PTP 收集)节点 —— 精确触摸板两处各有一个。
    static readonly string[] MatchKeywords = {
      "touchpad", "touch pad", "precision touch", "精确触摸板", "触摸板", "触控板",
      "trackpad", "synaptics", "elan", "alps"
    };

    static bool IsTouchpad(string deviceName) {
      if (string.IsNullOrEmpty(deviceName)) return false;
      string lower = deviceName.ToLowerInvariant();
      // 安全红线:凡是名字里带 mouse 的一律不碰(外接 USB 鼠标/虚拟鼠标)。
      // 触摸板的 DeviceDesc 可能是泛泛的 "HID-compliant mouse",但那种节点无从识别,
      // 宁可不动也不能误禁用户的鼠标。
      if (lower.Contains("mouse")) return false;
      foreach (var kw in MatchKeywords) {
        if (lower.Contains(kw)) return true;
      }
      return false;
    }

    /// <summary>读设备名:优先 FriendlyName(厂商型号),没有再退回 DeviceDesc。
    /// ponytail: 很多触摸板的 DeviceDesc 就是泛泛的 "HID-compliant mouse",真正的
    /// "Synaptics/ELAN ... TouchPad" 只写在 FriendlyName 里 —— 只读 DeviceDesc 会漏匹配,
    /// 表现就是点了开关什么都没发生。</summary>
    static string ReadDeviceName(IntPtr set, ref SP_DEVINFO_DATA did, StringBuilder sb) {
      uint t = 0, required = 0;
      sb.Clear();
      if (SetupDiGetDeviceRegistryProperty(set, ref did, SPDRP_FRIENDLYNAME,
          ref t, sb, (uint)sb.Capacity, ref required) && sb.Length > 0) {
        return sb.ToString();
      }
      t = 0; required = 0; sb.Clear();
      if (SetupDiGetDeviceRegistryProperty(set, ref did, SPDRP_DEVICEDESC,
          ref t, sb, (uint)sb.Capacity, ref required)) {
        return sb.ToString();
      }
      return null;
    }

    /// <summary>
    /// 启用/禁用所有触摸板设备。返回实际发生状态变更的设备数。
    /// </summary>
    public static int SetEnabled(bool enabled) {
      int changed = 0;
      // 所有设备类(Mouse + HIDClass)—— 精确触摸板的 PTP 输入节点在 HIDClass,
      // 只遍历 Mouse 类会漏掉它,触摸板照样能用。
      IntPtr devInfoSet = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
        DIGCF_ALLCLASSES | DIGCF_PRESENT);
      if (devInfoSet == new IntPtr(-1)) {
        Logger.Warn($"[TouchpadLock] SetupDiGetClassDevs 失败 err={Marshal.GetLastWin32Error()}");
        return 0;
      }

      try {
        uint index = 0;
        var did = new SP_DEVINFO_DATA();
        did.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

        while (SetupDiEnumDeviceInfo(devInfoSet, index, ref did)) {
          index++;
          var sb = new StringBuilder(512);
          string desc = ReadDeviceName(devInfoSet, ref did, sb);
          if (!IsTouchpad(desc)) continue;

          // 已是目标状态就跳过 —— 避免每次切开关都无谓重装一次设备节点。
          bool disabledNow = IsNodeDisabled(did.DevInst);
          if (disabledNow == !enabled) continue;

          if (ChangeDeviceState(devInfoSet, ref did, enabled ? DICS_ENABLE : DICS_DISABLE)) {
            changed++;
            Logger.Info($"[TouchpadLock] {(enabled ? "启用" : "禁用")} {desc}");
          } else {
            Logger.Warn($"[TouchpadLock] {(enabled ? "启用" : "禁用")}失败 {desc} err={Marshal.GetLastWin32Error()}");
          }
        }
      } finally {
        SetupDiDestroyDeviceInfoList(devInfoSet);
      }
      return changed;
    }

    /// <summary>触摸板当前是否处于禁用(已锁定)状态。用于 LoadState 回显真实状态。
    /// 多节点触摸板(Filter Driver + PTP)必须全部禁用才算锁定 —— 只看任一节点会把
    /// "只禁了半边"误报成已锁。</summary>
    public static bool IsLocked() {
      IntPtr devInfoSet = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
        DIGCF_ALLCLASSES | DIGCF_PRESENT);
      if (devInfoSet == new IntPtr(-1)) return false;
      try {
        int matched = 0;
        uint index = 0;
        var did = new SP_DEVINFO_DATA();
        did.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
        while (SetupDiEnumDeviceInfo(devInfoSet, index, ref did)) {
          index++;
          var sb = new StringBuilder(512);
          if (!IsTouchpad(ReadDeviceName(devInfoSet, ref did, sb))) continue;
          matched++;
          if (!IsNodeDisabled(did.DevInst)) return false;
        }
        return matched > 0;
      } finally {
        SetupDiDestroyDeviceInfoList(devInfoSet);
      }
    }

    static bool IsNodeDisabled(uint devInst) {
      uint status, problem;
      if (CM_Get_DevNode_Status(out status, out problem, devInst, 0) != 0) return false;
      return (status & DN_HAS_PROBLEM) != 0 && problem == CM_PROB_DISABLED;
    }

    /// <summary>--selftest: 名称匹配必须命中触摸板,且绝不能命中外接鼠标。
    /// 后者是安全红线 —— 匹配过宽会把用户的 USB 鼠标一起禁掉。</summary>
    public static string SelfCheck() {
      var fail = new List<string>();
      void Check(bool ok, string what) { if (!ok) fail.Add(what); }
      Check(IsTouchpad("Synaptics PrecisionTouchpad Filter Driver"), "miss: synaptics filter");
      Check(IsTouchpad("ELAN Precision Touchpad"), "miss: elan");
      Check(IsTouchpad("Alps Touch Pad"), "miss: alps");
      Check(IsTouchpad("触摸板"), "miss: zh-cn");
      Check(IsTouchpad("Synaptics TPFHID Device"), "miss: tpfhid");
      Check(!IsTouchpad("HID-compliant mouse"), "false positive: generic mouse");
      Check(!IsTouchpad("Logitech USB Mouse"), "false positive: usb mouse");
      Check(!IsTouchpad("Logitech Touch Mouse"), "false positive: touch mouse");
      Check(!IsTouchpad("TENCENTMYAPPS HID"), "false positive: hid bus");
      Check(!IsTouchpad(""), "false positive: empty");
      Check(!IsTouchpad(null), "false positive: null");
      return fail.Count == 0 ? "PASS TouchpadLockService name match"
        : "FAIL:\n  " + string.Join("\n  ", fail);
    }

    static bool ChangeDeviceState(IntPtr devInfoSet, ref SP_DEVINFO_DATA did, uint stateChange) {
      var parms = new SP_PROPCHANGE_PARAMS {
        ClassInstallHeader = new SP_CLASSINSTALL_HEADER {
          cbSize = (uint)Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER)),
          InstallFunction = DIF_PROPERTYCHANGE,
        },
        StateChange = stateChange,
        // ponytail: DICS_FLAG_GLOBAL = 1 —— 原来写的 0 不是合法枚举值,部分类安装器
        // 会直接拒绝(SetClassInstallParams 返回 false),表现就是"点了没反应"。
        Scope = 1,
        HwProfile = 0,
      };
      if (!SetupDiSetClassInstallParams(devInfoSet, ref did, ref parms,
          (uint)Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS)))) {
        return false;
      }
      return SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devInfoSet, ref did);
    }
  }
}
