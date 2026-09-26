using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CSVM.Sticks;

/// <summary>
/// The live <see cref="IStickNative"/>: <c>SDL2.dll</c> loaded by absolute path, with only its
/// joystick subsystem started, reduced to the DirectInput backend. Godot's own SDL3 shares the
/// process and reads the pads. This one must not touch a device or registration SDL3 relies on,
/// which the hints in <see cref="Load"/> ensure. The load order and why nothing goes through
/// <c>PATH</c> are in <c>docs/tooling.md</c>, "SDL2 for flight sticks".
/// </summary>
public sealed class Sdl2Sticks : IStickNative
{
    private const uint InitJoystick = 0x200;
    private const uint JoyDeviceAdded = 0x605;
    private const uint JoyDeviceRemoved = 0x606;
    private const int GetEvent = 2;
    private const int Ignore = 0;

    // The per-control event types. State is polled, so nothing reads them from SDL's queue.
    // An undrained queue grows with every axis twitch, so they are switched off at the source.
    private static readonly uint[] QuietEvents = { 0x600, 0x601, 0x602, 0x603, 0x604, 0x607 };

    private readonly Api _api;
    private readonly Dictionary<int, IntPtr> _open = new();
    private readonly SdlEvent[] _events = new SdlEvent[32];
    private bool _disposed;

    private Sdl2Sticks(Api api, string version)
    {
        _api = api;
        Version = version;
    }

    public string Version { get; }

    public string LastError => Text(_api.GetError());

    /// <summary>Where <c>SDL2.dll</c> is looked for, in order, duplicates dropped. First beside the
    /// executable, then the editor-hosted repo root's <c>tools/sdl2</c> (null in an exported build).
    /// Then the data root's, and the one two folders above the executable. Pure, so testable.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string exeDir, string? repoRoot, string? dataRoot)
    {
        var paths = new List<string>(4);
        void Add(string? root, params string[] parts)
        {
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var segments = new List<string> { root };
            segments.AddRange(parts);
            string path = Path.GetFullPath(Path.Combine(segments.ToArray()));
            if (!paths.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(path);
            }
        }

        Add(exeDir, "SDL2.dll");
        Add(repoRoot, "tools", "sdl2", "SDL2.dll");
        Add(dataRoot, "tools", "sdl2", "SDL2.dll");
        Add(exeDir, "..", "..", "sdl2", "SDL2.dll");
        return paths;
    }

