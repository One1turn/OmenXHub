// ThreadBindingService.cs — 实时线程/进程核心绑定
// SetThreadGroupAffinity(组内掩码) + SetProcessDefaultCpuSetMasks(进程默认)实现即时生效
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace OmenSuperHub.Services {
  internal static class ThreadBindingService {
    // ── P/Invoke ──
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetThreadGroupAffinity(IntPtr hThread, ref GROUP_AFFINITY groupAffinity, IntPtr previousGroupAffinity);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetThreadGroupAffinity(IntPtr hThread, out GROUP_AFFINITY groupAffinity);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetThreadIdealProcessorEx(IntPtr hThread, ref PROCESSOR_NUMBER idealProcessor, IntPtr previousIdealProcessor);

    // ponytail: 实测(2026-09-30 独立探针):SetThreadSelectedCpuSets 接受的是"CPU 集合 ID"
    // 而非逻辑处理器号,传处理器号返回 ERROR_INVALID_HANDLE(813)——评审建议的 CpuSets
    // 路线表达不了逐核绑定。Windows 线程亲和性模型 = 线程任一时刻归属单一处理器组 +
    // 组内掩码(OS 硬限制),故按 SetThreadGroupAffinity 的正确语义真修:组内位号、
    // 跨组请求取众数组、解绑恢复组内满掩码。旧的 SetProcessDefaultCpuSets 把 PULONG
    // 误声明为 ulong[](8 字节元素)且零调用方——随本注释一并移除。
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessDefaultCpuSetMasks(IntPtr hProcess, ref GROUP_AFFINITY mask, uint count);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessDefaultCpuSetMasks")]
    static extern bool SetProcessDefaultCpuSetMasksArray(IntPtr hProcess, IntPtr masks, uint count);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int GetActiveProcessorCount(ushort groupNumber);

    static ulong FullMaskForGroup(ushort group) {
      int count = GetActiveProcessorCount(group);
      return count >= 64 ? ~0UL : (count > 0 ? (1UL << count) - 1 : 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GROUP_AFFINITY {
      public ulong Mask;
      public ushort Group;
      public ushort Reserved1;
      public ushort Reserved2;
      public ushort Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESSOR_NUMBER {
      public ushort Group;
      public byte Number;
      public byte Reserved;
    }

    const uint THREAD_SET_LIMITED_INFORMATION = 0x0400;
    const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    const uint THREAD_SET_INFORMATION = 0x0020;
    const uint THREAD_QUERY_INFORMATION = 0x0040;
    const uint PROCESS_SET_INFORMATION = 0x0200;

    /// <summary>将线程绑定到指定组 + 掩码的核心</summary>
    public static bool BindThread(int threadId, ushort group, ulong affinityMask) {
      // ponytail: 实测(2026-09-30 探针):SetThreadGroupAffinity 需 THREAD_SET_INFORMATION(0x0020)——
      // MSDN 写"THREAD_SET_LIMITED_INFORMATION 即可"不成立,0x0400 被拒 gle=5。
      IntPtr hThread = OpenThread(THREAD_SET_INFORMATION | THREAD_SET_LIMITED_INFORMATION, false, (uint)threadId);
      if (hThread == IntPtr.Zero) return false;
      try {
        var aff = new GROUP_AFFINITY { Group = group, Mask = affinityMask };
        return SetThreadGroupAffinity(hThread, ref aff, IntPtr.Zero);
      } finally { CloseHandle(hThread); }
    }

    /// <summary>解绑线程：把线程当前组的组内掩码恢复为满(= 系统默认亲和形态)</summary>
    public static bool UnbindThread(int threadId) {
      IntPtr hThread = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, false, (uint)threadId);
      if (hThread == IntPtr.Zero) return false;
      try {
        if (!GetThreadGroupAffinity(hThread, out GROUP_AFFINITY aff)) return false;
        aff.Mask = FullMaskForGroup(aff.Group);
        return aff.Mask != 0 && SetThreadGroupAffinity(hThread, ref aff, IntPtr.Zero);
      } finally { CloseHandle(hThread); }
    }

    /// <summary>将进程的所有线程绑定到指定组+掩码</summary>
    public static int BindProcess(int processId, ushort group, ulong affinityMask) {
      int count = 0;
      foreach (int tid in EnumThreads(processId)) {
        if (BindThread(tid, group, affinityMask)) count++;
      }

      // 也尝试设置进程默认 CPU Sets (Win10+)
      try {
        IntPtr hProcess = CpuAffinity.Kernel32.OpenProcess(PROCESS_SET_INFORMATION, false, (uint)processId);
        if (hProcess != IntPtr.Zero) {
          try {
            var aff = new GROUP_AFFINITY { Group = group, Mask = affinityMask };
            SetProcessDefaultCpuSetMasks(hProcess, ref aff, 1);
          } finally { CloseHandle(hProcess); }
        }
      } catch { }

      return count;
    }

    /// <summary>解绑进程所有线程并恢复进程默认掩码（全核）</summary>
    public static int UnbindProcess(int processId) {
      int count = 0;
      foreach (int tid in EnumThreads(processId)) if (UnbindThread(tid)) count++;
      // 进程默认掩码数组恢复为各组满掩码(新建线程继承;SetProcessDefaultCpuSetMasks 支持逐组数组)
      try {
        ushort groups = GetActiveProcessorGroupCount();
        if (groups < 1) groups = 1;
        var masks = new GROUP_AFFINITY[groups];
        for (ushort g = 0; g < groups; g++)
          masks[g] = new GROUP_AFFINITY { Group = g, Mask = FullMaskForGroup(g) };
        IntPtr hProcess = CpuAffinity.Kernel32.OpenProcess(PROCESS_SET_INFORMATION, false, (uint)processId);
        if (hProcess != IntPtr.Zero) {
          try {
            var pinned = GCHandle.Alloc(masks, GCHandleType.Pinned);
            try { SetProcessDefaultCpuSetMasksArray(hProcess, pinned.AddrOfPinnedObject(), groups); }
            finally { pinned.Free(); }
          } finally { CloseHandle(hProcess); }
        }
      } catch { }
      return count;
    }

    /// <summary>将进程绑定到指定 CCD 的所有核心</summary>
    public static int BindProcessToCcd(int processId, int ccdId) {
      var cores = CpuTopologyService.GetCores()
          .Where(c => c.CcdId == ccdId)
          .ToList();
      if (cores.Count == 0) return 0;
      return BindCoreList(processId, cores);
    }

    /// <summary>将进程绑定到指定效率等级的核心</summary>
    public static int BindProcessToEfficiencyClass(int processId, bool performance) {
      var cores = CpuTopologyService.GetCores()
          .Where(c => performance ? c.IsPerformance : c.IsEfficiency)
          .ToList();
      if (cores.Count == 0) return 0;
      return BindCoreList(processId, cores);
    }

    /// <summary>将进程绑定到指定索引列表的核心</summary>
    public static int BindProcessToCoreIndices(int processId, int[] coreIndices) {
      var set = new HashSet<int>(coreIndices);
      var cores = CpuTopologyService.GetCores()
          .Where(c => set.Contains(c.LogicalIndex))
          .ToList();
      return BindCoreList(processId, cores);
    }

    static int BindCoreList(int processId, List<CoreInfo> cores) {
      if (cores.Count == 0) return 0;
      // ponytail: 跨组逐核子集 OS 层面无法表达(线程任一时刻只归属一个组)。跨组请求取
      // 目标核最多的组并记一次日志;单组机型(目标 OMEN 硬件)下恒为精确解。
      // 组内位号 = LogicalIndex - Group*64(LogicalIndex 是全局号,旧版直接 1UL<<全局号,
      // ≥64 时 C# 移位回绕成错核——评审确认的缺陷本体)。
      var byGroup = cores.GroupBy(c => (ushort)c.Group)
                         .OrderByDescending(g => g.Count()).First();
      if (byGroup.Count() < cores.Count)
        Logger.Warn($"[ThreadBinding] 跨组核集取众数组 {byGroup.Key}({byGroup.Count()}/{cores.Count} 核)");
      ulong mask = 0;
      foreach (var c in byGroup) mask |= 1UL << (c.LogicalIndex - c.Group * 64);
      if (mask == 0) return 0;
      int count = 0;
      foreach (int tid in EnumThreads(processId)) {
        if (BindThread(tid, byGroup.Key, mask)) count++;
      }
      return count;
    }

    // ponytail: OpenProcess 收敛到 CpuAffinity/Native.cs 的 Kernel32.OpenProcess(签名 uint)

    // ponytail: 组内掩码绑定→读回→解绑满掩码的往返验证(本模型的真实机制)。
    // 跨组子集 OS 不可表达(见 BindCoreList 注释),多组路径本机不可实证,属已声明盲区。
    public static string SelfCheck() {
      var fails = new List<string>();
      try {
        if (Environment.ProcessorCount < 3) return "SKIP ThreadBinding affinity roundtrip: too few cores";
        uint tid = GetCurrentThreadId();
        IntPtr h = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, false, tid);
        if (h == IntPtr.Zero) return "FAIL ThreadBinding affinity roundtrip: OpenThread(self) gle=" + Marshal.GetLastWin32Error();
        try {
          var want = new GROUP_AFFINITY { Group = 0, Mask = 0b111 };
          if (!SetThreadGroupAffinity(h, ref want, IntPtr.Zero))
            fails.Add($"绑定核 0/1/2 失败 gle={Marshal.GetLastWin32Error()}");
          else if (!GetThreadGroupAffinity(h, out GROUP_AFFINITY got) || got.Group != 0 || got.Mask != 0b111)
            fails.Add($"读回 group={got.Group} mask=0x{got.Mask:X},应为 0x7");
          if (!UnbindThread((int)tid)) fails.Add("解绑(满掩码恢复)失败");
          else {
            GetThreadGroupAffinity(h, out GROUP_AFFINITY now);
            ulong full = FullMaskForGroup(now.Group);
            if (full != 0 && now.Mask != full) fails.Add($"解绑后 mask=0x{now.Mask:X},应为满掩码 0x{full:X}");
          }
        } finally { CloseHandle(h); }
      } catch (Exception ex) { fails.Add("异常: " + ex.Message); }
      return fails.Count == 0 ? "PASS ThreadBinding affinity roundtrip"
        : "FAIL ThreadBinding:\n  " + string.Join("\n  ", fails);
    }

    /// <summary>枚举进程的所有线程 ID</summary>
    public static List<int> EnumThreads(int processId) {
      var tids = new List<int>();
      IntPtr h = CreateToolhelp32Snapshot(4/*TH32CS_SNAPTHREAD*/, 0);
      if (h == (IntPtr)(-1)) return tids;
      try {
        var te = new THREADENTRY32 { dwSize = Marshal.SizeOf<THREADENTRY32>() };
        if (Thread32First(h, ref te)) {
          do {
            if (te.th32OwnerProcessID == processId)
              tids.Add(te.th32ThreadID);
          } while (Thread32Next(h, ref te));
        }
      } finally { CloseHandle(h); }
      return tids;
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll")]
    static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [StructLayout(LayoutKind.Sequential)]
    struct THREADENTRY32 {
      public int dwSize;
      public int cntUsage;
      public int th32ThreadID;
      public uint th32OwnerProcessID;
      public int tpBasePri;
      public int tpDeltaPri;
      public uint dwFlags;
    }
  }
}
