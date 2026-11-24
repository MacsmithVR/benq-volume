# Controla el volumen interno del monitor por DDC/CI (VCP 0x62).
# Uso:
#   .\benq-volumen.ps1            -> muestra el volumen actual
#   .\benq-volumen.ps1 25         -> pone el volumen a 25
#   .\benq-volumen.ps1 +5 / -5    -> sube/baja 5
#   .\benq-volumen.ps1 mute|unmute
param([string]$Valor, [int]$Monitor = 0)

$code = @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public class DDCVol {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct PM { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string d; }
  public delegate bool EnumProc(IntPtr hMon, IntPtr hdc, IntPtr r, IntPtr p);
  [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumProc cb, IntPtr p);
  [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
  [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PM[] a);
  [DllImport("dxva2.dll")] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr t, out uint cur, out uint max);
  [DllImport("dxva2.dll")] public static extern bool SetVCPFeature(IntPtr h, byte code, uint val);
  public static List<IntPtr> All() {
    var l = new List<IntPtr>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hm, a, b, c) => {
      uint n; GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out n);
      var arr = new PM[n]; GetPhysicalMonitorsFromHMONITOR(hm, n, arr);
      foreach (var m in arr) l.Add(m.h); return true; }, IntPtr.Zero);
    return l; }
}
'@
if (-not ([System.Management.Automation.PSTypeName]'DDCVol').Type) { Add-Type -TypeDefinition $code }

$h = [DDCVol]::All()[$Monitor]
$cur = 0; $max = 0
if (-not [DDCVol]::GetVCPFeatureAndVCPFeatureReply($h, 0x62, [IntPtr]::Zero, [ref]$cur, [ref]$max)) { throw "El monitor $Monitor no responde a DDC/CI (activalo en el menu OSD)." }

switch -Regex ($Valor) {
  '^$'        { "Volumen: $cur / $max"; return }
  '^mute$'    { [void][DDCVol]::SetVCPFeature($h, 0x8D, 1); "Silenciado"; return }
  '^unmute$'  { [void][DDCVol]::SetVCPFeature($h, 0x8D, 2); "Sonido activado"; return }
  '^[+-]\d+$' { $new = $cur + [int]$Valor }
  '^\d+$'     { $new = [int]$Valor }
  default     { throw "Valor no valido: $Valor" }
}
$new = [Math]::Max(0, [Math]::Min($max, $new))
[void][DDCVol]::SetVCPFeature($h, 0x62, [uint32]$new)
"Volumen: $cur -> $new / $max"
