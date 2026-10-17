using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;

namespace SharedQuickSearch;

/// <summary>
/// WPF 动画默认按 60 帧计算。这里读取所有显示器里最高的刷新率（120 / 144 / 165 / 240 Hz），
/// 把动画帧率默认值改成它，高刷屏上的动画就是满帧的。
/// 必须在任何 Timeline 创建之前调用（App 的静态构造函数里）。
/// </summary>
internal static class FrameRate
{
    public static int Current { get; private set; } = 60;

    public static void Configure()
    {
        var best = 60;
        try
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
                if (EnumDisplaySettings(screen.DeviceName, EnumCurrentSettings, ref mode) && mode.dmDisplayFrequency is > 1 and <= 500)
                    best = Math.Max(best, mode.dmDisplayFrequency);
            }
        }
        catch { }
        Current = best;
        try
        {
            Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = best });
        }
        catch (InvalidOperationException) { } // 已经被使用过就保持系统默认，不影响功能
    }

    private const int EnumCurrentSettings = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
