// M3-04D enables WinForms only for NotifyIcon. Keep existing unqualified Cursor usages
// bound to the WPF input type instead of System.Windows.Forms.Cursor.
global using Cursor = System.Windows.Input.Cursor;
