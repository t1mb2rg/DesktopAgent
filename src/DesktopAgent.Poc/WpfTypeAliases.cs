// M3-04D enables WinForms only for NotifyIcon. Keep existing unqualified WPF UI
// type names bound to WPF so System.Windows.Forms / System.Drawing imports do not
// retroactively make the mature PoC windows ambiguous.
global using Cursor = System.Windows.Input.Cursor;
global using MessageBox = System.Windows.MessageBox;
global using Clipboard = System.Windows.Clipboard;
global using Rectangle = System.Windows.Shapes.Rectangle;
global using Color = System.Windows.Media.Color;
global using Brushes = System.Windows.Media.Brushes;
