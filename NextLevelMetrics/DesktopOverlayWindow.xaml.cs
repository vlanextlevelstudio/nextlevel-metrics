using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Documents;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace NextLevelMetrics;

public partial class DesktopOverlayWindow : Window
{
    private OverlaySettings? _lastSettings;
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x20;
    private const nint WsExToolWindow = 0x80;
    private const nint WsExNoActivate = 0x08000000;
    private static readonly nint HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const double MarginPixels = 5;
    private const double TopMarginPixels = 2;
    private const double NotificationGapPixels = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after,
        string className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    public DesktopOverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdatePosition();
        SizeChanged += (_, _) => UpdatePosition();
        SourceInitialized += (_, _) =>
        {
            nint window = new WindowInteropHelper(this).Handle;
            nint style = GetWindowLongPtr(window, GwlExStyle);
            SetWindowLongPtr(window, GwlExStyle,
                style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
    }

    internal void SetMetrics(OverlayPresentation presentation, OverlaySettings settings)
    {
        _lastSettings = settings;
        double scale = settings.TamanoPorcentaje / 100.0;
        MetricsText.FontFamily = new Media.FontFamily(OverlaySettings.FuenteUtilizable(settings.Fuente));
        MetricsText.FontWeight = settings.Fuente == "Segoe UI Semibold"
            ? FontWeights.SemiBold : FontWeights.Normal;
        MetricsText.FontSize = 17 * scale;
        OverlayBorder.Padding = new Thickness(8 * scale, 5 * scale,
            8 * scale, 5 * scale);
        OverlayBorder.CornerRadius = new CornerRadius(4 * scale);
        if (MetricsText.Effect is Media.Effects.DropShadowEffect shadow)
        {
            shadow.BlurRadius = 4 * scale;
            shadow.ShadowDepth = scale;
        }
        OverlayBorder.Background = settings.Fondo switch
        {
            OverlayBackground.OscuroSuave =>
                new Media.SolidColorBrush(Media.Color.FromArgb(160, 20, 20, 20)),
            OverlayBackground.NegroSolido => Media.Brushes.Black,
            _ => Media.Brushes.Transparent
        };
        MetricsText.Inlines.Clear();
        foreach (OverlayPart part in presentation.Parts)
        {
            var color = (Media.Color)Media.ColorConverter.ConvertFromString(part.Color)!;
            MetricsText.Inlines.Add(new Run(part.Text)
            {
                Foreground = new Media.SolidColorBrush(color)
            });
        }

        UpdateLayout();
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (_lastSettings is null) return;
        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return;
        System.Drawing.Rectangle bounds = Forms.Screen.PrimaryScreen!.Bounds;
        Media.Matrix toDips = source.CompositionTarget.TransformFromDevice;
        System.Windows.Point upperLeft = toDips.Transform(
            new System.Windows.Point(bounds.Left, bounds.Top));
        System.Windows.Point lowerRight = toDips.Transform(
            new System.Windows.Point(bounds.Right, bounds.Bottom));
        double marginX = MarginPixels * toDips.M11;
        double topMargin = TopMarginPixels * toDips.M22;
        bool right = _lastSettings.Posicion is OverlayPosition.SuperiorDerecha or OverlayPosition.InferiorDerecha;
        bool bottom = _lastSettings.Posicion is OverlayPosition.InferiorIzquierda or OverlayPosition.InferiorDerecha;
        if (!bottom)
        {
            Left = right ? lowerRight.X - ActualWidth - marginX : upperLeft.X + marginX;
            Top = upperLeft.Y + topMargin;
            return;
        }

        double widthPixels = ActualWidth / toDips.M11;
        double heightPixels = ActualHeight / toDips.M22;
        double xPixels = bounds.Left + MarginPixels;
        double yPixels = bounds.Bottom - heightPixels - MarginPixels;
        if (TryGetTaskbar(bounds, out double barTop, out double barHeight,
            out double? notificationLeft))
        {
            if (right && notificationLeft is double leftEdge)
                xPixels = Math.Max(bounds.Left + MarginPixels,
                    leftEdge - widthPixels - NotificationGapPixels);
            yPixels = barTop + (barHeight - heightPixels) / 2;
            // Si el HUD supera la altura de la barra, se mantiene completo en pantalla.
            yPixels = Math.Clamp(yPixels, bounds.Top + MarginPixels,
                bounds.Bottom - heightPixels - MarginPixels);
        }

        System.Windows.Point target = toDips.Transform(
            new System.Windows.Point(xPixels, yPixels));
        Left = target.X;
        Top = target.Y;
        if (IsVisible)
        {
            nint window = new WindowInteropHelper(this).Handle;
            SetWindowPos(window, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    private static bool TryGetTaskbar(System.Drawing.Rectangle monitor,
        out double top, out double height, out double? notificationLeft)
    {
        top = height = 0;
        notificationLeft = null;
        nint bar = FindWindow("Shell_TrayWnd", null);
        if (bar == 0 || !GetWindowRect(bar, out NativeRect barRect)) return false;
        int barWidth = barRect.Right - barRect.Left;
        int barHeight = barRect.Bottom - barRect.Top;
        if (barWidth <= 0 || barHeight <= 0 || barWidth <= barHeight) return false;

        // GetWindowRect puede estar virtualizado; Screen.Bounds da el ancho físico.
        double toPhysical = (double)monitor.Width / barWidth;
        if (toPhysical is < 0.5 or > 4) return false;
        height = barHeight * toPhysical;
        top = monitor.Bottom - height;

        nint notification = FindWindowEx(bar, 0, "TrayNotifyWnd", null);
        if (notification != 0 && GetWindowRect(notification, out NativeRect area) &&
            area.Left > barRect.Left && area.Right <= barRect.Right &&
            area.Top < barRect.Bottom && area.Bottom > barRect.Top)
        {
            notificationLeft = monitor.Left + (area.Left - barRect.Left) * toPhysical;
        }
        return true;
    }
}
