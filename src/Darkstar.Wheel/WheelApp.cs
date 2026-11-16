using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Darkstar.Wheel;

/// <summary>
/// The program around the wheel: an icon in the notification area, a keyboard hook and a joystick
/// poll that only react while DCS is in front, and the step that turns a pick into a file for the
/// client hook.
///
/// Driving it:
/// - the wheel key or the wheel button opens it; pressed again it sends the highlighted slice, or
///   closes the wheel when nothing is highlighted;
/// - 1-8 (top row or numpad) send that slice at once;
/// - the hat, or the left and right arrow keys, highlight a slice; Enter sends it;
/// - Esc closes.
/// </summary>
internal sealed class WheelApp : ApplicationContext
{
    private const string Title = "D.A.R.K.S.T.A.R. Radio Wheel";

    /// <summary>How long a pick may sit in the outbox before the player is told nobody took it.</summary>
    private static readonly TimeSpan PickUpWarning = TimeSpan.FromSeconds(3);

    private readonly WheelSettings _settings;
    private readonly string _settingsPath;
    private readonly IReadOnlyList<CallDefinition> _slices = RadioWheel.Slices;
    private readonly WheelWindow _wheel;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _bindingItem;
    private readonly System.Windows.Forms.Timer _tick;

    // The delegate has to outlive the hook: a collected delegate is a crash inside user32.
    private readonly Native.LowLevelKeyboardProc _hookProc;
    private IntPtr _hook;
    private readonly HashSet<int> _swallowed = new();

    private bool _buttonWasDown;
    private DateTime _closeAt = DateTime.MaxValue;
    private readonly List<(string Path, string Label, DateTime Written)> _pending = new();

    private bool _learning;
    private DateTime _learnUntil;
    private readonly uint[] _learnBaseline = new uint[Joystick.MaxDevices];

    private uint _lastPid;
    private bool _lastPidWasDcs;

    public WheelApp(WheelSettings settings, string settingsPath, string? loadProblem)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        _wheel = new WheelWindow(_slices);

        _bindingItem = new ToolStripMenuItem { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_bindingItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Learn the wheel key or joystick button...", null, (_, _) => StartLearning());
        menu.Items.Add("Open the settings file", null, (_, _) => OpenInShell(_settingsPath));
        menu.Items.Add("Open the folder with the DCS hook", null, (_, _) =>
            OpenInShell(Path.Combine(AppContext.BaseDirectory, "dcs", "Scripts", "Hooks")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = Title,
            ContextMenuStrip = menu,
            Visible = true,
        };
        UpdateBindingText();

        _hookProc = OnKeyboard;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);

        _tick = new System.Windows.Forms.Timer { Interval = 30 };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        if (_hook == IntPtr.Zero)
            Balloon("The wheel key will not work", "Windows refused the keyboard hook. A joystick button still works.");
        else if (loadProblem != null)
            Balloon("Settings not readable", loadProblem);
        else
            Balloon("Radio wheel ready", $"{DescribeBinding()} opens it while DCS is in front. Right-click this icon for settings.");
    }

    // ---- keyboard ------------------------------------------------------------------------------

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var message = wParam.ToInt32();
            var down = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            var up = message is Native.WM_KEYUP or Native.WM_SYSKEYUP;

            // Swallowed on the way down, so swallowed on the way up too - DCS must not see half a press.
            if (down && OnKeyDown(vk))
            {
                _swallowed.Add(vk);
                return (IntPtr)1;
            }

