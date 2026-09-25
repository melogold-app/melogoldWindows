using System.Runtime.InteropServices;
using Melogold.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace Melogold.App.Services;

/// <summary>
/// Кнопки ⏮ ⏯ ⏭ на миниатюре окна в панели задач (docs/PROMPT.md §5.2): <c>ITaskbarList3.ThumbBarAddButtons</c>,
/// нажатия приходят в оконную процедуру как <c>WM_COMMAND</c>/<c>THBN_CLICKED</c>. Иконки — светлые или тёмные по
/// теме панели задач; после перезапуска Проводника кнопки добавляются снова (<c>TaskbarButtonCreated</c>).
/// </summary>
public sealed class TaskbarButtons
{
    private const uint PreviousId = 1, PlayPauseId = 2, NextId = 3;
    private const int WmCommand = 0x0111;
    private const int ThbnClicked = 0x1800;
    private const int GwlpWndProc = -4;

    private readonly PlayerEngine _engine;
    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarButtonCreated");
    private readonly WndProc _proc;
    private readonly IntPtr _previousProc;
    private readonly Dictionary<string, IntPtr> _icons = [];
    private ITaskbarList3? _taskbar;
    private bool _added;
    private bool? _shownPlaying;
    private bool? _shownEnabled;

    public TaskbarButtons(PlayerEngine engine, IntPtr hwnd, DispatcherQueue dispatcher)
    {
        _engine = engine;
        _hwnd = hwnd;
        _dispatcher = dispatcher;
        // Своя оконная процедура поверх процедуры WinUI: нажатия кнопок и пересоздание панели задач
        _proc = Procedure;
        _previousProc = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc));
        engine.StateChanged += () => dispatcher.TryEnqueue(Update);
        engine.TrackChanged += () => dispatcher.TryEnqueue(Update);
    }

    /// <summary>Добавить кнопки; повторяется по <c>TaskbarButtonCreated</c>.</summary>
    public void Add()
    {
        try
        {
            _taskbar ??= (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
            var buttons = new[]
            {
                Button(PreviousId, "previous", "Previous"),
                Button(PlayPauseId, _engine.IsPlaying ? "pause" : "play", _engine.IsPlaying ? "Pause" : "Play"),
                Button(NextId, "next", "Next"),
            };
            _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            _added = true;
            _shownPlaying = null;
            _shownEnabled = null;
            Update();
        }
        catch (COMException e)
        {
            Log.Warn("Thumbnail toolbar not added", e);
        }
    }

    private void Update()
    {
        if (!_added || _taskbar is null) return;
        var playing = _engine.IsPlaying;
        var enabled = _engine.Current is not null;
        if (playing == _shownPlaying && enabled == _shownEnabled) return;
        _shownPlaying = playing;
        _shownEnabled = enabled;
        try
        {
            var buttons = new[]
            {
                Button(PreviousId, "previous", "Previous", enabled),
                Button(PlayPauseId, playing ? "pause" : "play", playing ? "Pause" : "Play", enabled),
                Button(NextId, "next", "Next", enabled),
            };
            _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
        }
        catch (COMException e)
        {
            Log.Warn("Thumbnail toolbar not updated", e);
        }
    }

    private ThumbButton Button(uint id, string icon, string tip, bool enabled = true) => new()
    {
        dwMask = 0x2 | 0x4 | 0x8, // THB_ICON | THB_TOOLTIP | THB_FLAGS
        iId = id,
        hIcon = Icon(icon),
        szTip = Loc.Get(tip),
        dwFlags = enabled ? 0u : 1u, // THBF_ENABLED : THBF_DISABLED
    };

    /// <summary>Иконка под тему панели задач: на тёмной — светлый значок.</summary>
    private IntPtr Icon(string name)
    {
        var lightTaskbar = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value == 1;
        var key = $"{name}-{(lightTaskbar ? "dark" : "light")}";
        if (_icons.TryGetValue(key, out var handle)) return handle;
        var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForWindow(_hwnd));
        handle = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "Thumbbar", key + ".ico"), 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        _icons[key] = handle;
        return handle;
    }

    private IntPtr Procedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmCommand && ((wParam.ToInt64() >> 16) & 0xFFFF) == ThbnClicked)
        {
            var id = (uint)(wParam.ToInt64() & 0xFFFF);
            _dispatcher.TryEnqueue(() =>
            {
                switch (id)
                {
                    case PreviousId: _engine.Previous(); break;
                    case PlayPauseId: _engine.TogglePlayPause(); break;
                    case NextId: _engine.Next(); break;
                }
            });
            return IntPtr.Zero;
        }
        if (message == _taskbarCreated) _dispatcher.TryEnqueue(Add);
        return CallWindowProc(_previousProc, hwnd, message, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButton
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;

        public uint dwFlags;
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr tab, IntPtr hwnd);
        void UnregisterTab(IntPtr tab);
        void SetTabOrder(IntPtr tab, IntPtr insertBefore);
        void SetTabActive(IntPtr tab, IntPtr hwnd, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }
}
