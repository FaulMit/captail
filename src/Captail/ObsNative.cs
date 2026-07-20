using System.Runtime.InteropServices;

namespace Captail;

internal static class ObsNative
{
    internal const string Library = "obs.dll";

    internal enum VideoFormat { None, I420, Nv12 }
    internal enum VideoColorSpace { Default, Cs601, Cs709, Srgb }
    internal enum VideoRange { Default, Partial, Full }
    internal enum ScaleType { Disable, Point, Bicubic, Bilinear, Lanczos, Area }
    internal enum SpeakerLayout { Unknown, Mono, Stereo }
    internal enum BoundsType { None, Stretch, ScaleInner }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoInfo
    {
        internal nint GraphicsModule;
        internal uint FpsNum;
        internal uint FpsDen;
        internal uint BaseWidth;
        internal uint BaseHeight;
        internal uint OutputWidth;
        internal uint OutputHeight;
        internal VideoFormat OutputFormat;
        internal uint Adapter;
        [MarshalAs(UnmanagedType.I1)] internal bool GpuConversion;
        internal VideoColorSpace ColorSpace;
        internal VideoRange Range;
        internal ScaleType ScaleType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioInfo
    {
        internal uint SamplesPerSecond;
        internal SpeakerLayout Speakers;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Vec2
    {
        internal float X;
        internal float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CallData
    {
        internal nint Stack;
        internal nuint Size;
        internal nuint Capacity;
        [MarshalAs(UnmanagedType.I1)] internal bool Fixed;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SignalCallback(nint data, nint callData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool AdapterCallback(
        nint data,
        nint name,
        uint id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_startup(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string locale,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string moduleConfigPath,
        nint profilerStore);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_shutdown();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_initialized();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_get_version_string();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_add_data_path(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_add_module_path(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string bin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_load_all_modules();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_post_load_modules();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_enum_encoder_types(nuint index, out nint id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_enter_graphics();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_leave_graphics();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gs_enum_adapters(AdapterCallback callback, nint data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int obs_reset_video(ref VideoInfo videoInfo);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int gs_create(
        out nint graphics,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string module,
        uint adapter);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gs_destroy(nint graphics);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gs_enter_context(nint graphics);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gs_leave_context();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint gs_effect_create_from_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        out nint errorString);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gs_effect_destroy(nint effect);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bfree(nint pointer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_reset_audio(ref AudioInfo audioInfo);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_get_video();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_get_audio();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint obs_get_total_frames();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_data_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_data_release(nint data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_data_set_string(
        nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_data_set_int(
        nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        long value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_data_set_bool(
        nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_source_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nint settings,
        nint hotkeyData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_source_release(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_source_set_audio_mixers(nint source, uint mixers);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_source_set_volume(nint source, float volume);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float obs_source_get_volume(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint obs_source_get_width(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint obs_source_get_height(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_source_get_proc_handler(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_scene_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_scene_release(nint scene);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_scene_get_source(nint scene);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_scene_add(nint scene, nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_sceneitem_set_alignment(nint item, uint alignment);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_sceneitem_set_bounds_alignment(nint item, uint alignment);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_sceneitem_set_bounds_type(nint item, BoundsType type);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_sceneitem_set_bounds(nint item, ref Vec2 bounds);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_sceneitem_set_pos(nint item, ref Vec2 position);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_sceneitem_set_scale_filter(nint item, ScaleType filter);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_set_output_source(uint channel, nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_video_encoder_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nint settings,
        nint hotkeyData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_audio_encoder_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nint settings,
        nuint mixerIndex,
        nint hotkeyData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_encoder_release(nint encoder);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_encoder_set_video(nint encoder, nint video);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_encoder_set_audio(nint encoder, nint audio);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_encoder_active(nint encoder);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_output_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nint settings,
        nint hotkeyData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_output_release(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_output_start(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_output_stop(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_output_force_stop(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_output_active(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int obs_output_get_total_frames(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong obs_output_get_total_bytes(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_output_get_last_error(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_output_set_video_encoder(nint output, nint encoder);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_output_set_audio_encoder(nint output, nint encoder, nuint index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_output_get_proc_handler(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_output_get_signal_handler(nint output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool proc_handler_call(
        nint handler,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nint callData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool proc_handler_call(
        nint handler,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref CallData callData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void signal_handler_connect(
        nint handler,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
        SignalCallback callback,
        nint data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void signal_handler_disconnect(
        nint handler,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
        SignalCallback callback,
        nint data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool calldata_get_string(
        ref CallData callData,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out nint value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool calldata_get_data(
        ref CallData callData,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out byte value,
        nuint size);
}