            if (up && _swallowed.Remove(vk)) return (IntPtr)1;
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>True when the key was for the wheel and DCS must not see it.</summary>
    private bool OnKeyDown(int vk)
    {
        if (_learning)
        {
            if (vk == 0x1B) StopLearning("Nothing changed.");   // Esc
            else LearnKey(vk);
            return true;
        }

        if (vk == _settings.KeyCode && vk != 0)
        {
            if (_swallowed.Contains(vk)) return true;               // held down: auto-repeat
            if (!_wheel.Visible && !DcsInFront()) return false;     // typing in another program
            OpenOrConfirm();
            return true;
        }

        if (!_wheel.Visible) return false;

        if (vk is >= 0x31 and <= 0x39) { Send(vk - 0x31); return true; }    // 1-9 on the top row
        if (vk is >= 0x61 and <= 0x69) { Send(vk - 0x61); return true; }    // numpad 1-9
        if (vk == 0x0D) { if (_wheel.Highlighted is { } marked) Send(marked); return true; }   // Enter
        if (vk == 0x1B) { CloseWheel(); return true; }                      // Esc
        if (vk == 0x25) { Step(-1); return true; }                          // left arrow
        if (vk == 0x27) { Step(+1); return true; }                          // right arrow

        return false;
    }

    // ---- the wheel -----------------------------------------------------------------------------

    private void OpenOrConfirm()
    {
        if (!_wheel.Visible)
        {
            _closeAt = DateTime.MaxValue;
            _wheel.Open(DcsArea(), _settings.Size, _settings.Callsign,
                $"1-{_slices.Count} send · Enter · Esc");
            return;
        }

        if (_wheel.Highlighted is { } marked) Send(marked);
        else CloseWheel();
    }

    private void Step(int direction)
    {
        var count = _slices.Count;
        var next = _wheel.Highlighted is { } current ? (current + direction + count) % count : 0;
        _wheel.Highlight(next);
    }

    private void Send(int slice)
    {
        if (slice < 0 || slice >= _slices.Count) return;
        if (_closeAt != DateTime.MaxValue) return;   // already sent, the wheel is on its way out

        var call = _slices[slice];
        var line = RadioWheel.ChatLine(_settings, call);

        try
        {
            var path = RadioWheel.Write(RadioWheel.OutboxFolder, line);
            _pending.Add((path, call.MenuLabel, DateTime.UtcNow));
            _wheel.Highlight(slice);
            _wheel.ShowStatus($"Sent: {call.MenuLabel}");
            _closeAt = DateTime.UtcNow.AddMilliseconds(600);
        }
        catch (Exception ex)
        {
            CloseWheel();
            Balloon("Could not send", ex.Message);
        }
    }

    private void CloseWheel()
    {
        _closeAt = DateTime.MaxValue;
        _wheel.CloseWheel();
    }

    // ---- every 30 ms ---------------------------------------------------------------------------

    private void OnTick()
    {
        if (_learning)
        {
            LearnFromJoysticks();
            return;
        }

        if (_wheel.Visible && DateTime.UtcNow >= _closeAt) CloseWheel();

        // Alt-Tab with the wheel open: it goes, rather than floating over the desktop.
        if (_wheel.Visible && !DcsInFront()) CloseWheel();

        PollJoystick();
        CheckPickedUp();
    }

    private void PollJoystick()
    {
        if (_settings.JoystickId < 0) return;

        var state = Joystick.Read(_settings.JoystickId);
        if (!state.Connected) return;

        var down = Joystick.IsDown(state.Buttons, _settings.JoystickButton);
        if (down && !_buttonWasDown && (_wheel.Visible || DcsInFront())) OpenOrConfirm();
        _buttonWasDown = down;

        // A released hat keeps the highlight, so the slice can be confirmed with the button.
        if (_wheel.Visible && _settings.UseHat && _closeAt == DateTime.MaxValue &&
            RadioWheel.SliceForHat(state.Pov, _slices.Count) is { } slice && slice != _wheel.Highlighted)
            _wheel.Highlight(slice);
    }

    /// <summary>
    /// The client hook deletes a pick as soon as DCS sends it. One still lying there after a few
    /// seconds was not sent - and the player should hear that from the wheel, not wait for an
    /// answer that is not coming.
    /// </summary>
    private void CheckPickedUp()
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var (path, label, written) = _pending[i];

