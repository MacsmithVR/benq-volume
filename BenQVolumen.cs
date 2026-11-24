// BenQVolumen: redirige las teclas/rueda de volumen del teclado al altavoz interno del
// monitor BenQ (DDC/CI, VCP 0x62 volumen y 0x8D silencio).
// En modo Automatico solo actua cuando la salida de audio predeterminada de Windows es el BenQ;
// con otra salida (p. ej. los auriculares) la rueda sigue controlando Windows como siempre.
// Compilar con build.ps1 (C# 5, csc de .NET Framework).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mtx = new Mutex(true, "BenQVolumen_SingleInstance", out created))
        {
            if (!created) return;
            // Sin esto Windows estira la ventana como un bitmap cuando hay escalado (>100 %) y se ve borrosa.
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-3) /*PER_MONITOR_AWARE*/); } catch { }
            Application.EnableVisualStyles();
            var app = new App();
            Application.Run();
            GC.KeepAlive(app);
        }
    }
}

enum Mode { Auto, Always, Off }

class App
{
    const string MonitorTag = "BNQ";   // fabricante en el EDID
    const string AudioTag = "BenQ";    // nombre de la salida de audio
    const int Step = 1;                // pasos de 0..50 => 2 % por "clic" de rueda
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "BenQVolumen";

    readonly NotifyIcon tray;
    readonly Osd osd;
    readonly SynchronizationContext ui;
    readonly System.Windows.Forms.Timer audioTimer;
    readonly Native.LowLevelKeyboardProc hookProc; // referencia viva para el GC
    readonly IntPtr hook;
    readonly AutoResetEvent signal = new AutoResetEvent(false);
    readonly ToolStripMenuItem miAuto, miAlways, miOff, miStartup;

    volatile bool active;
    volatile bool benqIsDefault;
    Mode mode = Mode.Auto;
    int pending;
    int muteReq;

    IntPtr phys = IntPtr.Zero;
    Rectangle benqArea = Screen.PrimaryScreen.WorkingArea;
    float benqScale = 1f;

    public App()
    {
        ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        osd = new Osd();

        var menu = new ContextMenuStrip();
        miAuto = new ToolStripMenuItem("Automático (solo si el audio sale por el BenQ)", null, delegate { SetMode(Mode.Auto); });
        miAlways = new ToolStripMenuItem("Siempre controlar el BenQ", null, delegate { SetMode(Mode.Always); });
        miOff = new ToolStripMenuItem("Desactivado (rueda = Windows)", null, delegate { SetMode(Mode.Off); });
        miStartup = new ToolStripMenuItem("Iniciar con Windows", null, delegate { ToggleStartup(); });
        miStartup.Checked = IsStartupEnabled();
        menu.Items.Add(miAuto);
        menu.Items.Add(miAlways);
        menu.Items.Add(miOff);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miStartup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Salir", null, delegate { Exit(); }));

        tray = new NotifyIcon();
        tray.Icon = MakeIcon();
        tray.ContextMenuStrip = menu;
        tray.Text = "BenQ Volumen";
        tray.Visible = true;
        tray.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ShowCurrentAsync();
        };

        audioTimer = new System.Windows.Forms.Timer();
        audioTimer.Interval = 2000;
        audioTimer.Tick += delegate { RefreshAudio(); };
        audioTimer.Start();
        RefreshAudio();
        UpdateMenu();

        var worker = new Thread(WorkerLoop);
        worker.IsBackground = true;
        worker.Start();

