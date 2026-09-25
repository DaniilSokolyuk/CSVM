using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CSVM.Bindings;
using CSVM.Utils;

namespace CSVM.Sticks;

/// <summary>One profile file's name and text, the form the shipped profiles reach the store in.
/// </summary>
public readonly record struct StickProfileText(string FileName, string Text);

/// <summary>
/// The stick profile files on disk. The read-only shipped set is handed in as texts, since an
/// exported build keeps it inside the pck. The user's own directory is the only place a save
/// writes, so a saved shipped profile lands as a user copy that overrides it. Rows use the keymap
/// file's control tokens, versioned and written atomically like <see cref="BindingStore"/>; the
/// format is in <c>docs/org/input.md</c>.
/// ⚠ Skip a file this reader cannot use, never guess at it. A misread model or companion would
/// make it active on the wrong hardware.
/// </summary>
public sealed class StickProfileStore
{
    /// <summary>The schema version written into every file: the header fields and the row shape.
    /// The tokens inside a row follow <see cref="BindingStore.Version"/>. The reader checks no
    /// version, on the keymap file's rule that an unreadable row costs that row alone.</summary>
    public const int Version = 1;

    private const string Extension = ".json";

    // The relaxed encoder, for the keymap file's reason. The default escapes an axis token's '+',
    // and a player reads and corrects this file by hand.
    private static readonly JsonWriterOptions WriterOptions =
        new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions ReaderOptions =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly Func<IEnumerable<StickProfileText>> _shipped;

    /// <summary>A store over the shipped texts and <paramref name="userDirectory"/>, which must be
    /// absolute, as <see cref="BindingStore"/>'s must.</summary>
    public StickProfileStore(Func<IEnumerable<StickProfileText>> shipped, string userDirectory)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        if (!Path.IsPathRooted(userDirectory))
        {
            throw new ArgumentException($"stick profile store needs an absolute directory, got '{userDirectory}'");
        }