            if (!File.Exists(path))
            {
                _pending.RemoveAt(i);
            }
            else if (DateTime.UtcNow - written > PickUpWarning)
            {
                _pending.RemoveAt(i);
                Balloon($"\"{label}\" was not sent",
                    "DCS has to be in a multiplayer mission, connected to a server as a client, " +
                    "with Darkstar-Wheel.lua in Saved Games\\DCS\\Scripts\\Hooks.");
            }
        }
    }

    // ---- learning the key or button ------------------------------------------------------------

    private void StartLearning()
    {
        CloseWheel();
        _learning = true;
        _learnUntil = DateTime.UtcNow.AddSeconds(15);

        for (var id = 0; id < Joystick.MaxDevices; id++)
        {
            var state = Joystick.Read(id);
            _learnBaseline[id] = state.Connected ? state.Buttons : 0;
        }

        Balloon("Learning", "Press the key or the joystick button that should open the wheel. Esc cancels.");
    }

    private void LearnKey(int vk)
    {
        _settings.KeyCode = vk;
        _settings.KeyName = ((Keys)vk).ToString();
        StopLearning($"The wheel key is now {_settings.KeyName}.");
    }

    private void LearnFromJoysticks()
    {
        if (DateTime.UtcNow > _learnUntil)
        {
            StopLearning("Nothing was pressed - nothing changed.");
            return;
        }

        for (var id = 0; id < Joystick.MaxDevices; id++)
        {
            var state = Joystick.Read(id);
            if (!state.Connected) continue;

            // Only a button that went down since learning started: one already held when the menu
            // was clicked, or a switch that is simply on, must not be taken for the answer.
            var pressed = state.Buttons & ~_learnBaseline[id];
            _learnBaseline[id] &= state.Buttons;

            var button = Joystick.FirstButton(pressed);
            if (button == 0) continue;

            _settings.JoystickId = id;
            _settings.JoystickButton = button;
            _buttonWasDown = true;   // the press that was just learnt must not open the wheel too
            StopLearning($"The wheel button is now button {button} on joystick {id + 1}.");
            return;
        }
    }

    private void StopLearning(string message)
    {
        _learning = false;
        var error = _settings.Save(_settingsPath);
        UpdateBindingText();
        Balloon("Radio wheel", error == null ? message : $"{message} Could not save it: {error}");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private string DescribeBinding()
    {
        var key = _settings.KeyCode != 0 ? _settings.KeyName : "";
        var button = _settings.JoystickId >= 0 && _settings.JoystickButton > 0
            ? $"button {_settings.JoystickButton} on joystick {_settings.JoystickId + 1}"
            : "";
        return (key, button) switch
        {
            ("", "") => "No key or button",
            (_, "") => key,
            ("", _) => button,
            _ => $"{key} or {button}",
        };
    }

    private void UpdateBindingText()
    {
        _bindingItem.Text = $"Opens with: {DescribeBinding()} · talks to {_settings.Callsign}";
    }

    private bool DcsInFront()
    {
        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return false;

        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == _lastPid) return _lastPidWasDcs;

        bool isDcs;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            isDcs = process.ProcessName.Equals("DCS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            isDcs = false;
        }

        _lastPid = pid;
        _lastPidWasDcs = isDcs;
        return isDcs;
    }

    /// <summary>The DCS window when it is in front, otherwise the main screen.</summary>
    private Rectangle DcsArea()
    {
        var window = Native.GetForegroundWindow();
        if (window != IntPtr.Zero && DcsInFront() && Native.GetWindowRect(window, out var r) &&
            r.Right > r.Left && r.Bottom > r.Top)
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

        return Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    }

    private void Balloon(string title, string text)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(4000);
    }

    private void OpenInShell(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Balloon("Not found", path);
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Balloon("Could not open it", ex.Message);
        }
    }

    protected override void ExitThreadCore()
    {
        _tick.Stop();
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _wheel.Close();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
