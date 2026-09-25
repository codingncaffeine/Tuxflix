using System.Reflection;
using System.Runtime.InteropServices;

namespace Tuxflix.Player.Native;

// The parts of libmpv's client and render API Tuxflix uses, from <mpv/client.h>, <mpv/render.h>
// and <mpv/render_gl.h> (client API 2.x). The library is the distribution's own libmpv.so.2,
// loaded when first used; nothing is bundled.

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    Tick = 14,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

internal enum MpvRenderParam
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
    Depth = 5,
    AdvancedControl = 10,
    NextFrameInfo = 11,
    BlockForTargetTime = 12,
    SkipRendering = 13,
    SoftwareSize = 17,
    SoftwareFormat = 18,
    SoftwareStride = 19,
    SoftwarePointer = 20,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public int Reason;
    public int Error;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParamEntry
{
    public MpvRenderParam Type;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvOpenGlInitParams
{
    public delegate* unmanaged<IntPtr, IntPtr, IntPtr> GetProcAddress;
    public IntPtr GetProcAddressContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlFbo
{
    public int Fbo;
    public int Width;
    public int Height;
    public int InternalFormat;
}

internal static unsafe partial class LibMpv
{
    private const string Library = "mpv";

    static LibMpv()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly, Resolve);
    }

    /// <summary>Whether the system's libmpv can be loaded at all.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return mpv_client_api_version() >= (2UL << 16);
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library) return IntPtr.Zero;
        foreach (var candidate in new[] { "libmpv.so.2", "libmpv.so" })
        {
            if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
        }

        return IntPtr.Zero;
    }

    [LibraryImport(Library)]
    public static partial ulong mpv_client_api_version();

    [LibraryImport(Library)]
    public static partial IntPtr mpv_create();

    [LibraryImport(Library)]
    public static partial int mpv_initialize(IntPtr handle);

    [LibraryImport(Library)]
    public static partial void mpv_terminate_destroy(IntPtr handle);

    [LibraryImport(Library)]
    public static partial IntPtr mpv_error_string(int error);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_option_string(IntPtr handle, string name, string data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property_string(IntPtr handle, string name, string data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property(IntPtr handle, string name, MpvFormat format, void* data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_get_property(IntPtr handle, string name, MpvFormat format, void* data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr mpv_get_property_string(IntPtr handle, string name);

    /// <summary>Queues a property change and returns at once; mpv copies the value.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property_async(IntPtr handle, ulong replyUserdata, string name, MpvFormat format, void* data);

    [LibraryImport(Library)]
    public static partial void mpv_free(IntPtr data);

    [LibraryImport(Library)]
    public static partial int mpv_command(IntPtr handle, IntPtr* args);

    [LibraryImport(Library)]
    public static partial int mpv_command_async(IntPtr handle, ulong replyUserdata, IntPtr* args);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_observe_property(IntPtr handle, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_request_log_messages(IntPtr handle, string minimumLevel);

    [LibraryImport(Library)]
    public static partial MpvEvent* mpv_wait_event(IntPtr handle, double timeout);

    [LibraryImport(Library)]
    public static partial void mpv_wakeup(IntPtr handle);

    [LibraryImport(Library)]
    public static partial int mpv_render_context_create(IntPtr* context, IntPtr handle, MpvRenderParamEntry* parameters);

    [LibraryImport(Library)]
    public static partial int mpv_render_context_render(IntPtr context, MpvRenderParamEntry* parameters);

    [LibraryImport(Library)]
    public static partial void mpv_render_context_set_update_callback(IntPtr context, delegate* unmanaged<IntPtr, void> callback, IntPtr callbackContext);

    [LibraryImport(Library)]
    public static partial ulong mpv_render_context_update(IntPtr context);

    [LibraryImport(Library)]
    public static partial void mpv_render_context_report_swap(IntPtr context);

    [LibraryImport(Library)]
    public static partial void mpv_render_context_free(IntPtr context);

    public static string Describe(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"mpv error {error}";
}