        _shipped = shipped;
        UserDirectory = userDirectory;
    }

    /// <summary>The directory user profiles are read from and every save writes to.</summary>
    public string UserDirectory { get; }

    /// <summary>The file a user save writes a profile to when it has no user file yet:
    /// <c>231D-0200.json</c>, or with companions <c>231D-0200+231D-0201.json</c>. Two layouts of one
    /// model thus sit side by side. The name carries no meaning on read.</summary>
    public static string FileNameFor(StickProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var name = new StringBuilder(NamePart(profile.Model));
        foreach (var companion in profile.Companions)
        {
            name.Append('+').Append(NamePart(companion));
        }

        return name.Append(Extension).ToString();
    }

    /// <summary>The canonical JSON text of a profile: the header, then per context only the bound
    /// actions, each row as bare control tokens. A full axis is written once, under its pair's
    /// positive row. Rows this build could not read go back verbatim unless the action is bound now.
    /// </summary>
    public static string Serialize(StickProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteString("model", profile.Model.ToString());
            w.WriteString("name", profile.Name);
            w.WriteStartArray("companions");
            foreach (var companion in profile.Companions)
            {
                w.WriteStringValue(companion.ToString());
            }

            w.WriteEndArray();
            w.WriteBoolean("ignore", profile.Ignore);
            w.WriteStartObject("contexts");
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var context in Enum.GetValues<InputContext>())
            {
                string contextName = NameOf(context);
                written.Add(contextName);
                w.WriteStartObject(contextName);
                WriteContext(w, profile, context, contextName);
                w.WriteEndObject();
            }

            foreach (var (contextName, _, _) in profile.Unread)
            {
                if (written.Add(contextName))
                {
                    w.WriteStartObject(contextName);
                    WriteUnread(w, profile, contextName, null);
                    w.WriteEndObject();
                }
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The profile a JSON text describes. Null, with the reason in <paramref name="error"/>,
    /// for no JSON object or an unreadable model or companion. An unreadable row binds nothing and
    /// is kept verbatim in <see cref="StickProfile.Unread"/>; the rest of the file still loads.
    /// </summary>
    public static StickProfile? Deserialize(string json, out string error)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, ReaderOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not a JSON object";
                return null;
            }

            if (!root.TryGetProperty("model", out var modelText) || modelText.ValueKind != JsonValueKind.String
                || !StickModel.TryParse(modelText.GetString(), out var model))
            {
                error = "no readable \"model\" (expected \"VVVV/PPPP\" in hex)";
                return null;
            }

            if (!TryCompanions(root, out var companions, out error))
            {
                return null;
            }

            string? name = root.TryGetProperty("name", out var nameText) && nameText.ValueKind == JsonValueKind.String
                ? nameText.GetString()
                : null;
            bool ignore = root.TryGetProperty("ignore", out var flag) && flag.ValueKind == JsonValueKind.True;
            var profile = new StickProfile(model, companions, name, ignore);
            if (root.TryGetProperty("contexts", out var contexts) && contexts.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in contexts.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Object)
                    {
                        ReadContext(profile, entry.Name, entry.Value);
                    }
                }
            }

            error = string.Empty;
            return profile;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Every <c>*.json</c> file directly in <paramref name="directory"/>, in ordinal name
    /// order; empty when the directory does not exist. A file that cannot be read is logged and
    /// left out.</summary>
    public static IReadOnlyList<StickProfileText> ReadDirectory(string directory)
    {
        var texts = new List<StickProfileText>();
        if (!Directory.Exists(directory))
        {
            return texts;
        }

        var paths = Directory.GetFiles(directory, "*" + Extension);
        Array.Sort(paths, StringComparer.Ordinal);
        foreach (var path in paths)
        {
            try
            {
                texts.Add(new StickProfileText(Path.GetFileName(path), File.ReadAllText(path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("core", $"stick profile {path}: skipped, {e.Message}");
            }
        }

        return texts;
    }

    /// <summary>Every usable profile file, shipped first, then the user's, each set in the order its
    /// source lists it. Unusable files are logged and left out.</summary>
    public IReadOnlyList<StickProfileFile> LoadAll()
    {
        var files = new List<StickProfileFile>();
        Collect(files, StickProfileSource.Shipped, _shipped());
        Collect(files, StickProfileSource.User, ReadDirectory(UserDirectory));
        return files;
    }

    /// <summary>Writes <paramref name="profile"/> into the user directory atomically and returns the
    /// file it now is. <paramref name="from"/> is the file it was loaded from. A user file for the
    /// same layout is rewritten in place; anything else goes under <see cref="FileNameFor"/>.
    /// Nothing is ever written to the shipped set.</summary>
    public StickProfileFile Save(StickProfile profile, StickProfileFile? from = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string name = from is { Source: StickProfileSource.User } && from.Profile.SameLayout(profile)
            ? from.FileName
            : FileNameFor(profile);
        Directory.CreateDirectory(UserDirectory);
        var path = Path.Combine(UserDirectory, Path.GetFileName(name));
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(profile), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        Log.Info("core", $"stick profile saved: {profile.Model} -> {path}");
        return new StickProfileFile(StickProfileSource.User, Path.GetFileName(name), profile);
    }

    private static void Collect(List<StickProfileFile> files, StickProfileSource source, IEnumerable<StickProfileText> texts)
    {
        foreach (var text in texts)
        {
            if (Deserialize(text.Text, out string error) is { } profile)
            {
                files.Add(new StickProfileFile(source, text.FileName, profile));
            }
            else
            {
                Log.Warn("core", $"stick profile {source.ToString().ToLowerInvariant()} {text.FileName}: skipped, {error}");
            }
        }
    }

    private static bool TryCompanions(JsonElement root, out List<StickModel> companions, out string error)
    {
        companions = new List<StickModel>();
        error = string.Empty;
        if (!root.TryGetProperty("companions", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            error = "\"companions\" is not an array";
            return false;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !StickModel.TryParse(item.GetString(), out var companion))
            {
                error = $"unreadable companion {item.GetRawText()}";
                return false;
            }

            companions.Add(companion);
        }

        return true;
    }

    // Readable rows go in after every row is read, full axes last, as in BindingStore's reader. A
    // full axis lands on both rows of its pair, and adding it last keeps each row's own order.
    private static void ReadContext(StickProfile profile, string contextName, JsonElement element)
    {
        bool known = TryContext(contextName, out var context);
        var rows = new List<(InputAction Action, List<Binding> Bindings)>();
        foreach (var entry in element.EnumerateObject())
        {
            if (known && TryAction(entry.Name, context, out var action) && entry.Value.ValueKind == JsonValueKind.Array
                && TryRow(entry.Value, action, profile.Model, out var bindings))
            {
                rows.Add((action, bindings));
            }
            else
            {
                profile.Unread.Add((contextName, entry.Name, entry.Value.GetRawText()));
            }
        }

        if (!known)
        {
            return;
        }

        var map = profile.Map(context);
        foreach (bool fullAxes in new[] { false, true })
        {
            foreach (var (action, bindings) in rows)
            {
                foreach (var binding in bindings)
                {
                    if ((binding.Control.Kind == ControlKind.FullAxis) == fullAxes)
                    {
                        map.Add(action, binding);
                    }
                }
            }
        }
    }

    // The whole row or none of it, as in the keymap file. A token is a bare control, or a keymap
    // token naming this file's model in any case. Both land on the canonical identity, since
    // DeviceId equality is ordinal and a lower-case id would be another device.
    private static bool TryRow(JsonElement array, InputAction action, StickModel model, out List<Binding> bindings)
    {
        bindings = new List<Binding>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string token = item.GetString()!;
            var decoded = token.Contains('/')
                ? BindingStore.Decode(token)
                : BindingStore.Decode($"pad:{model.Device.Id}/{token}");
            if (decoded is not { } binding || !StickModel.TryFromDevice(binding.Device, out var named) || named != model
                || binding.Control.Kind is not (ControlKind.Button or ControlKind.Axis or ControlKind.FullAxis or ControlKind.Hat)
                || (binding.Control.Kind == ControlKind.FullAxis && !AxisPairs.TakesFullAxis(action)))
            {
                return false;
            }

            bindings.Add(new Binding(model.Device, binding.Control));
        }

        return true;
    }

    private static void WriteContext(Utf8JsonWriter w, StickProfile profile, InputContext context, string contextName)
    {
        var map = profile.Map(context);
        foreach (var action in DefaultBindings.ActionsIn(context))
        {
            bool started = false;
            foreach (var binding in BindingStore.StoredRow(map, action))
            {
                if (!started)
                {
                    w.WriteStartArray(action.ToString());
                    started = true;
                }

                w.WriteStringValue(ControlToken(profile.Model, binding.Control));
            }

            if (started)
            {
                w.WriteEndArray();
            }
        }

        WriteUnread(w, profile, contextName, map);
    }

    private static void WriteUnread(Utf8JsonWriter w, StickProfile profile, string contextName, ActionMap? map)
    {
        foreach (var (rowContext, actionName, json) in profile.Unread)
        {
            if (!string.Equals(rowContext, contextName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (map is not null && TryContext(contextName, out var context)
                && TryAction(actionName, context, out var action) && map.Bindings(action).Count > 0)
            {
                continue;
            }

            w.WritePropertyName(actionName);
            w.WriteRawValue(json, skipInputValidation: true);
        }
    }

    // A stick's buttons and half axes are written by number: the engine's gamepad names (A,
    // LeftShoulder, LeftX) mean nothing on a stick. The reader accepts either form.
    private static string ControlToken(StickModel model, BindingControl control)
    {
        switch (control.Kind)
        {
            case ControlKind.Button:
                return "button:#" + control.Index.ToString(CultureInfo.InvariantCulture);
            case ControlKind.Axis:
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "axis:#{0}{1}@{2}",
                    control.Index,
                    control.Sign < 0 ? "-" : "+",
                    control.Deadzone);
            default:
                string token = BindingStore.Encode(new Binding(model.Device, control));
                return token[(token.LastIndexOf('/') + 1)..];
        }
    }

    private static bool TryContext(string text, out InputContext context)
    {
        foreach (var candidate in Enum.GetValues<InputContext>())
        {
            if (string.Equals(text, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                context = candidate;
                return true;
            }
        }

        context = default;
        return false;
    }

    // By name only: Enum.TryParse would also take "3" or "PitchUp, PitchDown" as some action.
    private static bool TryAction(string text, InputContext context, out InputAction action)
    {
        if (Enum.TryParse(text, ignoreCase: true, out action)
            && string.Equals(action.ToString(), text, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultBindings.ContextOf(action) == context;
        }

        return false;
    }

    private static string NamePart(StickModel model) =>
        string.Create(CultureInfo.InvariantCulture, $"{model.Vendor:X4}-{model.Product:X4}");

    private static string NameOf(InputContext context) => context.ToString().ToLowerInvariant();
}
