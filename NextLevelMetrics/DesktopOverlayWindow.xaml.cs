using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NextLevelMetrics;

public partial class DesktopOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x20;
    private const nint WsExToolWindow = 0x80;
    private const nint WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    public DesktopOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            nint window = new WindowInteropHelper(this).Handle;
            nint style = GetWindowLongPtr(window, GwlExStyle);
            SetWindowLongPtr(window, GwlExStyle,
                style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
    }

    public void SetMetrics(string text) => MetricsText.Text = text;
}