    /// <summary>Loads the first candidate that exists and starts the joystick subsystem. Otherwise
    /// returns null, <paramref name="outcome"/> naming the cause: no file, a failed load, a missing
    /// export or a failed init. Never throws, since a missing stick library must not stop a launch.</summary>
    public static Sdl2Sticks? Load(IReadOnlyList<string> candidates, out string outcome)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        string? path = null;
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }
        }

        if (path is null)
        {
            outcome = $"no SDL2.dll (tried {string.Join(", ", candidates)})";
            return null;
        }

        if (!NativeLibrary.TryLoad(path, out IntPtr library))
        {
            outcome = $"SDL2.dll failed to load from {path} (tried {string.Join(", ", candidates)})";
            return null;
        }

        Api api;
        try
        {
            api = new Api(library);
        }
        catch (EntryPointNotFoundException e)
        {
            outcome = $"SDL2.dll at {path} lacks {e.Message}, not the pinned build";
            return null;
        }

        api.GetVersion(out SdlVersion v);
        string version = string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{v.Patch}");

        // ⚠ Do not drop any of these. SDL3 reads the pads here: HIDAPI would handshake with them,
        // RAWINPUT take SDL3's process-wide registration, XInput and WGI list a pad twice.
        // The sticks enumerate through DirectInput alone.
        api.SetHint("SDL_NO_SIGNAL_HANDLERS", "1");
        api.SetHint("SDL_JOYSTICK_HIDAPI", "0");
        api.SetHint("SDL_JOYSTICK_RAWINPUT", "0");
        api.SetHint("SDL_JOYSTICK_WGI", "0");
        api.SetHint("SDL_XINPUT_ENABLED", "0");

        // Without its video subsystem SDL2 has no window, so it delivers events unfocused anyway.
        // Set so a later build cannot start dropping hot-plug while the game is in the background.
        api.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        if (api.InitSubSystem(InitJoystick) != 0)
        {
            outcome = $"SDL {version} from {path}: joystick init failed: {Text(api.GetError())}";
            return null;
        }

        foreach (uint type in QuietEvents)
        {
            api.EventState(type, Ignore);
        }

        outcome = $"SDL {version} from {path}";
        return new Sdl2Sticks(api, version);
    }

    public bool Pump()
    {
        if (_disposed)
        {
            return false;
        }

        _api.JoystickUpdate();
        bool plugged = false;
        int got;
        do
        {
            got = _api.PeepEvents(_events, _events.Length, GetEvent, 0, uint.MaxValue);
            for (int i = 0; i < got; i++)
            {
                plugged |= _events[i].Type is JoyDeviceAdded or JoyDeviceRemoved;
            }
        }
        while (got == _events.Length);

        return plugged;
    }

    public IReadOnlyList<StickListing> List()
    {
        int count = _disposed ? 0 : Math.Max(0, _api.NumJoysticks());
        var listed = new List<StickListing>(count);
        for (int i = 0; i < count; i++)
        {
            int instance = _api.GetDeviceInstanceId(i);
            if (instance < 0)
            {
                continue;
            }

            var model = new StickModel(_api.GetDeviceVendor(i), _api.GetDeviceProduct(i));
            listed.Add(new StickListing(instance, Text(_api.NameForIndex(i)), model, GuidText(_api.GetDeviceGuid(i))));
        }

        return listed;
    }

    public Stick? Open(StickListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        if (_disposed)
        {
            return null;
        }

        // SDL opens by list position, which moves on every plug, so the position is found afresh.
        for (int i = 0; i < _api.NumJoysticks(); i++)
        {
            if (_api.GetDeviceInstanceId(i) != listing.Instance)
            {
                continue;
            }

            IntPtr joystick = _api.JoystickOpen(i);
            if (joystick == IntPtr.Zero)
            {
                return null;
            }

            _open[listing.Instance] = joystick;
            return new Stick(listing.Instance, listing.Name, listing.Model, listing.Guid,
                _api.NumAxes(joystick), _api.NumButtons(joystick), _api.NumHats(joystick));
        }

        return null;
    }

    public void Close(int instance)
    {
        if (_open.Remove(instance, out IntPtr joystick) && !_disposed)
        {
            _api.JoystickClose(joystick);
        }
    }

    public short Axis(int instance, int axis) =>
        _open.TryGetValue(instance, out IntPtr j) ? _api.GetAxis(j, axis) : (short)0;

    public bool Button(int instance, int button) =>
        _open.TryGetValue(instance, out IntPtr j) && _api.GetButton(j, button) != 0;

    public byte Hat(int instance, int hat) =>
        _open.TryGetValue(instance, out IntPtr j) ? _api.GetHat(j, hat) : (byte)0;

    /// <summary>Closes every device and shuts SDL down. The library stays mapped: SDL's helper
    /// threads may still be unwinding, and unmapping under them would crash the process.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (IntPtr joystick in _open.Values)
        {
            _api.JoystickClose(joystick);
        }

        _open.Clear();
        _api.QuitSubSystem(InitJoystick);
        _api.Quit();
        _disposed = true;
    }

    private static string Text(IntPtr utf8) => Marshal.PtrToStringUTF8(utf8) ?? string.Empty;

    // The byte order SDL_JoystickGetGUIDString prints: the struct's bytes as they sit in memory.
    private static string GuidText(SdlGuid guid)
    {
        var text = new StringBuilder(32);
        foreach (ulong half in new[] { guid.Low, guid.High })
        {
            foreach (byte b in BitConverter.GetBytes(half))
            {
                text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlVersion
    {
        public byte Major;
        public byte Minor;
        public byte Patch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlGuid
    {
        public ulong Low;
        public ulong High;
    }

    // SDL_Event is a 56-byte union whose first field is always the type; nothing else is read.
    [StructLayout(LayoutKind.Sequential, Size = 56)]
    private struct SdlEvent
    {
        public uint Type;
    }

    // The exports this bridge calls, bound once by name from the loaded handle. A missing export
    // throws EntryPointNotFoundException, which Load turns into its one log line.
    private sealed class Api
    {
        public readonly GetVersionFn GetVersion;
        public readonly SetHintFn SetHint;
        public readonly UIntToIntFn InitSubSystem;
        public readonly UIntFn QuitSubSystem;
        public readonly VoidFn Quit;
        public readonly PtrFn GetError;
        public readonly IntFn NumJoysticks;
        public readonly IndexToPtrFn NameForIndex;
        public readonly IndexToGuidFn GetDeviceGuid;
        public readonly IndexToUShortFn GetDeviceVendor;
        public readonly IndexToUShortFn GetDeviceProduct;
        public readonly IndexToIntFn GetDeviceInstanceId;
        public readonly IndexToPtrFn JoystickOpen;
        public readonly PtrToVoidFn JoystickClose;
        public readonly PtrToIntFn NumAxes;
        public readonly PtrToIntFn NumButtons;
        public readonly PtrToIntFn NumHats;
        public readonly GetAxisFn GetAxis;
        public readonly GetByteFn GetButton;
        public readonly GetByteFn GetHat;
        public readonly VoidFn JoystickUpdate;
        public readonly PeepEventsFn PeepEvents;
        public readonly EventStateFn EventState;

        public Api(IntPtr library)
        {
            GetVersion = Bind<GetVersionFn>(library, "SDL_GetVersion");
            SetHint = Bind<SetHintFn>(library, "SDL_SetHint");
            InitSubSystem = Bind<UIntToIntFn>(library, "SDL_InitSubSystem");
            QuitSubSystem = Bind<UIntFn>(library, "SDL_QuitSubSystem");
            Quit = Bind<VoidFn>(library, "SDL_Quit");
            GetError = Bind<PtrFn>(library, "SDL_GetError");
            NumJoysticks = Bind<IntFn>(library, "SDL_NumJoysticks");
            NameForIndex = Bind<IndexToPtrFn>(library, "SDL_JoystickNameForIndex");
            GetDeviceGuid = Bind<IndexToGuidFn>(library, "SDL_JoystickGetDeviceGUID");
            GetDeviceVendor = Bind<IndexToUShortFn>(library, "SDL_JoystickGetDeviceVendor");
            GetDeviceProduct = Bind<IndexToUShortFn>(library, "SDL_JoystickGetDeviceProduct");
            GetDeviceInstanceId = Bind<IndexToIntFn>(library, "SDL_JoystickGetDeviceInstanceID");
            JoystickOpen = Bind<IndexToPtrFn>(library, "SDL_JoystickOpen");
            JoystickClose = Bind<PtrToVoidFn>(library, "SDL_JoystickClose");
            NumAxes = Bind<PtrToIntFn>(library, "SDL_JoystickNumAxes");
            NumButtons = Bind<PtrToIntFn>(library, "SDL_JoystickNumButtons");
            NumHats = Bind<PtrToIntFn>(library, "SDL_JoystickNumHats");
            GetAxis = Bind<GetAxisFn>(library, "SDL_JoystickGetAxis");
            GetButton = Bind<GetByteFn>(library, "SDL_JoystickGetButton");
            GetHat = Bind<GetByteFn>(library, "SDL_JoystickGetHat");
            JoystickUpdate = Bind<VoidFn>(library, "SDL_JoystickUpdate");
            PeepEvents = Bind<PeepEventsFn>(library, "SDL_PeepEvents");
            EventState = Bind<EventStateFn>(library, "SDL_EventState");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void GetVersionFn(out SdlVersion version);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SetHintFn(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int UIntToIntFn(uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void UIntFn(uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void VoidFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr PtrFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int IntFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr IndexToPtrFn(int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate SdlGuid IndexToGuidFn(int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate ushort IndexToUShortFn(int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int IndexToIntFn(int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PtrToVoidFn(IntPtr joystick);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int PtrToIntFn(IntPtr joystick);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short GetAxisFn(IntPtr joystick, int axis);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate byte GetByteFn(IntPtr joystick, int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int PeepEventsFn([In, Out] SdlEvent[] events, int count, int action, uint minType, uint maxType);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate byte EventStateFn(uint type, int state);

        private static T Bind<T>(IntPtr library, string name)
            where T : Delegate =>
            NativeLibrary.TryGetExport(library, name, out IntPtr address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : throw new EntryPointNotFoundException(name);
    }
}
