using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Rimlight.SettingsUI;

/// <summary>
/// The tray flyout. Opens above the notification area, closes (hides) when it loses focus.
/// </summary>
public partial class SettingsFlyout : Window
{
    private readonly SettingsViewModel _vm;
    private DateTime _hiddenAt;
    private bool _menuOpen;

    public SettingsFlyout(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Deactivated += (_, _) => { if (!_menuOpen) HideFlyout(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideFlyout(); };
        SourceInitialized += (_, _) => ApplyWindowsLook();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 11 rounded corners + dark border colour. Silently ignored on Windows 10.</summary>
    private void ApplyWindowsLook()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int round = 2;                 // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));    // DWMWA_USE_IMMERSIVE_DARK_MODE
        int border = 0x00403A3A;       // COLORREF 0x00BBGGRR — subtle grey edge
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));  // DWMWA_BORDER_COLOR
    }

    public event EventHandler? QuitRequested;
    public event EventHandler? PreviewLyricsRequested;

    private void PreviewLyrics_Click(object sender, RoutedEventArgs e) => PreviewLyricsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when the flyout becomes visible / hidden (used to run the live meter only while open).</summary>
    public event Action<bool>? VisibilityChangedTo;

    /// <summary>Show if hidden, hide if shown. Ignores the click that caused it to lose focus.</summary>
    public void Toggle()
    {
        if (IsVisible) { HideFlyout(); return; }
        // Clicking the tray icon while open first deactivates (hides) us; don't immediately re-open.
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 250) return;
        ShowFlyout();
    }

    public void ShowFlyout()
    {
        var wa = SystemParameters.WorkArea;
        // Never taller than the screen: the settings list scrolls instead.
        Scroller.MaxHeight = Math.Max(200, wa.Height - 230);
        if (!IsVisible) Show();
        UpdateLayout();
        // Bottom-right of the primary work area, just above the tray, with a small gap like Windows' own flyouts.
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 12;
        // Always come to the very front, even over full-screen lyrics (also topmost).
        Topmost = false;
        Topmost = true;
        Activate();
        VisibilityChangedTo?.Invoke(true);
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        Hide();
        _hiddenAt = DateTime.UtcNow;
        VisibilityChangedTo?.Invoke(false);
    }

    private void DisplayButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = DisplayButton, Placement = PlacementMode.Bottom };
        foreach (var d in _vm.Displays)
        {
            var item = new MenuItem { Header = d.Name, IsChecked = d.Id == _vm.Display };
            var id = d.Id;
            item.Click += (_, _) => _vm.Display = id;
            menu.Items.Add(item);
            if (d.Id == "all") menu.Items.Add(new Separator());
        }
        menu.Closed += (_, _) => { _menuOpen = false; Activate(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }

    private void PatternButton_Click(object sender, RoutedEventArgs e) =>
        OpenMenu(PatternButton, SettingsViewModel.MotionStyles.Select(m => (m.Name, m.Style == _vm.Motion, (Action)(() => _vm.Motion = m.Style))), separatorAfterFirst: true);

    private void ChoosePicture_Click(object sender, RoutedEventArgs e)
    {
        _menuOpen = true; // keep the panel open while the file dialog is up
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a background picture for lyrics",
                Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
            };
            if (dlg.ShowDialog(this) == true) _vm.LyricsPicture = dlg.FileName;
        }
        finally { _menuOpen = false; Activate(); }
    }

    private void ResetPicture_Click(object sender, RoutedEventArgs e) => _vm.LyricsPicture = "";

    private void AmbientButton_Click(object sender, RoutedEventArgs e) =>
        OpenMenu(AmbientButton, SettingsViewModel.AmbientPresets.Select(a => (a.Name, a.Preset == _vm.Ambient, (Action)(() => _vm.Ambient = a.Preset))), separatorAfterFirst: true);

    private void LyricsStyleButton_Click(object sender, RoutedEventArgs e) =>
        OpenMenu(LyricsStyleButton, SettingsViewModel.LyricsStyles.Select(l => (l.Name, l.Style == _vm.LyricsStyle, (Action)(() => _vm.LyricsStyle = l.Style))), separatorAfterFirst: false);

    private void OpenMenu(Button anchor, IEnumerable<(string Name, bool Checked, Action Pick)> items, bool separatorAfterFirst)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        bool first = true;
        foreach (var (name, isChecked, pick) in items)
        {
            var item = new MenuItem { Header = name, IsChecked = isChecked };
            item.Click += (_, _) => pick();
            menu.Items.Add(item);
            if (first && separatorAfterFirst) menu.Items.Add(new Separator());
            first = false;
        }
        menu.Closed += (_, _) => { _menuOpen = false; Activate(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }

    private void PlayerButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PlayerButton, Placement = PlacementMode.Bottom };
        foreach (var (id, name) in _vm.Players)
        {
            var item = new MenuItem { Header = name, IsChecked = id == _vm.PreferredPlayer };
            var pick = id;
            item.Click += (_, _) => _vm.PreferredPlayer = pick;
            menu.Items.Add(item);
            if (id == "Auto" && _vm.Players.Count > 1) menu.Items.Add(new Separator());
        }
        menu.Closed += (_, _) => { _menuOpen = false; Activate(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Rimlight.Core.Diagnostics.Log.Error("Couldn't open logs folder", ex); }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
