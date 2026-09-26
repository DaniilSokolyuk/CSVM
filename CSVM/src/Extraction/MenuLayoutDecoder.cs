// Do not leave C# 5 while ExtractRof.ps1 Add-Types this file: PowerShell 5.1 rejects newer syntax.
// That rules out interpolation, expression bodies, `?.`, file-scoped namespaces and System.Text.Json.
// The format is in docs/formats/menu-layout.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CSVM.Extraction
{
    /// <summary>Decodes <c>LAYOUT.CSV</c>, <c>SCRAPBOOK.CSV</c>, <c>RESOURCE.H</c> and the GUI
    /// scripts into one structured document, and serialises it. The decode is pure: give it
    /// <see cref="MenuLayoutInput"/> and it never reads a file.</summary>
    public static class MenuLayoutDecoder
    {
        public const int SchemaVersion = 1;

        private static readonly Regex SectionRe = new Regex(@"^\[(.+)\]$");
        private static readonly Regex GlobalMacroRe = new Regex(@"^G\d+$");
        private static readonly Regex ScreenMacroRe = new Regex(@"^V\d+$");
        private static readonly Regex MacroUseRe = new Regex(@"<([^<>]+)>");
        private static readonly Regex ResourceDefineRe = new Regex(@"^\s*#define\s+(IDS_\w+)\s+(\d+)");
        private static readonly Regex ScriptObjectRe = new Regex(@"object\s+(\w+)\s*=\s*@(\w+)@(\w+)");
        private static readonly Regex ScriptKeyRe = new Regex("(\\w+)\\.YC\\s*=\\s*\"([^\"]+)\"");
        private static readonly Regex LiteralRunRe = new Regex("(?:\"(?:[^\"\\\\]|\\\\.)*\"\\s*)+");
        private static readonly Regex LiteralRe = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
        private static readonly Regex AssetRe = new Regex(@"\w\.(png|tga|jpg|jpeg|wav|mpg|tif|bm|dll|script)$", RegexOptions.IgnoreCase);

        /// <summary>Reads the extracted rof tree and writes <c>menu_layout.json</c> into it.
        /// Returns the document so the caller can print its census.</summary>
        public static MenuLayoutDocument Run(string rofRoot, Dictionary<int, string> strings)
        {
            MenuLayoutInput input = ReadTree(rofRoot, strings);
            MenuLayoutDocument doc = Decode(input);
            string json = ToJson(doc);
            File.WriteAllText(Path.Combine(rofRoot, "menu_layout.json"), json, new UTF8Encoding(false));
            return doc;
        }

        /// <summary>Gathers the decode's inputs from an extracted <c>rof</c> tree.</summary>
        public static MenuLayoutInput ReadTree(string rofRoot, Dictionary<int, string> strings)
        {
            var input = new MenuLayoutInput();
            input.Strings = strings;
            string assets = Path.Combine(rofRoot, "ASSETS");
            string layoutPath = Path.Combine(assets, "LAYOUT.CSV");
            if (File.Exists(layoutPath))
            {
                input.LayoutCsv = File.ReadAllText(layoutPath, Encoding.ASCII);
            }

            string scrapbookPath = Path.Combine(assets, "SCRAPBOOK.CSV");
            if (File.Exists(scrapbookPath))
            {
                input.ScrapbookCsv = File.ReadAllText(scrapbookPath, Encoding.ASCII);
            }

            string resourcePath = Path.Combine(assets, "SCRIPTS", "RESOURCE.H");
            if (File.Exists(resourcePath))
            {
                input.ResourceHeader = File.ReadAllText(resourcePath, Encoding.ASCII);
            }

            string scriptDir = Path.Combine(assets, "SCRIPTS");
            if (Directory.Exists(scriptDir))
            {
                string[] files = Directory.GetFiles(scriptDir, "*.SCRIPT");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    var src = new MenuScriptSource();
                    src.Name = Path.GetFileNameWithoutExtension(f);
                    src.Text = File.ReadAllText(f, Encoding.ASCII);
                    input.Scripts.Add(src);
                }
            }

            if (Directory.Exists(assets))
            {
                foreach (string f in Directory.GetFiles(assets, "*", SearchOption.AllDirectories))
                {
                    input.ArchiveFiles.Add(f.Substring(assets.Length).Replace('\\', '/').TrimStart('/'));
                }
            }

            string patchRoot = Path.Combine(rofRoot, "_crimptch");
            if (Directory.Exists(patchRoot))
            {
                foreach (string f in Directory.GetFiles(patchRoot, "*", SearchOption.AllDirectories))
                {
                    input.PatchMembers.Add(f.Substring(patchRoot.Length).Replace('\\', '/').TrimStart('/'));
                }
            }

            input.PatchMembers.Sort(StringComparer.OrdinalIgnoreCase);
            return input;
        }

        /// <summary>Decodes the gathered inputs. Every unresolved macro, absent art file and
        /// unjoinable string id is emitted rather than guessed.</summary>
        public static MenuLayoutDocument Decode(MenuLayoutInput input)
        {
            var doc = new MenuLayoutDocument();
            doc.Schema = SchemaVersion;
            doc.Sources.Add("ASSETS/LAYOUT.CSV");
            doc.Sources.Add("ASSETS/SCRAPBOOK.CSV");
            doc.Sources.Add("ASSETS/SCRIPTS/RESOURCE.H");
            doc.Sources.Add("ASSETS/SCRIPTS/*.SCRIPT");
            doc.WidgetTypes = BuildWidgetTypes();
            doc.Patch.Root = "_crimptch";
            doc.Patch.Rule = "patch-wins: a member here replaces the base tree's member at the same archive path";
            doc.Patch.Members = input.PatchMembers;

            Dictionary<string, int> resourceIds = ParseResourceHeader(input.ResourceHeader);
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var presentByName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in input.ArchiveFiles)
            {
                present.Add(f);
                presentByName.Add(PathTail(f));
            }

            var scriptByName = new Dictionary<string, MenuScriptSource>(StringComparer.OrdinalIgnoreCase);
            foreach (MenuScriptSource s in input.Scripts)
            {
                scriptByName[s.Name] = s;
            }

            var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sections = ParseSectionedCsv(input.LayoutCsv, doc.Warnings, "LAYOUT.CSV");
            var missingArt = new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var seenArt = new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var unresolvedStrings = new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (CsvSection section in sections)
            {
                if (string.Equals(section.Name, "GLOBALVARS", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (CsvEntry e in section.Entries)
                    {
                        if (!GlobalMacroRe.IsMatch(e.Key))
                        {
                            doc.Warnings.Add("GLOBALVARS entry is not a G<n> macro: " + e.Key);
                            continue;
                        }

                        MenuMacro m = MakeMacro(e);
                        globals[m.Name] = m.Value;
                        doc.Globals.Add(m);
                    }

                    continue;
                }

                var screen = new MenuScreen();
                screen.Section = section.Name.Trim('@');
                var locals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (CsvEntry e in section.Entries)
                {
                    if (!ScreenMacroRe.IsMatch(e.Key))
                    {
                        continue;
                    }

                    MenuMacro m = MakeMacro(e);
                    locals[m.Name] = m.Value;
                    screen.Macros.Add(m);
                }

                foreach (CsvEntry e in section.Entries)
                {
                    if (GlobalMacroRe.IsMatch(e.Key) || ScreenMacroRe.IsMatch(e.Key))
                    {
                        continue;
                    }

                    MenuWidget w = BuildWidget(screen.Section, e, locals, globals, doc);
                    if (w.Type.Length == 0)
                    {
                        continue;
                    }

                    foreach (string a in w.Art)
                    {
                        seenArt[a] = true;
                        if (!presentByName.Contains(PathTail(a)))
                        {
                            missingArt[a] = true;
                        }
                    }

                    if (w.ResIdSymbol.StartsWith("IDS_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (resourceIds.ContainsKey(w.ResIdSymbol))
                        {
                            w.ResId = resourceIds[w.ResIdSymbol];
                            if (input.Strings.ContainsKey(w.ResId))
                            {
                                w.Text = input.Strings[w.ResId];
                                w.TextSource = "resource";
                            }
                            else
                            {
                                w.TextSource = "unresolved-string";
                                unresolvedStrings[w.ResIdSymbol] = true;
                            }
                        }
                        else
                        {
                            w.TextSource = "unresolved-symbol";
                            unresolvedStrings[w.ResIdSymbol] = true;
                        }
                    }

                    screen.Widgets.Add(w);
                    if (w.NavigateTo.Length > 0)
                    {
                        var edge = new MenuNavEdge();
                        edge.From = screen.Section;
                        edge.Widget = w.Key;
                        edge.To = w.NavigateTo;
                        edge.Priority = FieldValue(w, "ScriptPri");
                        edge.EndScript = FieldValue(w, "EndScript");
                        doc.Navigation.Add(edge);
                    }
                }

                if (scriptByName.ContainsKey(screen.Section))
                {
                    MenuScriptSource src = scriptByName[screen.Section];
                    screen.Script = "ASSETS/SCRIPTS/" + src.Name + ".SCRIPT";
                    ReadScript(src.Text, screen);
                }
                else
                {
                    doc.Warnings.Add("layout section has no script of the same name: " + screen.Section);
                }

                doc.Screens.Add(screen);
            }

            foreach (string a in seenArt.Keys)
            {
                if (missingArt.ContainsKey(a))
                {
                    doc.MissingArt.Add(a);
                }
            }

            foreach (string s in unresolvedStrings.Keys)
            {
                doc.UnresolvedStrings.Add(s);
            }

            ReadExternalAssets(input.Scripts, presentByName, doc);
            ReadScrapbook(input.ScrapbookCsv, doc);
            BuildCounts(doc, seenArt.Count, input.Scripts.Count);
            return doc;
        }

        /// <summary>Serialises the document. Hand-rolled because the extractor compiles this file
        /// under PowerShell 5.1, where no JSON writer is available.</summary>
        public static string ToJson(MenuLayoutDocument doc)
        {
            var w = new JsonWriter();
            w.StartObject();
            w.Number("schema", doc.Schema);
            w.StartArray("sources");
            foreach (string s in doc.Sources)
            {
                w.Value(s);
            }

            w.EndArray();
            w.StartObject("patchOverlay");
            w.String("root", doc.Patch.Root);
            w.String("rule", doc.Patch.Rule);
            w.StartArray("members");
            foreach (string m in doc.Patch.Members)
            {
                w.Value(m);
            }

            w.EndArray();
            w.EndObject();
            w.StartArray("widgetTypes");
            foreach (MenuWidgetType t in doc.WidgetTypes)
            {
                w.StartObject();
                w.String("type", t.Type);
                w.String("widget", t.Widget);
                w.String("scriptClass", t.ScriptClass);
                w.StartArray("fields");
                for (int i = 0; i < t.Fields.Count; i++)
                {
                    w.StartObject();
                    w.String("name", t.Fields[i]);
                    w.String("kind", t.Kinds[i]);
                    w.Bool("optional", t.OptionalFields.Contains(t.Fields[i]));
                    w.EndObject();
                }

                w.EndArray();
                w.EndObject();
            }

            w.EndArray();
            WriteMacros(w, "globals", doc.Globals);
            w.StartArray("screens");
            foreach (MenuScreen s in doc.Screens)
            {
                w.StartObject();
                w.String("section", s.Section);
                w.String("script", s.Script);
                WriteMacros(w, "macros", s.Macros);
                w.StartArray("widgets");
                foreach (MenuWidget widget in s.Widgets)
                {
                    WriteWidget(w, widget);
                }

                w.EndArray();
                w.StartArray("scriptWidgets");
                foreach (MenuScriptWidget sw in s.ScriptWidgets)
                {
                    w.StartObject();
                    w.String("key", sw.Key);
                    w.String("class", sw.Class);
                    w.Bool("hasLayoutRow", sw.HasLayoutRow);
                    w.EndObject();
                }

                w.EndArray();
                WriteStrings(w, "scriptRefs", s.ScriptRefs);
                WriteStrings(w, "keysWithoutLayoutRow", s.KeysWithoutLayoutRow);
                WriteStrings(w, "rowsNoScriptCreates", s.RowsNoScriptCreates);
                w.EndObject();
            }

            w.EndArray();
            w.StartArray("navigation");
            foreach (MenuNavEdge e in doc.Navigation)
            {
                w.StartObject();
                w.String("from", e.From);
                w.String("widget", e.Widget);
                w.String("to", e.To);
                w.String("priority", e.Priority);
                w.String("endScript", e.EndScript);
                w.EndObject();
            }

            w.EndArray();
            w.StartArray("externalAssets");
            foreach (MenuExternalAsset a in doc.ExternalAssets)
            {
                w.StartObject();
                w.String("path", a.Path);
                w.String("kind", a.Kind);
                w.String("script", a.Script);
                w.Bool("present", a.Present);
                w.EndObject();
            }

            w.EndArray();
            w.StartArray("scrapbook");
            foreach (MenuScrapbookEntry e in doc.Scrapbook)
            {
                w.StartObject();
                w.String("key", e.Key);
                w.Number("mission", e.Mission);
                w.Number("spread", e.Spread);
                w.Number("item", e.Item);
                WriteFields(w, e.Fields);
                w.EndObject();
            }

            w.EndArray();
            WriteStrings(w, "missingArt", doc.MissingArt);
            WriteStrings(w, "unresolvedMacros", doc.UnresolvedMacros);
            WriteStrings(w, "unresolvedStrings", doc.UnresolvedStrings);
            WriteStrings(w, "warnings", doc.Warnings);
            w.StartObject("counts");
            foreach (MenuCount c in doc.Counts)
            {
                w.Number(c.Name, c.Value);
            }

            w.EndObject();
            w.EndObject();
            return w.ToString();
        }

        /// <summary>Splits one record on commas, honouring the double quotes
        /// <c>SCRAPBOOK.CSV</c> wraps its comma-bearing rectangle field in.</summary>
        public static List<string> SplitRecord(string value)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (char c in value)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (c == ',' && !quoted)
                {
                    fields.Add(sb.ToString());
                    sb.Length = 0;
                }
                else
                {
                    sb.Append(c);
                }
            }

            fields.Add(sb.ToString());
            return fields;
        }

        private static string PathTail(string path)
        {
            int i = path.LastIndexOf('/');
            int j = path.LastIndexOf('\\');
            int k = i > j ? i : j;
            return k < 0 ? path : path.Substring(k + 1);
        }

        private static MenuMacro MakeMacro(CsvEntry e)
        {
            var m = new MenuMacro();
            int comma = e.Value.IndexOf(',');
            if (comma < 0)
            {
                m.Name = e.Value;
                return m;
            }

            m.Name = e.Value.Substring(0, comma);
            m.Value = e.Value.Substring(comma + 1);
            return m;
        }

        private static string FieldValue(MenuWidget w, string name)
        {
            foreach (MenuField f in w.Fields)
            {
                if (string.Equals(f.Name, name, StringComparison.Ordinal))
                {
                    return f.Value;
                }
            }

            return string.Empty;
        }

        private static Dictionary<string, int> ParseResourceHeader(string text)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in SplitLines(text))
            {
                Match m = ResourceDefineRe.Match(line);
                if (m.Success && !map.ContainsKey(m.Groups[1].Value))
                {
                    map[m.Groups[1].Value] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            }

            return map;
        }

        private static string[] SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static List<CsvSection> ParseSectionedCsv(string text, List<string> warnings, string what)
        {
            var sections = new List<CsvSection>();
            var current = new CsvSection();
            bool haveSection = false;
            foreach (string raw in SplitLines(text))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';')
                {
                    continue;
                }

                Match sm = SectionRe.Match(line);
                if (sm.Success)
                {
                    current = new CsvSection();
                    current.Name = sm.Groups[1].Value.Trim();
                    haveSection = true;
                    sections.Add(current);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    warnings.Add(what + ": line is neither a section, a comment nor a KEY=VALUE record, ignored: " + line);
                    continue;
                }

                if (!haveSection)
                {
                    warnings.Add(what + ": record before the first section header, ignored: " + line);
                    continue;
                }

                var entry = new CsvEntry();
                entry.Key = line.Substring(0, eq).Trim();
                entry.Value = line.Substring(eq + 1);
                current.Entries.Add(entry);
            }

            return sections;
        }

        private static string Resolve(
            string value,
            Dictionary<string, string> locals,
            Dictionary<string, string> globals,
            string where,
            MenuLayoutDocument doc)
        {
            string s = value;
            for (int guard = 0; guard < 8; guard++)
            {
                Match m = MacroUseRe.Match(s);
                if (!m.Success)
                {
                    return s;
                }

                string name = m.Groups[1].Value;
                string replacement;
                if (locals.ContainsKey(name))
                {
                    replacement = locals[name];
                }
                else if (globals.ContainsKey(name))
                {
                    replacement = globals[name];
                }
                else
                {
                    if (!doc.UnresolvedMacros.Contains(where + " <" + name + ">"))
                    {
                        doc.UnresolvedMacros.Add(where + " <" + name + ">");
                    }

                    return s;
                }

                s = s.Replace("<" + name + ">", replacement);
            }

            doc.Warnings.Add(where + ": macro substitution did not settle, left as authored: " + value);
            return value;
        }

        private static MenuWidget BuildWidget(
            string section,
            CsvEntry entry,
            Dictionary<string, string> locals,
            Dictionary<string, string> globals,
            MenuLayoutDocument doc)
        {
            List<string> raw = SplitRecord(entry.Value);
            string type = raw[0].Trim();
            var def = new MenuWidgetType();
            bool known = false;
            foreach (MenuWidgetType t in doc.WidgetTypes)
            {
                if (string.Equals(t.Type, type, StringComparison.Ordinal))
                {
                    def = t;
                    known = true;
                    break;
                }
            }

            var w = new MenuWidget();
            if (!known)
            {
                doc.Warnings.Add(section + "." + entry.Key + ": unknown widget type '" + type + "', row ignored");
                return w;
            }

            w.Key = entry.Key;
            w.Type = def.Type;
            w.Widget = def.Widget;
            int supplied = raw.Count - 1;
            for (int i = 0; i < def.Fields.Count; i++)
            {
                string name = def.Fields[i];
                bool optional = def.OptionalFields.Contains(name);
                if (i >= supplied)
                {
                    if (!optional)
                    {
                        doc.Warnings.Add(section + "." + entry.Key + ": row stops before field '" + name + "'");
                    }

                    continue;
                }

                string authored = raw[i + 1];
                if (optional && authored.Length == 0 && !HasLaterValue(raw, i + 1))
                {
                    continue;
                }

                string resolved = Resolve(authored, locals, globals, section + "." + entry.Key, doc);
                var f = new MenuField();
                f.Name = name;
                f.Value = resolved;
                f.Raw = string.Equals(resolved, authored, StringComparison.Ordinal) ? string.Empty : authored;
                w.Fields.Add(f);
                if (string.Equals(def.Kinds[i], "art", StringComparison.Ordinal) && resolved.Length > 0)
                {
                    w.Art.Add(resolved);
                }

                if (string.Equals(def.Kinds[i], "resid", StringComparison.Ordinal))
                {
                    w.ResIdSymbol = resolved;
                }

                if (string.Equals(def.Kinds[i], "script", StringComparison.Ordinal))
                {
                    w.NavigateTo = resolved;
                }
            }

            for (int i = def.Fields.Count; i < supplied; i++)
            {
                if (raw[i + 1].Length > 0)
                {
                    doc.Warnings.Add(section + "." + entry.Key + ": field past the declared order carries a value: " + raw[i + 1]);
                }
            }

            w.TextSource = ClassifyText(w.ResIdSymbol);
            w.Frames = FrameCount(w);
            return w;
        }

        private static bool HasLaterValue(List<string> raw, int from)
        {
            for (int i = from; i < raw.Count; i++)
            {
                if (raw[i].Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string ClassifyText(string symbol)
        {
            if (symbol.StartsWith("IDS_", StringComparison.OrdinalIgnoreCase))
            {
                return "resource";
            }

            if (string.Equals(symbol, "!", StringComparison.Ordinal))
            {
                return "runtime";
            }

            if (symbol.Length == 0 || string.Equals(symbol, "0", StringComparison.Ordinal))
            {
                return "none";
            }

            return "other";
        }

        private static int FrameCount(MenuWidget w)
        {
            if (string.Equals(w.Type, "P", StringComparison.Ordinal))
            {
                int frames;
                return int.TryParse(FieldValue(w, "NumFrames"), NumberStyles.Integer, CultureInfo.InvariantCulture, out frames) ? frames : 0;
            }

            if (!string.Equals(w.Type, "B", StringComparison.Ordinal))
            {
                return 0;
            }

            string style = FieldValue(w, "Style");
            return string.Equals(style, "1", StringComparison.Ordinal) || string.Equals(style, "2", StringComparison.Ordinal) ? 8 : 4;
        }

        private static void ReadScript(string text, MenuScreen screen)
        {
            var classByVar = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in ScriptObjectRe.Matches(text))
            {
                classByVar[m.Groups[1].Value] = "@" + m.Groups[2].Value + "@" + m.Groups[3].Value;
            }

            var layoutKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MenuWidget w in screen.Widgets)
            {
                layoutKeys.Add(w.Key);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ScriptKeyRe.Matches(text))
            {
                string key = m.Groups[2].Value;
                if (!seen.Add(key))
                {
                    continue;
                }

                var sw = new MenuScriptWidget();
                sw.Key = key;
                string varName = m.Groups[1].Value;
                sw.Class = classByVar.ContainsKey(varName) ? classByVar[varName] : string.Empty;
                sw.HasLayoutRow = layoutKeys.Contains(key);
                screen.ScriptWidgets.Add(sw);
                if (!sw.HasLayoutRow)
                {
                    screen.KeysWithoutLayoutRow.Add(key);
                }
            }

            foreach (MenuWidget w in screen.Widgets)
            {
                if (!seen.Contains(w.Key))
                {
                    screen.RowsNoScriptCreates.Add(w.Key);
                }
            }

            foreach (Match m in LiteralRunRe.Matches(text))
            {
                string joined = JoinLiterals(m.Value);
                if (joined.IndexOf(".script", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !screen.ScriptRefs.Contains(joined))
                {
                    screen.ScriptRefs.Add(joined);
                }
            }
        }

        private static string JoinLiterals(string run)
        {
            var sb = new StringBuilder();
            foreach (Match m in LiteralRe.Matches(run))
            {
                sb.Append(m.Groups[1].Value.Replace("\\\\", "/"));
            }

            return sb.ToString();
        }

        private static void ReadExternalAssets(
            List<MenuScriptSource> scripts,
            HashSet<string> presentByName,
            MenuLayoutDocument doc)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MenuScriptSource src in scripts)
            {
                foreach (Match m in LiteralRunRe.Matches(src.Text))
                {
                    string joined = JoinLiterals(m.Value);
                    if (joined.Length == 0 || joined.IndexOf(".script", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    bool complete = AssetRe.IsMatch(joined);
                    bool fragment = !complete &&
                        (joined.StartsWith(".", StringComparison.Ordinal) ||
                         joined.IndexOf("assets/", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!complete && !fragment)
                    {
                        continue;
                    }

                    if (!seen.Add(src.Name + "|" + joined))
                    {
                        continue;
                    }

                    var a = new MenuExternalAsset();
                    a.Path = joined;
                    a.Kind = complete ? "file" : "fragment";
                    a.Script = src.Name;
                    a.Present = complete && presentByName.Contains(PathTail(joined));
                    doc.ExternalAssets.Add(a);
                }
            }
        }

        private static void ReadScrapbook(string text, MenuLayoutDocument doc)
        {
            if (text.Length == 0)
            {
                return;
            }

            string[] names = new string[]
            {
                "Objective", "ResourceId", "ImageName", "ImageType", "X", "Y", "Alpha", "Width",
                "Height", "DrawOrder", "ZoomRect", "Zoom", "ZoomX", "ZoomY", "TitleResId", "TextResId",
            };
            foreach (CsvSection section in ParseSectionedCsv(text, doc.Warnings, "SCRAPBOOK.CSV"))
            {
                foreach (CsvEntry e in section.Entries)
                {
                    var entry = new MenuScrapbookEntry();
                    entry.Key = e.Key;
                    string[] parts = e.Key.Split('_');
                    if (parts.Length == 3)
                    {
                        entry.Mission = ParseInt(parts[0]);
                        entry.Spread = ParseInt(parts[1]);
                        entry.Item = ParseInt(parts[2]);
                    }

                    List<string> raw = SplitRecord(e.Value);
                    if (raw.Count != names.Length)
                    {
                        doc.Warnings.Add("SCRAPBOOK.CSV." + e.Key + ": " + raw.Count + " fields, expected " + names.Length);
                    }

                    for (int i = 0; i < names.Length && i < raw.Count; i++)
                    {
                        var f = new MenuField();
                        f.Name = names[i];
                        f.Value = raw[i];
                        entry.Fields.Add(f);
                    }

                    doc.Scrapbook.Add(entry);
                }
            }
        }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        private static void BuildCounts(MenuLayoutDocument doc, int artRefs, int scriptCount)
        {
            int widgets = 0;
            int macros = doc.Globals.Count;
            int scriptWidgets = 0;
            int keysWithoutRow = 0;
            var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (MenuScreen s in doc.Screens)
            {
                widgets += s.Widgets.Count;
                macros += s.Macros.Count;
                scriptWidgets += s.ScriptWidgets.Count;
                keysWithoutRow += s.KeysWithoutLayoutRow.Count;
                foreach (MenuWidget w in s.Widgets)
                {
                    byType[w.Type] = (byType.ContainsKey(w.Type) ? byType[w.Type] : 0) + 1;
                }
            }

            AddCount(doc, "screens", doc.Screens.Count);
            AddCount(doc, "scripts", scriptCount);
            AddCount(doc, "widgets", widgets);
            foreach (string t in byType.Keys)
            {
                AddCount(doc, "widgets." + t, byType[t]);
            }

            AddCount(doc, "macros", macros);
            AddCount(doc, "macrosUnresolved", doc.UnresolvedMacros.Count);
            AddCount(doc, "navigationEdges", doc.Navigation.Count);
            AddCount(doc, "artReferences", artRefs);
            AddCount(doc, "artMissing", doc.MissingArt.Count);
            AddCount(doc, "scriptWidgets", scriptWidgets);
            AddCount(doc, "scriptKeysWithoutLayoutRow", keysWithoutRow);
            AddCount(doc, "externalAssets", doc.ExternalAssets.Count);
            AddCount(doc, "scrapbookEntries", doc.Scrapbook.Count);
            AddCount(doc, "warnings", doc.Warnings.Count);
            int resolved = 0;
            var symbols = new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (MenuScreen s in doc.Screens)
            {
                foreach (MenuWidget w in s.Widgets)
                {
                    if (w.ResIdSymbol.StartsWith("IDS_", StringComparison.OrdinalIgnoreCase))
                    {
                        symbols[w.ResIdSymbol] = string.Equals(w.TextSource, "resource", StringComparison.Ordinal);
                    }
                }
            }

            foreach (string k in symbols.Keys)
            {
                if (symbols[k])
                {
                    resolved++;
                }
            }

            AddCount(doc, "stringSymbols", symbols.Count);
            AddCount(doc, "stringSymbolsResolved", resolved);
        }

        private static void AddCount(MenuLayoutDocument doc, string name, int value)
        {
            var c = new MenuCount();
            c.Name = name;
            c.Value = value;
            doc.Counts.Add(c);
        }

        private static void WriteMacros(JsonWriter w, string name, List<MenuMacro> macros)
        {
            w.StartArray(name);
            foreach (MenuMacro m in macros)
            {
                w.StartObject();
                w.String("name", m.Name);
                w.String("value", m.Value);
                w.EndObject();
            }

            w.EndArray();
        }

        private static void WriteStrings(JsonWriter w, string name, List<string> values)
        {
            w.StartArray(name);
            foreach (string v in values)
            {
                w.Value(v);
            }

            w.EndArray();
        }

        private static void WriteFields(JsonWriter w, List<MenuField> fields)
        {
            w.StartObject("fields");
            foreach (MenuField f in fields)
            {
                w.String(f.Name, f.Value);
            }

            w.EndObject();
            bool any = false;
            foreach (MenuField f in fields)
            {
                if (f.Raw.Length > 0)
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                return;
            }

            w.StartObject("authored");
            foreach (MenuField f in fields)
            {
                if (f.Raw.Length > 0)
                {
                    w.String(f.Name, f.Raw);
                }
            }

            w.EndObject();
        }

        private static void WriteWidget(JsonWriter w, MenuWidget widget)
        {
            w.StartObject();
            w.String("key", widget.Key);
            w.String("type", widget.Type);
            w.String("widget", widget.Widget);
            WriteFields(w, widget.Fields);
            WriteStrings(w, "art", widget.Art);
            if (widget.ResIdSymbol.Length > 0)
            {
                w.String("resIdSymbol", widget.ResIdSymbol);
            }

            if (widget.ResId >= 0)
            {
                w.Number("resId", widget.ResId);
            }

            if (widget.Text.Length > 0)
            {
                w.String("text", widget.Text);
            }

            w.String("textSource", widget.TextSource);
            if (widget.NavigateTo.Length > 0)
            {
                w.String("navigateTo", widget.NavigateTo);
            }

            if (widget.Frames > 0)
            {
                w.Number("frames", widget.Frames);
            }

            w.EndObject();
        }

        private static MenuWidgetType MakeType(string type, string widget, string scriptClass, string fields, string kinds, string optional)
        {
            var t = new MenuWidgetType();
            t.Type = type;
            t.Widget = widget;
            t.ScriptClass = scriptClass;
            t.Fields.AddRange(fields.Split(','));
            t.Kinds.AddRange(kinds.Split(','));
            if (optional.Length > 0)
            {
                t.OptionalFields.AddRange(optional.Split(','));
            }

            return t;
        }

        private static List<MenuWidgetType> BuildWidgetTypes()
        {
            var types = new List<MenuWidgetType>();
            types.Add(MakeType(
                "B", "button", "@ctl@BE",
                "ArtPath,X,Y,Z,ResID,ScriptToExe,ScriptPri,EndScript,Left,Top,Right,Bottom,Style,Checked,ColorDisabled,ColorActive,ColorRollover,ColorDepressed",
                "art,int,int,int,resid,script,int,bool,int,int,int,int,int,bool,color,color,color,color",
                "ColorDisabled,ColorActive,ColorRollover,ColorDepressed"));
            types.Add(MakeType(
                "P", "pane", "@ctl@ZJ",
                "ArtPath,X,Y,Z,NumFrames,IsRegion,AlphaType,Volatile",
                "art,int,int,int,int,bool,int,bool",
                string.Empty));
            types.Add(MakeType(
                "T", "text", "@ctl@PE",
                "ResID,X,Y,Z,Width,Height,Color,Justify",
                "resid,int,int,int,int,int,color,int",
                string.Empty));
            types.Add(MakeType(
                "D", "dropdown", "@ctl@PM",
                "Slider,UpArrow,DownArrow,DropUp,DropDown,X,Y,Z,Width,ItemHeight,TotalDisplayed",
                "art,art,art,art,art,int,int,int,int,int,int",
                string.Empty));
            types.Add(MakeType(
                "A", "textlist", "@ctl@SJ",
                "X,Y,Z,Width,Height,Color,Justify,ItemSpacing",
                "int,int,int,int,int,color,int,int",
                string.Empty));
            types.Add(MakeType(
                "S", "scrolltext", "@ctl@JN",
                "BorderColor,BackColor,Slider,UpArrow,DownArrow,X,Y,Z,Width,Height,ResID,Color",
                "color,color,art,art,art,int,int,int,int,int,resid,color",
                string.Empty));
            types.Add(MakeType(
                "M", "movie", "@ctl@AL",
                "ArtPath,X,Y,Z,ScaleX,ScaleY,Loops,IsRegion",
                "art,int,int,int,int,int,int,bool",
                string.Empty));
            types.Add(MakeType(
                "L", "listbox", "@ctl@EN",
                "Slider,UpArrow,DownArrow,X,Y,Z,Width,ItemHeight,TotalDisplayed",
                "art,art,art,int,int,int,int,int,int",
                string.Empty));
            types.Add(MakeType(
                "E", "editbox", "@ctl@IM",
                "FontId,X,Y,Z,Width,Height,MaxChars,TextColor,FrameColor,CursorColor",
                "text,int,int,int,int,int,int,color,color,color",
                string.Empty));
            types.Add(MakeType(
                "Z", "slider", "@ctl@DL",
                "X,Y,Z,MinValue,MaxValue,CurrentValue,RegionArt,SliderArt,Left,Top,Right,Bottom",
                "int,int,int,int,int,int,art,art,int,int,int,int",
                string.Empty));
            types.Add(MakeType(
                "W", "sound", "@ctl@SK",
                "WavFile,Channel,Volume,LoopCount,AutoStart",
                "art,int,int,int,bool",
                string.Empty));
            return types;
        }

        private sealed class CsvEntry
        {
            public string Key = string.Empty;
            public string Value = string.Empty;
        }

        private sealed class CsvSection
        {
            public string Name = string.Empty;
            public List<CsvEntry> Entries = new List<CsvEntry>();
        }

        private sealed class JsonWriter
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly List<bool> _first = new List<bool>();

            public void StartObject()
            {
                Separator();
                _sb.Append('{');
                _first.Add(true);
            }

            public void StartObject(string name)
            {
                Separator();
                Key(name);
                _sb.Append('{');
                _first.Add(true);
            }

            public void EndObject()
            {
                _sb.Append('}');
                _first.RemoveAt(_first.Count - 1);
            }

            public void StartArray(string name)
            {
                Separator();
                Key(name);
                _sb.Append('[');
                _first.Add(true);
            }

            public void EndArray()
            {
                _sb.Append(']');
                _first.RemoveAt(_first.Count - 1);
            }

            public void String(string name, string value)
            {
                Separator();
                Key(name);
                Quote(value);
            }

            public void Number(string name, int value)
            {
                Separator();
                Key(name);
                _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            }

            public void Bool(string name, bool value)
            {
                Separator();
                Key(name);
                _sb.Append(value ? "true" : "false");
            }

            public void Value(string value)
            {
                Separator();
                Quote(value);
            }

            public override string ToString()
            {
                return _sb.ToString();
            }

            private void Separator()
            {
                if (_first.Count == 0)
                {
                    return;
                }

                if (_first[_first.Count - 1])
                {
                    _first[_first.Count - 1] = false;
                }
                else
                {
                    _sb.Append(',');
                }
            }

            private void Key(string name)
            {
                Quote(name);
                _sb.Append(':');
            }

            private void Quote(string value)
            {
                _sb.Append('"');
                foreach (char c in value)
                {
                    if (c == '"' || c == '\\')
                    {
                        _sb.Append('\\').Append(c);
                    }
                    else if (c == '\n')
                    {
                        _sb.Append("\\n");
                    }
                    else if (c == '\r')
                    {
                        _sb.Append("\\r");
                    }
                    else if (c == '\t')
                    {
                        _sb.Append("\\t");
                    }
                    else if (c < 0x20 || c > 0x7E)
                    {
                        _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _sb.Append(c);
                    }
                }

                _sb.Append('"');
            }
        }
    }

    /// <summary>One <c>NAME,VALUE</c> substitution, file-wide (<c>G n</c>) or per screen
    /// (<c>V n</c>).</summary>
    public sealed class MenuMacro
    {
        public string Name = string.Empty;
        public string Value = string.Empty;
    }

    /// <summary>A named field of a widget row: the text as authored, and the text after macro
    /// substitution. <see cref="Raw"/> is empty unless substitution changed the value.</summary>
    public sealed class MenuField
    {
        public string Name = string.Empty;
        public string Value = string.Empty;
        public string Raw = string.Empty;
    }

    /// <summary>One widget row, keyed by its layout key and typed by its one-letter type
    /// code.</summary>
    public sealed class MenuWidget
    {
        public string Key = string.Empty;
        public string Type = string.Empty;
        public string Widget = string.Empty;
        public List<MenuField> Fields = new List<MenuField>();
        public List<string> Art = new List<string>();
        public string ResIdSymbol = string.Empty;
        public int ResId = -1;
        public string Text = string.Empty;
        public string TextSource = string.Empty;
        public string NavigateTo = string.Empty;
        public int Frames = 0;
    }

    /// <summary>A widget the screen's script creates, with the <c>@ctl@XX</c> class it binds.
    /// Scripts also build keys by concatenation, so a key here with no layout row is a lead, not
    /// proof of a missing row.</summary>
    public sealed class MenuScriptWidget
    {
        public string Key = string.Empty;
        public string Class = string.Empty;
        public bool HasLayoutRow = false;
    }

    /// <summary>One screen: a layout section, the script of the same name, and the mismatch
    /// between what each of the two declares.</summary>
    public sealed class MenuScreen
    {
        public string Section = string.Empty;
        public string Script = string.Empty;
        public List<MenuMacro> Macros = new List<MenuMacro>();
        public List<MenuWidget> Widgets = new List<MenuWidget>();
        public List<MenuScriptWidget> ScriptWidgets = new List<MenuScriptWidget>();
        public List<string> ScriptRefs = new List<string>();
        public List<string> KeysWithoutLayoutRow = new List<string>();
        public List<string> RowsNoScriptCreates = new List<string>();
    }

    /// <summary>A navigation edge the layout states and no script mentions: a button's
    /// <c>ScriptToExe</c> target.</summary>
    public sealed class MenuNavEdge
    {
        public string From = string.Empty;
        public string Widget = string.Empty;
        public string To = string.Empty;
        public string Priority = string.Empty;
        public string EndScript = string.Empty;
    }

    /// <summary>An asset a script names rather than the layout. <c>Kind</c> is <c>file</c> for a
    /// complete name and <c>fragment</c> for a half of a runtime-built one.</summary>
    public sealed class MenuExternalAsset
    {
        public string Path = string.Empty;
        public string Kind = string.Empty;
        public string Script = string.Empty;
        public bool Present = false;
    }

    /// <summary>One <c>SCRAPBOOK.CSV</c> row, keyed <c>mission_spread_item</c>.</summary>
    public sealed class MenuScrapbookEntry
    {
        public string Key = string.Empty;
        public int Mission = -1;
        public int Spread = -1;
        public int Item = -1;
        public List<MenuField> Fields = new List<MenuField>();
    }

    /// <summary>The declared field order of one widget type, established from the shipped rows.
    /// A reader converts <see cref="MenuField"/> values with <see cref="Kinds"/>.</summary>
    public sealed class MenuWidgetType
    {
        public string Type = string.Empty;
        public string Widget = string.Empty;
        public string ScriptClass = string.Empty;
        public List<string> Fields = new List<string>();
        public List<string> Kinds = new List<string>();
        public List<string> OptionalFields = new List<string>();
    }

    /// <summary>The patch archive's precedence, which the extracted tree does not otherwise
    /// state.</summary>
    public sealed class MenuPatchOverlay
    {
        public string Root = string.Empty;
        public string Rule = string.Empty;
        public List<string> Members = new List<string>();
    }

    /// <summary>One census number, emitted so a consumer can compare a tree against the format
    /// page without re-counting.</summary>
    public sealed class MenuCount
    {
        public string Name = string.Empty;
        public int Value = 0;
    }

    /// <summary>The decoded menu layout: the artifact runtime reads instead of the original
    /// files.</summary>
    public sealed class MenuLayoutDocument
    {
        public int Schema = 1;
        public List<string> Sources = new List<string>();
        public MenuPatchOverlay Patch = new MenuPatchOverlay();
        public List<MenuWidgetType> WidgetTypes = new List<MenuWidgetType>();
        public List<MenuMacro> Globals = new List<MenuMacro>();
        public List<MenuScreen> Screens = new List<MenuScreen>();
        public List<MenuNavEdge> Navigation = new List<MenuNavEdge>();
        public List<MenuExternalAsset> ExternalAssets = new List<MenuExternalAsset>();
        public List<MenuScrapbookEntry> Scrapbook = new List<MenuScrapbookEntry>();
        public List<string> MissingArt = new List<string>();
        public List<string> UnresolvedMacros = new List<string>();
        public List<string> UnresolvedStrings = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<MenuCount> Counts = new List<MenuCount>();
    }

    /// <summary>The named source text of one GUI script.</summary>
    public sealed class MenuScriptSource
    {
        public string Name = string.Empty;
        public string Text = string.Empty;
    }

    /// <summary>Everything the decode reads, so it runs without touching a disk.</summary>
    public sealed class MenuLayoutInput
    {
        public string LayoutCsv = string.Empty;
        public string ScrapbookCsv = string.Empty;
        public string ResourceHeader = string.Empty;
        public List<MenuScriptSource> Scripts = new List<MenuScriptSource>();
        public List<string> ArchiveFiles = new List<string>();
        public List<string> PatchMembers = new List<string>();
        public Dictionary<int, string> Strings = new Dictionary<int, string>();
    }
}