        hookProc = HookCallback;
        hook = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, hookProc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
            MessageBox.Show("No se pudo instalar el hook de teclado.", "BenQ Volumen");
    }

    // ---------- Modo / estado ----------
    void SetMode(Mode m) { mode = m; RecomputeActive(); UpdateMenu(); }

    void RecomputeActive()
    {
        active = mode == Mode.Always || (mode == Mode.Auto && benqIsDefault);
    }

    void UpdateMenu()
    {
        miAuto.Checked = mode == Mode.Auto;
        miAlways.Checked = mode == Mode.Always;
        miOff.Checked = mode == Mode.Off;
        string state = active ? "rueda → BenQ" : "rueda → Windows";
        SetTip("BenQ Volumen (" + state + ")");
    }

    void RefreshAudio()
    {
        bool before = active;
        string name = AudioDefault.GetDefaultOutputName();
        benqIsDefault = name != null && name.IndexOf(AudioTag, StringComparison.OrdinalIgnoreCase) >= 0;
        RecomputeActive();
        if (before != active) UpdateMenu();
    }

    void SetTip(string t)
    {
        if (t.Length > 63) t = t.Substring(0, 63);
        tray.Text = t;
    }

    // ---------- Hook de teclado ----------
    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && active)
        {
            int vk = Marshal.ReadInt32(lParam);
            if (vk == 0xAD || vk == 0xAE || vk == 0xAF)
            {
                int msg = wParam.ToInt32();
                if (msg == 0x100 || msg == 0x104) // WM_KEYDOWN / WM_SYSKEYDOWN
                {
                    if (vk == 0xAF) Interlocked.Add(ref pending, Step);
                    else if (vk == 0xAE) Interlocked.Add(ref pending, -Step);
                    else Interlocked.Exchange(ref muteReq, 1);
                    signal.Set();
                }
                return (IntPtr)1; // no llega a Windows
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // ---------- DDC/CI (hilo de trabajo) ----------
    bool showRequested;

    void ShowCurrentAsync() { showRequested = true; signal.Set(); }

    void WorkerLoop()
    {
        while (true)
        {
            signal.WaitOne();
            try { Apply(); }
            catch { ResetMonitor(); }
        }
    }

    void Apply()
    {
        if (!EnsureMonitor()) return;
        uint cur, max, mute, mmax;

        bool toggleMute = Interlocked.Exchange(ref muteReq, 0) == 1;
        int delta = Interlocked.Exchange(ref pending, 0);
        bool show = showRequested; showRequested = false;
        if (!toggleMute && delta == 0 && !show) return;

        if (!Get(0x62, out cur, out max))
        {
            ResetMonitor();
            if (!EnsureMonitor() || !Get(0x62, out cur, out max)) return;
        }
        bool muted = Get(0x8D, out mute, out mmax) && mute == 1;

        if (toggleMute)
        {
            muted = !muted;
            Native.SetVCPFeature(phys, 0x8D, muted ? 1u : 2u);
        }
        if (delta != 0)
        {
            int nv = Math.Max(0, Math.Min((int)max, (int)cur + delta));
            if (nv != (int)cur) Native.SetVCPFeature(phys, 0x62, (uint)nv);
            cur = (uint)nv;
            if (muted) { Native.SetVCPFeature(phys, 0x8D, 2u); muted = false; } // como Windows: subir/bajar quita el silencio
        }

        int v = (int)cur, m = (int)max; bool mu = muted; Rectangle area = benqArea; float sc = benqScale;
        ui.Post(delegate
        {
            osd.ShowValue(v, m, mu, area, sc);
            SetTip("BenQ: " + (mu ? "silenciado" : v + " / " + m));
        }, null);
    }

    bool Get(byte code, out uint cur, out uint max)
    {
        cur = 0; max = 0;
        return phys != IntPtr.Zero && Native.GetVCPFeatureAndVCPFeatureReply(phys, code, IntPtr.Zero, out cur, out max);
    }

    void ResetMonitor()
    {
        if (phys != IntPtr.Zero) Native.DestroyPhysicalMonitor(phys);
        phys = IntPtr.Zero;
    }

    bool EnsureMonitor()
    {
        if (phys != IntPtr.Zero) return true;
        IntPtr found = IntPtr.Zero;
        Rectangle area = Rectangle.Empty;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hm, IntPtr hdc, IntPtr rc, IntPtr p)
        {
            var mi = new Native.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
            if (!Native.GetMonitorInfo(hm, ref mi)) return true;
            var dd = new Native.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            for (uint i = 0; Native.EnumDisplayDevices(mi.szDevice, i, ref dd, 1 /*EDD_GET_DEVICE_INTERFACE_NAME*/); i++)
            {
                if (dd.DeviceID != null && dd.DeviceID.IndexOf(MonitorTag, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    uint n;
                    if (Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out n) && n > 0)
                    {
                        var arr = new Native.PHYSICAL_MONITOR[n];
                        if (Native.GetPhysicalMonitorsFromHMONITOR(hm, n, arr))
                        {
                            found = arr[0].h;
                            for (int k = 1; k < arr.Length; k++) Native.DestroyPhysicalMonitor(arr[k].h);
                            area = Rectangle.FromLTRB(mi.rcWork.L, mi.rcWork.T, mi.rcWork.R, mi.rcWork.B);
                            uint dx, dy;
                            try { if (Native.GetDpiForMonitor(hm, 0 /*MDT_EFFECTIVE_DPI*/, out dx, out dy) == 0) benqScale = dx / 96f; } catch { }
                        }
                    }
                    return false;
                }
                dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            }
            return true;
        }, IntPtr.Zero);
        phys = found;
        if (!area.IsEmpty) benqArea = area;
        return phys != IntPtr.Zero;
    }

    // ---------- Inicio con Windows ----------
    bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunName) != null;
    }

    void ToggleStartup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (IsStartupEnabled()) k.DeleteValue(RunName, false);
            else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
        }
        miStartup.Checked = IsStartupEnabled();
    }

    void Exit()
    {
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        tray.Visible = false;
        tray.Dispose();
        Application.Exit();
    }

    static Icon MakeIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var b = new SolidBrush(Color.FromArgb(124, 58, 237)))
                g.FillEllipse(b, 1, 1, 30, 30);
            // altavoz
            var pts = new[] { new Point(8, 13), new Point(12, 13), new Point(17, 8), new Point(17, 24), new Point(12, 19), new Point(8, 19) };
            g.FillPolygon(Brushes.White, pts);
            using (var pen = new Pen(Color.White, 2f))
            {
                g.DrawArc(pen, 15, 11, 7, 10, -60, 120);
                g.DrawArc(pen, 15, 7, 11, 18, -60, 120);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

// ---------- OSD ----------
class Osd : Form
{
    int val, max = 1;
    bool muted;
    float s = 1f; // escala DPI del monitor donde se muestra
    readonly System.Windows.Forms.Timer hideTimer;

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(280, 64);
        BackColor = Color.FromArgb(32, 32, 32);
        Opacity = 0.94;
        DoubleBuffered = true;
        hideTimer = new System.Windows.Forms.Timer();
        hideTimer.Interval = 1500;
        hideTimer.Tick += delegate { hideTimer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/ | 0x8 /*TOPMOST*/ | 0x20 /*TRANSPARENT*/;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND (Windows 11)
        try { Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
    }

    int S(float px) { return (int)Math.Round(px * s); }

    public void ShowValue(int v, int m, bool mu, Rectangle area, float scale)
    {
        val = v; max = Math.Max(1, m); muted = mu; s = scale;
        int w = S(280), h = S(64);
        Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Bottom - h - S(48), w, h);
        if (!Visible) Show();
        Invalidate();
        hideTimer.Stop();
        hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; // ClearType se ve mal en ventanas semitransparentes
        using (var f = new Font("Segoe UI", 14f * s, GraphicsUnit.Pixel))
        using (var fb = new Font("Segoe UI Semibold", 14f * s, GraphicsUnit.Pixel))
        {
            g.DrawString("Altavoz BenQ", f, Brushes.Gainsboro, S(16), S(10));
            string right = muted ? "Silenciado" : val.ToString();
            var sz = g.MeasureString(right, fb);
            g.DrawString(right, fb, Brushes.White, Width - S(16) - sz.Width, S(10));
        }
        var track = new Rectangle(S(16), S(42), Width - S(32), S(6));
        using (var b = new SolidBrush(Color.FromArgb(70, 70, 70))) g.FillRectangle(b, track);
        int w = (int)(track.Width * (double)val / max);
        var fill = muted ? Color.FromArgb(120, 120, 120) : Color.FromArgb(139, 92, 246);
        using (var b = new SolidBrush(fill)) g.FillRectangle(b, track.X, track.Y, w, track.Height);
    }
}

// ---------- Salida de audio predeterminada (Core Audio) ----------
static class AudioDefault
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        int OpenPropertyStore(int access, out IPropertyStore store);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        int GetCount(out int c);
        int GetAt(int i, out PROPERTYKEY key);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public IntPtr p; IntPtr p2; }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    public static string GetDefaultOutputName()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice dev;
            if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out dev) != 0 || dev == null) return null;
            IPropertyStore store;
            if (dev.OpenPropertyStore(0, out store) != 0) return null;
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            PROPVARIANT pv;
            if (store.GetValue(ref key, out pv) != 0) return null;
            string name = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null;
            PropVariantClear(ref pv);
            Marshal.ReleaseComObject(store);
            Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(en);
            return name;
        }
        catch { return null; }
    }
}

// ---------- Win32 ----------
static class Native
{
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rc, IntPtr p);

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr h;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc;
    }

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("dxva2.dll")] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint cur, out uint max);
    [DllImport("dxva2.dll")] public static extern bool SetVCPFeature(IntPtr h, byte code, uint val);

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
}
