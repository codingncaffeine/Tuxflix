using System.Runtime.InteropServices;

namespace Tuxflix.App.Tv;

/// <summary>
/// Controllers through SDL3, loaded when it is there (<c>libSDL3.so.0</c>, the sdl3 package) and
/// left alone when it is not: the interface then runs on the keyboard.
/// </summary>
/// <remarks>
/// Only SDL's gamepad subsystem starts: no window, no video, no sound, and no signal handlers
/// (the application keeps its own for SIGTERM). SDL's own database names each controller's
/// buttons by position, so every pad it knows (Xbox, PlayStation, Switch, 8BitDo, Steam) comes
/// out with the same layout. Every call happens on the input thread: SDL wants its events
/// pumped on the thread that started it. A pad plugged in or out arrives as an event, and SDL
/// sends one for each pad already connected when it starts.
/// </remarks>
internal sealed unsafe class Sdl3PadSource : IPadSource
{
    private const uint InitGamepad = 0x00002000;
    private const uint EventAxis = 0x650;
    private const uint EventButtonDown = 0x651;
    private const uint EventButtonUp = 0x652;
    private const uint EventAdded = 0x653;
    private const uint EventRemoved = 0x654;

    /// <summary>How often the queue is looked at while a pad is connected: 125 times a second, a USB pad's own rate.</summary>
    private static readonly TimeSpan Connected = TimeSpan.FromMilliseconds(8);

    /// <summary>With none connected, only plugging one in is waited for.</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<uint, nint> _pads = [];
    private readonly byte[] _event = new byte[128];
    private nint _library;
    private bool _started;

    private delegate* unmanaged<uint, byte> _init;
    private delegate* unmanaged<void> _quit;
    private delegate* unmanaged<byte*, byte*, byte> _setHint;
    private delegate* unmanaged<byte*, byte> _pollEvent;
    private delegate* unmanaged<uint, nint> _openGamepad;
    private delegate* unmanaged<nint, void> _closeGamepad;
    private delegate* unmanaged<nint, byte*> _gamepadName;
    private delegate* unmanaged<byte*> _getError;

    public bool Open(out string? unavailable)
    {
        if (!NativeLibrary.TryLoad("libSDL3.so.0", out _library) && !NativeLibrary.TryLoad("libSDL3.so", out _library))
        {
            unavailable = "SDL3 is not installed (the sdl3 package)";
            return false;
        }

        try
        {
            _init = (delegate* unmanaged<uint, byte>)NativeLibrary.GetExport(_library, "SDL_Init");
            _quit = (delegate* unmanaged<void>)NativeLibrary.GetExport(_library, "SDL_Quit");
            _setHint = (delegate* unmanaged<byte*, byte*, byte>)NativeLibrary.GetExport(_library, "SDL_SetHint");
            _pollEvent = (delegate* unmanaged<byte*, byte>)NativeLibrary.GetExport(_library, "SDL_PollEvent");
            _openGamepad = (delegate* unmanaged<uint, nint>)NativeLibrary.GetExport(_library, "SDL_OpenGamepad");
            _closeGamepad = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(_library, "SDL_CloseGamepad");
            _gamepadName = (delegate* unmanaged<nint, byte*>)NativeLibrary.GetExport(_library, "SDL_GetGamepadName");
            _getError = (delegate* unmanaged<byte*>)NativeLibrary.GetExport(_library, "SDL_GetError");
        }
        catch (EntryPointNotFoundException ex)
        {
            unavailable = $"the SDL library found is not SDL3 ({ex.Message})";
            return false;
        }

        Hint("SDL_NO_SIGNAL_HANDLERS", "1");
        Hint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        if (_init(InitGamepad) == 0)
        {
            unavailable = $"SDL3 could not start its gamepad support: {Text(_getError())}";
            return false;
        }

        _started = true;
        unavailable = null;
        return true;
    }

    public bool Next(TimeSpan timeout, out PadEvent padEvent)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            fixed (byte* buffer = _event)
            {
                while (_pollEvent(buffer) != 0)
                {
                    if (Translate(buffer, out padEvent)) return true;
                }
            }

            var left = deadline - Environment.TickCount64;
            if (left <= 0)
            {
                padEvent = default;
                return false;
            }

            Thread.Sleep((int)Math.Min(left, (_pads.Count > 0 ? Connected : Idle).TotalMilliseconds));
        }
    }

    private bool Translate(byte* buffer, out PadEvent padEvent)
    {
        var type = *(uint*)buffer;
        var which = *(uint*)(buffer + 16);
        switch (type)
        {
            case EventButtonDown or EventButtonUp:
                padEvent = new PadEvent(type == EventButtonDown ? PadEventKind.ButtonDown : PadEventKind.ButtonUp, which, (PadButton)buffer[20]);
                return true;
            case EventAxis:
                var axis = (PadAxis)buffer[20];
                var raw = *(short*)(buffer + 24);
                padEvent = PadEvent.Moved(axis, axis is PadAxis.LeftTrigger or PadAxis.RightTrigger ? Math.Max(0, raw / 32767f) : Math.Clamp(raw / 32767f, -1, 1), which);
                return true;
            case EventAdded when !_pads.ContainsKey(which):
                var pad = _openGamepad(which);
                if (pad == 0)
                {
                    Core.Diagnostics.Log.Warn($"A controller was connected but could not be opened: {Text(_getError())}");
                    break;
                }

                _pads[which] = pad;
                padEvent = new PadEvent(PadEventKind.Added, which, Name: Text(_gamepadName(pad)) ?? "Controller");
                return true;
            case EventRemoved when _pads.Remove(which, out var gone):
                _closeGamepad(gone);
                padEvent = new PadEvent(PadEventKind.Removed, which);
                return true;
        }

        padEvent = default;
        return false;
    }

    public void Dispose()
    {
        if (_started)
        {
            foreach (var pad in _pads.Values) _closeGamepad(pad);
            _pads.Clear();
            _quit();
            _started = false;
        }

        // The library stays loaded: SDL may still be finishing a thread of its own, and the
        // process is ending anyway.
    }

    private void Hint(string name, string value)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* n = nameBytes)
        fixed (byte* v = valueBytes)
        {
            _setHint(n, v);
        }
    }

    private static string? Text(byte* text) => text is null ? null : Marshal.PtrToStringUTF8((nint)text);
}
