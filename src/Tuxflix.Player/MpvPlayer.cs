using System.Collections.Concurrent;
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
/// Commands and property changes are queued with mpv's asynchronous API and return at once, so any
/// thread may send them, the UI thread included; a refusal comes back as a reply and is logged.
/// Reads (<see cref="GetString"/>, <see cref="GetNumber"/>), creating the player and disposing it
/// wait for mpv's core and belong on a worker thread. The video is drawn by whoever owns a render
/// context on <see cref="Handle"/>; that context must be freed before this player is disposed,
/// which is libmpv's rule, not ours.
/// </remarks>
public sealed unsafe class MpvPlayer : IDisposable
{
    private readonly Thread _events;
    private readonly Dictionary<ulong, string> _observed = [];
    private readonly ConcurrentDictionary<ulong, string> _requests = new();
    private long _nextRequest;
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

    /// <summary>A file finished loading and playback is about to begin. Raised on the player's event thread.</summary>
    public event Action? FileLoaded;

    /// <summary>Playback of the current file ended, and why. Raised on the player's event thread.</summary>
    public event Action<EndReason, string?>? Ended;

    /// <summary>Loads <paramref name="url"/>, starting <paramref name="start"/> seconds in. Returns at once.</summary>
    public void Load(string url, double start = 0)
    {
        // mpv runs queued requests in the order they were sent: the start point is set before the load.
        PostProperty("start", start > 0 ? start.ToString("0.###", CultureInfo.InvariantCulture) : "none");
        PostCommand("loadfile", url, "replace");
    }

    /// <summary>Queues a command and returns at once.</summary>
    public void PostCommand(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (_disposing) return;
        var id = Remember($"command {arguments[0]}");
        var handles = new IntPtr[arguments.Length + 1];
        try
        {
            for (var i = 0; i < arguments.Length; i++) handles[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            fixed (IntPtr* args = handles)
            {
                Sent(id, LibMpv.mpv_command_async(_handle, id, args));
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

    /// <summary>Queues a property change and returns at once.</summary>
    public void PostProperty(string name, string value)
    {
        if (_disposing) return;
        var id = Remember($"{name}={value}");
        var text = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            Sent(id, LibMpv.mpv_set_property_async(_handle, id, name, MpvFormat.String, &text));
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>Queues a yes/no property change and returns at once.</summary>
    public void PostFlag(string name, bool value)
    {
        if (_disposing) return;
        var flag = value ? 1 : 0;
        var id = Remember($"{name}={value}");
        Sent(id, LibMpv.mpv_set_property_async(_handle, id, name, MpvFormat.Flag, &flag));
    }

    /// <summary>Queues a numeric property change and returns at once.</summary>
    public void PostNumber(string name, double value)
    {
        if (_disposing) return;
        var id = Remember(string.Create(CultureInfo.InvariantCulture, $"{name}={value}"));
        Sent(id, LibMpv.mpv_set_property_async(_handle, id, name, MpvFormat.Double, &value));
    }

    /// <summary>Reads a property as text. Waits for mpv's core: never on the UI thread.</summary>
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

    /// <summary>Reads a numeric property. Waits for mpv's core: never on the UI thread.</summary>
    public double? GetNumber(string name)
    {
        double value;
        return LibMpv.mpv_get_property(_handle, name, MpvFormat.Double, &value) >= 0 ? value : null;
    }

    /// <summary>Destroys the player. Waits for mpv to wind down: never on the UI thread.</summary>
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

    private ulong Remember(string what)
    {
        var id = (ulong)Interlocked.Increment(ref _nextRequest);
        _requests[id] = what;
        return id;
    }

    private void Sent(ulong id, int result)
    {
        if (result >= 0) return;
        _requests.TryRemove(id, out var what);
        Log.Warn($"mpv could not queue {what}: {LibMpv.Describe(result)}");
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
                    case MpvEventId.CommandReply or MpvEventId.SetPropertyReply:
                        if (_requests.TryRemove(e->ReplyUserdata, out var what) && e->Error < 0)
                        {
                            Log.Warn($"mpv refused {what}: {LibMpv.Describe(e->Error)}");
                        }

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
