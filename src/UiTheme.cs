using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace DellFans
{
    public sealed class UiPalette
    {
        readonly Dictionary<string, Color> colors;
        public string Id { get; private set; }
        public string Name { get; private set; }
        public bool IsCustom { get; private set; }
        public string FontFamily { get; private set; }
        // Card corner radius in logical pixels (96 dpi).
        public int CornerRadius { get; private set; }
        // 0-100: how dark the soft card shadow is.
        public int ShadowStrength { get; private set; }
        public Color Background { get { return colors["background"]; } }
        public Color Surface { get { return colors["surface"]; } }
        public Color SurfaceAlt { get { return colors["surfaceAlt"]; } }
        public Color Ink { get { return colors["ink"]; } }
        public Color Muted { get { return colors["muted"]; } }
        public Color Accent { get { return colors["accent"]; } }
        public Color AccentInk { get { return colors["accentInk"]; } }
        public Color Edge { get { return colors["edge"]; } }
        public Color Warning { get { return colors["warning"]; } }
        public Color Input { get { return colors["input"]; } }
        public Color Selection { get { return colors["selection"]; } }
        internal UiPalette(string id, string name, bool custom, Dictionary<string, Color> values, string fontFamily, int radius, int shadow)
        { Id = id; Name = name; IsCustom = custom; colors = new Dictionary<string, Color>(values); FontFamily = fontFamily; CornerRadius = radius; ShadowStrength = shadow; }
        internal Dictionary<string, Color> CopyColors() { return new Dictionary<string, Color>(colors); }
    }

    // One set of design tokens for every page: radius, spacing and type sizes are logical pixels at 96 dpi and are scaled by the surface.
    public static class UiMetrics
    {
        public const int CardRadius = 20, ChipRadius = 12, Gap = 16, Pad = 20, Margin = 24, ControlHeight = 40;
        public const float TitleSize = 13, BodySize = 13, SmallSize = 12, ValueSize = 40, GaugeSize = 36;
    }

    // Versioned data resources affect only GUI presentation, never thermal control or executable code.
    public static class UiTheme
    {
        static readonly string[] colorNames = { "background", "surface", "surfaceAlt", "ink", "muted", "accent", "accentInk", "edge", "warning", "input", "selection" };
        // Colors follow the Rust deck palettes (bg -> surface, chip -> surfaceAlt, text, muted, accent, border, warn); background is one step deeper.
        static readonly UiPalette[] builtins = {
            Builtin("white", "WHITE", "#EDF0F5 #FFFFFF #E8EBF1 #1B1F27 #5C6472 #2F62D4 #FFFFFF #D3D9E3 #BE3A2E #F3F5F9 #DCE6FB", 20),
            Builtin("black", "BLACK", "#0D0E10 #17181B #25272C #F2F3F5 #9BA0AA #4C8DFF #08101F #2E3037 #FF6B6B #1E2024 #1F3256", 85),
            Builtin("purple-black", "PURPLE-BLACK", "#0B0911 #14111C #241E33 #EFEAFB #A79CC4 #9B6BFF #120A24 #3A3054 #FF6B8A #1B1626 #33264F", 100)
        };
        // Alias ids accepted as a skin "base".
        static readonly string[,] legacyIds = { { "light", "white" }, { "black-gray", "black" } };
        static IList<UiPalette> available = Array.AsReadOnly(builtins);
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static readonly Dictionary<string, FontFamily> families = new Dictionary<string, FontFamily>();
        public static UiPalette Current { get; private set; }
        public static IList<UiPalette> Available { get { return available; } }
        static IList<string> diagnostics = Array.AsReadOnly(new string[0]);
        public static string ThemesDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DellFans", "Themes"); } }
        public static IList<string> Diagnostics { get { return diagnostics; } }
        public static event Action Changed;
        static UiTheme() { Current = builtins[1]; }
        static UiPalette Builtin(string id, string name, string values, int shadow)
        {
            string[] parts = values.Split(' '); Dictionary<string, Color> colors = new Dictionary<string, Color>();
            for (int i = 0; i < colorNames.Length; i++) colors.Add(colorNames[i], ColorTranslator.FromHtml(parts[i]));
            return new UiPalette(id, name, false, colors, "Segoe UI", UiMetrics.CardRadius, shadow);
        }
        public static bool Select(string id)
        {
            UiPalette found = null;
            foreach (UiPalette p in available) if (p.Id == id) { found = p; break; }
            UiPalette selected = found ?? builtins[1];
            if (!Object.ReferenceEquals(Current, selected))
            {
                Current = selected; Action changed = Changed; if (changed != null) changed();
            }
            return found != null;
        }
        // Mode accent colors are shared with the deck; they are not part of a skin.
        public static Color ModeColor(string mode)
        {
            switch (mode)
            {
                case FanModes.Quiet: return Color.FromArgb(0x3F, 0xB6, 0x8B);
                case FanModes.Balanced: return Color.FromArgb(0x4C, 0x8D, 0xFF);
                case FanModes.Performance: return Color.FromArgb(0xFF, 0x8A, 0x3D);
                case FanModes.Custom: return Color.FromArgb(0xA2, 0x7B, 0xFF);
                default: return Color.FromArgb(0x8A, 0x8F, 0x99);
            }
        }
        // One cached family and one font per (size, style): nothing is created per paint or per control.
        public static Font Font(float pixels, bool bold)
        {
            string key = Current.FontFamily + "|" + pixels.ToString("0.#") + "|" + (bold ? "b" : "r");
            lock (fonts)
            {
                Font font;
                if (fonts.TryGetValue(key, out font)) return font;
                FontFamily family;
                if (!families.TryGetValue(Current.FontFamily, out family)) { family = new FontFamily(Current.FontFamily); families[Current.FontFamily] = family; }
                font = new Font(family, pixels, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = font; return font;
            }
        }
        // Fonts and families are cached for the life of the process (a handful of sizes and at most one family per skin); this frees them
        // at exit or between tests. Never call it while a window is open.
        public static void ReleaseFonts()
        {
            lock (fonts)
            {
                foreach (Font font in fonts.Values) font.Dispose();
                fonts.Clear();
                foreach (FontFamily family in families.Values) family.Dispose();
                families.Clear();
            }
        }
        public static int CachedFontCount { get { lock (fonts) return fonts.Count; } }
        public static bool TryParse(string json, out UiPalette palette, out string error)
        {
            palette = null; error = null;
            if (json == null || json.Length > 16384 || Encoding.UTF8.GetByteCount(json) > 16384)
            { error = "Theme JSON must be at most 16 KiB."; return false; }
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 16384, RecursionLimit = 4 };
                Dictionary<string, object> data = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) { error = "Theme must be a JSON object."; return false; }
                foreach (string key in data.Keys)
                    if (Array.IndexOf(new[] { "schemaVersion", "id", "name", "base", "fontFamily", "cornerRadius", "shadowStrength", "colors" }, key) < 0)
                    { error = "Unknown theme property."; return false; }
                object version; string id, name, baseId;
                if (!data.TryGetValue("schemaVersion", out version) || !(version is int) || (int)version != 1)
                { error = "Unsupported theme schemaVersion; expected 1."; return false; }
                if (!StringValue(data, "id", out id) || !Regex.IsMatch(id, "\\A[a-z][a-z0-9-]{0,39}\\z"))
                { error = "Theme id must be 1-40 lowercase letters, digits or hyphens, beginning with a letter."; return false; }
                foreach (UiPalette builtin in builtins)
                    if (builtin.Id == id) { error = "Built-in theme ids cannot be replaced."; return false; }
                for (int i = 0; i < legacyIds.GetLength(0); i++)
                    if (legacyIds[i, 0] == id) { error = "Built-in theme ids cannot be replaced."; return false; }
                if (!StringValue(data, "name", out name) || name.Length == 0 || name.Length > 48 || name.Trim() != name)
                { error = "Theme name must contain 1-48 visible characters."; return false; }
                foreach (char c in name) if (Char.IsControl(c)) { error = "Theme name contains a control character."; return false; }
                UiPalette source = null;
                if (StringValue(data, "base", out baseId))
                {
                    for (int i = 0; i < legacyIds.GetLength(0); i++) if (legacyIds[i, 0] == baseId) baseId = legacyIds[i, 1];
                    foreach (UiPalette builtin in builtins) if (builtin.Id == baseId) source = builtin;
                }
                if (source == null) { error = "Theme base must be white, black or purple-black."; return false; }
                string font = source.FontFamily;
                if (data.ContainsKey("fontFamily") && (!StringValue(data, "fontFamily", out font) ||
                    Array.IndexOf(new[] { "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei", "Arial", "Tahoma" }, font) < 0))
                { error = "Theme fontFamily is not in the supported system-font list."; return false; }
                int radius = source.CornerRadius, shadow = source.ShadowStrength; object number;
                if (data.TryGetValue("cornerRadius", out number))
                {
                    if (!(number is int) || (int)number < 0 || (int)number > 28)
                    { error = "Theme cornerRadius must be an integer from 0 to 28."; return false; }
                    radius = (int)number;
                }
                if (data.TryGetValue("shadowStrength", out number))
                {
                    if (!(number is int) || (int)number < 0 || (int)number > 100)
                    { error = "Theme shadowStrength must be an integer from 0 to 100."; return false; }
                    shadow = (int)number;
                }
                Dictionary<string, Color> colors = source.CopyColors(); object colorValue;
                if (data.TryGetValue("colors", out colorValue))
                {
                    Dictionary<string, object> overrides = colorValue as Dictionary<string, object>;
                    if (overrides == null) { error = "Theme colors must be a JSON object."; return false; }
                    foreach (KeyValuePair<string, object> entry in overrides)
                    {
                        string color = entry.Value as string;
                        if (!colors.ContainsKey(entry.Key)) { error = "Unknown theme color token."; return false; }
                        if (color == null || !Regex.IsMatch(color, "\\A#[0-9a-fA-F]{6}\\z"))
                        { error = "Theme colors must use opaque #RRGGBB values."; return false; }
                        colors[entry.Key] = ColorTranslator.FromHtml(color);
                    }
                }
                UiPalette candidate = new UiPalette(id, name, true, colors, font, radius, shadow);
                foreach (Color surface in new[] { candidate.Background, candidate.Surface, candidate.SurfaceAlt, candidate.Input })
                    foreach (Color ink in new[] { candidate.Ink, candidate.Muted, candidate.Warning, candidate.Accent })
                        if (Contrast(ink, surface) < 4.5) { error = "Theme text colors need at least 4.5:1 contrast on every surface."; return false; }
                if (Contrast(candidate.AccentInk, candidate.Accent) < 4.5 || Contrast(candidate.Ink, candidate.Selection) < 4.5)
                { error = "Theme action and selection labels need at least 4.5:1 contrast."; return false; }
                palette = candidate; return true;
            }
            // Untrusted input: JavaScriptSerializer also throws FormatException (malformed \u escapes) and others, never only ArgumentException.
            catch (Exception) { error = "Invalid theme JSON."; return false; }
        }
        static bool StringValue(Dictionary<string, object> data, string key, out string value)
        { object raw; value = data.TryGetValue(key, out raw) ? raw as string : null; return value != null; }
        static double Channel(byte value)
        { double c = value / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color)
        { return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B); }
        public static double Contrast(Color a, Color b)
        { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
        // Explicit GUI startup/reload only: service and CLI startup never scan per-user resources.
        public static string[] ReloadCustomThemes()
        { return ReloadCustomThemes(ThemesDirectory); }
        public static string[] ReloadCustomThemes(string directory)
        {
            List<UiPalette> next = new List<UiPalette>(builtins); List<string> errors = new List<string>();
            try
            {
                if (String.IsNullOrEmpty(directory) || !Path.IsPathRooted(directory) || Path.GetFullPath(directory).StartsWith("\\\\", StringComparison.Ordinal))
                    errors.Add("Theme directory must be a local absolute path.");
                else if (Directory.Exists(directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) errors.Add("Theme directory cannot be a symbolic link or junction.");
                    else
                    {
                        int count = 0;
                        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
                        {
                            if (++count > 32) { errors.Add("Only the first 32 theme JSON files are loaded."); break; }
                            string filename = Path.GetFileName(path), id = Path.GetFileNameWithoutExtension(path);
                            if (!Regex.IsMatch(id, "\\A[a-z][a-z0-9-]{0,39}\\z")) { errors.Add(filename + ": the file name is not a valid theme id."); continue; }
                            try
                            {
                                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { errors.Add(filename + ": symbolic links are not supported."); continue; }
                                byte[] bytes = new byte[16385]; int length = 0;
                                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                                {
                                    while (length < bytes.Length)
                                    {
                                        int read = stream.Read(bytes, length, bytes.Length - length); if (read == 0) break; length += read;
                                    }
                                }
                                if (length > 16384) { errors.Add(filename + ": theme JSON exceeds 16 KiB."); continue; }
                                string json = new UTF8Encoding(false, true).GetString(bytes, 0, length);
                                if (json.Length > 0 && json[0] == '﻿') json = json.Substring(1);
                                UiPalette palette; string error;
                                if (!TryParse(json, out palette, out error)) { errors.Add(filename + ": " + error); continue; }
                                if (palette.Id != id) { errors.Add(filename + ": filename must match the theme id."); continue; }
                                bool duplicate = false;
                                foreach (UiPalette existing in next) if (existing.Id == palette.Id) duplicate = true;
                                if (duplicate) { errors.Add(filename + ": duplicate theme id."); continue; }
                                next.Add(palette);
                            }
                            catch (IOException) { errors.Add(filename + ": theme file could not be read."); }
                            catch (UnauthorizedAccessException) { errors.Add(filename + ": theme file access denied."); }
                            catch (System.Security.SecurityException) { errors.Add(filename + ": theme file access denied."); }
                            catch (DecoderFallbackException) { errors.Add(filename + ": theme file must be valid UTF-8."); }
                            catch (Exception) { errors.Add(filename + ": theme file is invalid."); } // one bad file never blocks the others
                        }
                    }
                }
            }
            catch (IOException) { errors.Add("Theme directory could not be read."); }
            catch (UnauthorizedAccessException) { errors.Add("Theme directory access denied."); }
            catch (System.Security.SecurityException) { errors.Add("Theme directory access denied."); }
            catch (ArgumentException) { errors.Add("Theme directory path is invalid."); }
            catch (NotSupportedException) { errors.Add("Theme directory path is invalid."); }
            catch (Exception) { errors.Add("Theme directory could not be read."); }
            string selected = Current.Id;
            available = next.AsReadOnly(); diagnostics = errors.AsReadOnly(); Select(selected);
            string folder; try { folder = Path.GetFileName((directory ?? "").TrimEnd('\\', '/')); } catch (ArgumentException) { folder = ""; }
            UiText.SetIgnored("themes", UiText.IgnoredFromDiagnostics(errors, string.IsNullOrEmpty(folder) ? "Themes" : folder));
            return errors.ToArray();
        }
    }
}
