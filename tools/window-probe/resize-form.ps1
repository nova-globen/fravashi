param([int]$Width, [int]$Height)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class F { [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
 public static string Size(uint pid, int w, int hh) { IntPtr found = IntPtr.Zero; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero);
  if (found == IntPtr.Zero) return "no window"; RECT r; GetWindowRect(found, out r);
  SetWindowPos(found, IntPtr.Zero, r.L, r.T, w, hh, 0x0014); System.Threading.Thread.Sleep(700);
  GetWindowRect(found, out r); return (r.R - r.L) + "x" + (r.B - r.T) + " at " + r.L + "," + r.T; } }
'@
Get-Process fravashi | ForEach-Object { [F]::Size([uint32]$_.Id, $Width, $Height) }
