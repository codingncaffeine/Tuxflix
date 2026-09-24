using System.Globalization;
using System.Runtime.InteropServices;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Player.Native;

namespace Tuxflix.Player;

/// <summary>Why playback of a file ended.</summary>
public enum EndReason
{
    Finished = 0,
    Stopped = 2,
    Quit = 3,
    Failed = 4,
    Redirected = 5,
}

/// <summary>A property mpv reported, with its new value.</summary>
public sealed record PlayerChange(string Name, double? Number, bool? Flag, string? Text);

/// <summary>
/// One mpv instance: options, commands, observed properties, and the events it raises.
/// </summary>
/// <remarks>
/// Events arrive on a thread of the player's own, never the UI thread; whoever shows them marshals.
/// The video is drawn by whoever owns a render context on <see cref="Handle"/>; that context must
/// be freed before this player is disposed, which is libmpv's rule, not ours.
/// </remarks>
public sealed unsafe class MpvPlayer : IDisposable
{
    private readonly Thread _events;
    private readonly Dictionary<ulong, string> _observed = [];
    private IntPtr _handle;
    private volatile bool _disposing;

    /// <param name="options">mpv options set before initialisation, such as vo, hwdec and ao.</param>
    public MpvPlayer(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _handle = LibMpv.mpv_create();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("libmpv could not create a player.");

        foreach (var (name, value) in options)
        {
            var result = LibMpv.mpv_set_option_string(_handle, name, value);
            if (result < 0) Log.Warn($"mpv refused option {name}: {LibMpv.Describe(result)}");
        }

        Check(LibMpv.mpv_initialize(_handle), "start");
        LibMpv.mpv_request_log_messages(_handle, "info");

        Observe("time-pos", MpvFormat.Double);
        Observe("duration", MpvFormat.Double);
        Observe("pause", MpvFormat.Flag);
        Observe("paused-for-cache", MpvFormat.Flag);
        Observe("seeking", MpvFormat.Flag);
        Observe("eof-reached", MpvFormat.Flag);
        Observe("volume", MpvFormat.Double);
        Observe("mute", MpvFormat.Flag);
        Observe("demuxer-cache-time", MpvFormat.Double);
        Observe("hwdec-current", MpvFormat.String);
        Observe("aid", MpvFormat.String);
        Observe("sid", MpvFormat.String);
        Observe("track-list/count", MpvFormat.Double);

        _events = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _events.Start();
    }

    /// <summary>Whether the system's libmpv can be used.</summary>
    public static bool IsAvailable => LibMpv.IsAvailable;

    /// <summary>The raw handle, for a render context.</summary>
    public IntPtr Handle => _handle;

    /// <summary>An observed property changed. Raised on the player's event thread.</summary>
    public event Action<PlayerChange>? Changed;

    /// <summary>A file finished loading and playback is about to begin.</summary>
    public event Action? FileLoaded;

    /// <summary>Playback of the current file ended, and why.</summary>
    public event Action<EndReason, string?>? Ended;

    /// <summary>Loads <paramref name="url"/>, starting <paramref name="start"/> seconds in.</summary>
    public void Load(string url, double start = 0)
    {
        if (start > 0) SetProperty("start", start.ToString("0.###", CultureInfo.InvariantCulture));
        else SetProperty("start", "none");
        Command("loadfile", url, "replace");
    }

    public void Command(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var handles = new IntPtr[arguments.Length + 1];
        try
        {
            for (var i = 0; i < arguments.Length; i++) handles[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            fixed (IntPtr* args = handles)
            {
                var result = LibMpv.mpv_command(_handle, args);
                if (result < 0) Log.Warn($"mpv command {arguments[0]} failed: {LibMpv.Describe(result)}");
            }
        }
        finally
        {
            foreach (var handle in handles)
            {
                if (handle != IntPtr.Zero) Marshal.FreeCoTaskMem(handle);
            }
        }
    }

    public void SetProperty(string name, string value)
    {
        var result = LibMpv.mpv_set_property_string(_handle, name, value);
        if (result < 0) Log.Warn($"mpv refused {name}={value}: {LibMpv.Describe(result)}");
    }

    public void SetFlag(string name, bool value)
    {
        var flag = value ? 1 : 0;
        LibMpv.mpv_set_property(_handle, name, MpvFormat.Flag, &flag);
    }

    public void SetNumber(string name, double value) => LibMpv.mpv_set_property(_handle, name, MpvFormat.Double, &value);

    public string? GetString(string name)
    {
        var value = LibMpv.mpv_get_property_string(_handle, name);
        if (value == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            LibMpv.mpv_free(value);
        }
    }

    public double? GetNumber(string name)
    {
        double value;
        return LibMpv.mpv_get_property(_handle, name, MpvFormat.Double, &value) >= 0 ? value : null;
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        var handle = _handle;
        LibMpv.mpv_wakeup(handle);
        _events.Join(TimeSpan.FromSeconds(3));
        _handle = IntPtr.Zero;
        LibMpv.mpv_terminate_destroy(handle);
    }

    private void Observe(string name, MpvFormat format)
    {
        var id = (ulong)(_observed.Count + 1);
        _observed[id] = name;
        LibMpv.mpv_observe_property(_handle, id, name, format);
    }

    private void EventLoop()
    {
        while (!_disposing)
        {
            var e = LibMpv.mpv_wait_event(_handle, -1);
            if (e is null || _disposing) break;
            try
            {
                switch (e->EventId)
                {
                    case MpvEventId.Shutdown:
                        return;
                    case MpvEventId.PropertyChange:
                        Report((MpvEventProperty*)e->Data);
                        break;
                    case MpvEventId.FileLoaded:
                        FileLoaded?.Invoke();
                        break;
                    case MpvEventId.EndFile:
                        var end = (MpvEventEndFile*)e->Data;
                        Ended?.Invoke((EndReason)end->Reason, end->Error < 0 ? LibMpv.Describe(end->Error) : null);
                        break;
                    case MpvEventId.LogMessage:
                        var log = (MpvEventLogMessage*)e->Data;
                        var level = Marshal.PtrToStringUTF8(log->Level);
                        var text = $"mpv {Marshal.PtrToStringUTF8(log->Prefix)}: {Marshal.PtrToStringUTF8(log->Text)?.TrimEnd()}";
                        if (level is "error" or "fatal") Log.Warn(text);
                        else Log.Info(text);
                        break;
                }
            }
            catch (Exception ex)
            {
                // A handler that throws must not end the only thread that drains mpv's events.
                Log.Warn("A playback event handler failed.", ex);
            }
        }
    }

    private void Report(MpvEventProperty* property)
    {
        var name = Marshal.PtrToStringUTF8(property->Name) ?? string.Empty;
        var change = property->Format switch
        {
            MpvFormat.Double => new PlayerChange(name, *(double*)property->Data, null, null),
            MpvFormat.Flag => new PlayerChange(name, null, *(int*)property->Data != 0, null),
            MpvFormat.String => new PlayerChange(name, null, null, Marshal.PtrToStringUTF8(*(IntPtr*)property->Data)),
            _ => new PlayerChange(name, null, null, null),
        };
        Changed?.Invoke(change);
    }

    private static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"mpv could not {what}: {LibMpv.Describe(result)}");
    }
}
