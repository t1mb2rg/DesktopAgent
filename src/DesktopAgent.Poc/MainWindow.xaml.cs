using System.Collections.ObjectModel;
using System.Windows;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Poc;

public partial class MainWindow : Window
{
    private readonly IDisplayTopologyReader _displayTopologyReader;
    private readonly ObservableCollection<DisplayRow> _displayRows = [];

    public MainWindow(IDisplayTopologyReader displayTopologyReader)
    {
        InitializeComponent();

        _displayTopologyReader = displayTopologyReader;
        DisplaysGrid.ItemsSource = _displayRows;

        Loaded += (_, _) => RefreshDisplays();
    }

    private void RefreshDisplays_Click(object sender, RoutedEventArgs e) =>
        RefreshDisplays();

    private void RefreshDisplays()
    {
        try
        {
            var displays = _displayTopologyReader.GetDisplays();

            _displayRows.Clear();
            foreach (var display in displays)
            {
                _displayRows.Add(DisplayRow.From(display));
            }

            StatusText.Text = $"已识别 {displays.Count} 块显示器。M1 后续只会修改用户指定的目标显示器。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取失败：{exception.Message}";
        }
    }

    private sealed record DisplayRow(
        string DeviceName,
        bool IsPrimary,
        string BoundsText,
        string WorkAreaText,
        string DpiText,
        string ScaleText)
    {
        public static DisplayRow From(DisplayDescriptor display) => new(
            display.DeviceName,
            display.IsPrimary,
            Format(display.Bounds.Left, display.Bounds.Top, display.Bounds.Width, display.Bounds.Height),
            Format(display.WorkArea.Left, display.WorkArea.Top, display.WorkArea.Width, display.WorkArea.Height),
            $"{display.DpiX} × {display.DpiY}",
            $"{display.ScaleX:P0} × {display.ScaleY:P0}");

        private static string Format(int left, int top, int width, int height) =>
            $"({left}, {top})  {width} × {height}";
    }
}
