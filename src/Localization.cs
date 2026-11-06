using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DellFans
{
    // An external skin, language or preference file that was skipped, and why (N-04).
    public sealed class IgnoredFile
    {
        public string FileName, Reason;
    }
    public static class UiText
    {
        const int MaxPackBytes = 65536, MaxPackFiles = 32;
        static readonly string[] ignoredSources = { "preferences", "themes", "languages" };
        static readonly Dictionary<string, IgnoredFile[]> ignoredBySource = new Dictionary<string, IgnoredFile[]>();
        static string OneLine(string value, int limit)
        {
            value = Regex.Replace(value ?? "", @"[\r\n\t]+", " ").Trim();
            return value.Length > limit ? value.Substring(0, limit) : value;
        }
        // Replaces what one source (preferences, themes or languages) reports as skipped; null clears it.
        public static event Action IgnoredChanged;
        public static void SetIgnored(string source, IEnumerable<IgnoredFile> files)
        {
            lock (ignoredBySource)
            {
                if (files == null) ignoredBySource.Remove(source);
                else
                {
                    List<IgnoredFile> copy = new List<IgnoredFile>();
                    foreach (IgnoredFile file in files) copy.Add(new IgnoredFile { FileName = OneLine(file.FileName, 100), Reason = OneLine(file.Reason, 200) });
                    ignoredBySource[source] = copy.ToArray();
                }
            }
            Action changed = IgnoredChanged; if (changed != null) changed();
        }
        // "name.json: reason" diagnostics become entries; a diagnostic without a file name is attributed to the folder.
        public static IgnoredFile[] IgnoredFromDiagnostics(IEnumerable<string> diagnostics, string folderName)
        {
            List<IgnoredFile> result = new List<IgnoredFile>();
            foreach (string line in diagnostics)
            {
                Match match = Regex.Match(line ?? "", @"\A(?<file>[^:\\/]{1,120}\.json): (?<reason>.*)\z", RegexOptions.Singleline);
                result.Add(match.Success ? new IgnoredFile { FileName = match.Groups["file"].Value, Reason = match.Groups["reason"].Value } : new IgnoredFile { FileName = folderName, Reason = line });
            }
            return result.ToArray();
        }
        public static IgnoredFile[] IgnoredFiles
        {
            get
            {
                List<IgnoredFile> all = new List<IgnoredFile>();
                lock (ignoredBySource)
                    foreach (string source in ignoredSources) { IgnoredFile[] files; if (ignoredBySource.TryGetValue(source, out files)) all.AddRange(files); }
                return all.ToArray();
            }
        }
        // One line per skipped file in the active language ("已忽略：文件名（原因）"), empty when nothing was skipped.
        public static string IgnoredSummary()
        {
            List<string> lines = new List<string>();
            foreach (IgnoredFile file in IgnoredFiles) lines.Add(F("已忽略：{0}（{1}）", file.FileName, file.Reason));
            return string.Join(Environment.NewLine, lines.ToArray());
        }
        sealed class LanguagePack
        {
            public string Culture, DisplayName;
            public Dictionary<string, string> Translations;
        }
        static readonly Dictionary<string, LanguagePack> packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);
        static string selectedCulture = CultureInfo.CurrentUICulture.Name;
        static string language = Resolve(selectedCulture);
        static LanguagePack activePack;
        static string[] packDiagnostics = new string[0];
        // Keep initialization after startup has selected the thread's system UI culture.
        static UiText() { }
        public static string Language { get { return language; } }
        public static KeyValuePair<string,string>[] AvailableLanguages
        {
            get
            {
                Dictionary<string,string> choices=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {{"zh-Hans","简体中文"},{"zh-Hant","繁體中文"},{"en","English"}};
                foreach(LanguagePack pack in packs.Values) choices[pack.Culture]=pack.DisplayName;
                KeyValuePair<string,string>[] result=new KeyValuePair<string,string>[choices.Count];int index=0;foreach(var choice in choices)result[index++]=choice;return result;
            }
        }
        public static string Resolve(string culture)
        {
            LanguagePack pack = MatchingPack(culture);
            if (pack != null) return pack.Culture;
            string[] parts = (culture ?? "").Trim().ToLowerInvariant().Split('-');
            if (parts[0] != "zh") return "en";
            if (parts.Length == 1) return "zh-Hans";
            string script = parts[1] == "hans" || parts[1] == "chs" ? "zh-Hans" :
                parts[1] == "hant" || parts[1] == "cht" ? "zh-Hant" : null;
            if (script != null && (parts.Length == 2 || (parts.Length == 3 && Region(parts[2])))) return script;
            if (parts.Length == 2)
            {
                if (parts[1] == "cn" || parts[1] == "sg") return "zh-Hans";
                if (parts[1] == "tw" || parts[1] == "hk" || parts[1] == "mo") return "zh-Hant";
            }
            return "en";
        }
        static bool Region(string value)
        {
            if (value.Length != 2 && value.Length != 3) return false;
            foreach (char c in value)
                if (value.Length == 2 ? c < 'a' || c > 'z' : c < '0' || c > '9') return false;
            return true;
        }
        static LanguagePack MatchingPack(string culture)
        {
            string name; LanguagePack pack;
            if (!CultureName((culture ?? "").Trim(), out name)) return null;
            if (packs.TryGetValue(name, out pack) || packs.TryGetValue(name.Split('-')[0], out pack)) return pack;
            return null;
        }
        public static void Select(string culture) { selectedCulture = culture; activePack = MatchingPack(culture); language = Resolve(culture); }
        public static string PacksDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DellFans", "Languages"); } }
        public static string[] PackDiagnostics { get { return (string[])packDiagnostics.Clone(); } }
        public static string[] ValidatePack(string json)
        {
            try { ReadPack(json); return new string[0]; }
            catch (Exception error) { return new[] { error.GetBaseException().Message }; }
        }
        public static string ExportTemplate(string culture, string displayName)
        {
            string name;
            if (!CultureName(culture, out name)) throw new ArgumentException("Culture must be a valid culture name.", "culture");
            if (!PlainText(displayName, 80, false)) throw new ArgumentException("Display name must be plain text of at most 80 characters.", "displayName");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            StringBuilder result = new StringBuilder();
            result.AppendLine("{").AppendLine("  \"schemaVersion\": 1,");
            result.Append("  \"culture\": ").Append(serializer.Serialize(name)).AppendLine(",");
            result.Append("  \"displayName\": ").Append(serializer.Serialize(displayName)).AppendLine(",");
            result.AppendLine("  \"translations\": {");
            List<string> keys = new List<string>(text.Keys); keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
                result.Append("    ").Append(serializer.Serialize(keys[i])).Append(": ").Append(serializer.Serialize(text[keys[i]][0])).AppendLine(i + 1 == keys.Count ? "" : ",");
            result.AppendLine("  }").AppendLine("}");
            return result.ToString();
        }
        public static string[] InitializePacks() { return InitializePacks(PacksDirectory); }
        public static string[] InitializePacks(string directory)
        {
            packs.Clear();
            List<string> errors = new List<string>();
            try
            {
                if (String.IsNullOrEmpty(directory) || Path.GetFullPath(directory).StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Language folder must be a local directory.");
                if (Directory.Exists(directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Language folder cannot be a reparse point.");
                    List<string> files = new List<string>();
                    foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        files.Add(file);
                        if (files.Count > MaxPackFiles) { files.Clear(); errors.Add("Language folder: at most 32 JSON files are allowed; no packs loaded."); break; }
                    }
                    files.Sort(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in files)
                    {
                        try
                        {
                            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Language files cannot be reparse points.");
                            string json;
                            using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                if (stream.Length > MaxPackBytes) throw new FormatException("File exceeds 64 KiB.");
                                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true), true)) json = reader.ReadToEnd();
                            }
                            LanguagePack pack = ReadPack(json);
                            if (packs.ContainsKey(pack.Culture) || duplicates.Contains(pack.Culture))
                            {
                                packs.Remove(pack.Culture); duplicates.Add(pack.Culture);
                                throw new FormatException("Duplicate culture; all packs for this culture are ignored.");
                            }
                            packs.Add(pack.Culture, pack);
                        }
                        catch (Exception error) { errors.Add(Path.GetFileName(file) + ": " + error.GetBaseException().Message); }
                    }
                }
            }
            catch (Exception error) { errors.Add("Language folder: " + error.GetBaseException().Message); }
            Select(selectedCulture);
            packDiagnostics = errors.ToArray();
            string folder;
            try { folder = Path.GetFileName((directory ?? "").TrimEnd('\\', '/')); } catch (ArgumentException) { folder = ""; }
            SetIgnored("languages", IgnoredFromDiagnostics(errors, string.IsNullOrEmpty(folder) ? "Languages" : folder));
            return PackDiagnostics;
        }
        static bool CultureName(string culture, out string name)
        {
            name = null;
            if (culture == null || culture.Length > 35 || !Regex.IsMatch(culture, @"\A[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8}){0,3}\z")) return false;
            try { name = CultureInfo.GetCultureInfo(culture).Name; return name.Length != 0; }
            catch (CultureNotFoundException) { return false; }
        }
        static bool PlainText(string value, int limit, bool multiline)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > limit || value.IndexOfAny(new[] { '<', '>' }) >= 0) return false;
            foreach (char c in value) if (Char.IsControl(c) && !(multiline && (c == '\n' || c == '\r' || c == '\t'))) return false;
            return true;
        }
        static LanguagePack ReadPack(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaxPackBytes) throw new FormatException("File exceeds 64 KiB or is empty.");
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaxPackBytes, RecursionLimit = 8 };
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null || root.Count != 4 || !root.ContainsKey("schemaVersion") || !(root["schemaVersion"] is int) || (int)root["schemaVersion"] != 1 ||
                !root.ContainsKey("culture") || !root.ContainsKey("displayName") || !root.ContainsKey("translations")) throw new FormatException("Expected schemaVersion 1, culture, displayName, and translations only.");
            string culture = root["culture"] as string, name;
            string display = root["displayName"] as string;
            Dictionary<string, object> entries = root["translations"] as Dictionary<string, object>;
            if (!CultureName(culture, out name)) throw new FormatException("Culture must be a valid culture name.");
            if (!PlainText(display, 80, false)) throw new FormatException("Display name must be plain text of at most 80 characters.");
            if (entries == null || entries.Count == 0 || entries.Count > text.Count) throw new FormatException("Translations must be a nonempty dictionary of known message IDs.");
            Dictionary<string, string> translations = new Dictionary<string, string>();
            foreach (KeyValuePair<string, object> entry in entries)
            {
                string value = entry.Value as string;
                if (!text.ContainsKey(entry.Key)) throw new FormatException("Unknown message ID: " + entry.Key);
                if (!PlainText(value, 4096, true)) throw new FormatException("Translation must be plain text of at most 4096 characters: " + entry.Key);
                if (!SameArguments(text[entry.Key][0], value)) throw new FormatException("Format arguments must preserve their index, count, alignment, and format: " + entry.Key);
                translations.Add(entry.Key, value);
            }
            return new LanguagePack { Culture = name, DisplayName = display, Translations = translations };
        }
        static List<string> Arguments(string value)
        {
            List<string> result = new List<string>();
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '{' && value[i] != '}') continue;
                if (i + 1 < value.Length && value[i + 1] == value[i]) { i++; continue; }
                if (value[i] == '}') throw new FormatException("Unmatched format brace.");
                int end = value.IndexOf('}', i + 1);
                if (end < 0) throw new FormatException("Unmatched format brace.");
                string argument = value.Substring(i + 1, end - i - 1);
                if (!Regex.IsMatch(argument, @"\A[0-9]{1,2}(?:,-?[0-9]{1,3})?(?::[^{}]{0,64})?\z")) throw new FormatException("Invalid format argument.");
                result.Add(argument); i = end;
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }
        static bool SameArguments(string source, string translation)
        {
            List<string> expected = Arguments(source), actual = Arguments(translation);
            if (expected.Count != actual.Count) return false;
            for (int i = 0; i < expected.Count; i++) if (expected[i] != actual[i]) return false;
            return true;
        }
        // The source text is Simplified Chinese. Device names and technical identifiers remain unchanged.
        static readonly Dictionary<string, string[]> text = new Dictionary<string, string[]>
        {
            {"设置",new[]{"Settings","設定"}},
            {"皮肤",new[]{"Theme","外觀"}},
            {"语言",new[]{"Language","語言"}},
            { "最小化", new[] { "Minimize", "最小化" } },
            { "最大化", new[] { "Maximize", "最大化" } },
            { "还原", new[] { "Restore", "還原" } },
            { "系统风扇", new[] { "System fan", "系統風扇" } },
            { "静音", new[] { "Quiet", "靜音" } },
            { "平衡", new[] { "Balanced", "平衡" } },
            { "高性能", new[] { "Performance", "高效能" } },
            { "自定义", new[] { "Custom", "自訂" } },
            { "模式", new[] { "Mode", "模式" } },
            { "保存模式", new[] { "Save mode", "儲存模式" } },
            { "重新应用模式", new[] { "Reapply mode", "重新套用模式" } },
            { "{0} · BIOS 控制，不接管风扇", new[] { "{0} · BIOS in control, no takeover", "{0} · BIOS 控制，不接管風扇" } },
            { "{0} · {1:N0}–{2:N0} RPM · {3}°C 起调", new[] { "{0} · {1:N0}–{2:N0} RPM · starts at {3}°C", "{0} · {1:N0}–{2:N0} RPM · {3}°C 起調" } },
            { "保存模式 {0}", new[] { "Save mode: {0}", "儲存模式：{0}" } },
            { "保存自定义最低转速 {0} RPM", new[] { "Save custom minimum: {0} RPM", "儲存自訂最低轉速 {0} RPM" } },
            { "请输入 1000–5570 的整数 RPM。", new[] { "Enter a whole number of RPM from 1000 to 5570.", "請輸入 1000–5570 的整數 RPM。" } },
            { "将交回 BIOS 自动控制，服务不再接管风扇。已保存的自定义最低转速会保留。", new[] { "Control returns to the BIOS and the service stops taking over the fan. The saved custom minimum is kept.", "將交回 BIOS 自動控制，服務不再接管風扇。已儲存的自訂最低轉速會保留。" } },
            { "故障暂停 · BIOS 自动，已保存的模式仍保留", new[] { "Paused after an error · BIOS automatic; the saved mode is retained", "故障暫停 · BIOS 自動，已儲存的模式仍保留" } },
            { "BIOS 自动模式，沿用 BIOS 风扇控制。", new[] { "BIOS automatic mode. BIOS remains in control.", "BIOS 自動模式，沿用 BIOS 風扇控制。" } },
            { "CPU 温度达到 90 °C，保持全速，降到 85 °C 后恢复模式规则。", new[] { "CPU temperature reached 90 °C. Full speed is maintained until it falls to 85 °C, then the mode rules resume.", "CPU 溫度達到 90 °C，保持全速，降到 85 °C 後恢復模式規則。" } },
            { "模式控制：高于起调温度每秒 +50 RPM，低于起调温度 3 °C 并稳定 20 秒后每秒 −100 RPM。", new[] { "Mode control: +50 RPM per second above the start temperature; −100 RPM per second after 20 s stable 3 °C below it.", "模式控制：高於起調溫度每秒 +50 RPM，低於起調溫度 3 °C 並穩定 20 秒後每秒 −100 RPM。" } },
            { "CPU 温度未就绪，模式尚未保存或启用。", new[] { "CPU temperature is not ready. The mode was not saved or enabled.", "CPU 溫度尚未就緒，模式尚未儲存或啟用。" } },
            { "已临时交回 BIOS 自动；配置已保留，重启服务或重新应用模式可恢复。", new[] { "Temporarily returned to BIOS automatic control. Settings are retained; restart the service or reapply the mode to resume.", "已暫時交回 BIOS 自動；設定已保留，重新啟動服務或重新套用模式可恢復。" } },
            { "后台服务资源持续超限，已临时恢复 BIOS 自动，保存的模式未修改。", new[] { "The service exceeded its resource limits. BIOS automatic control was restored temporarily; the saved mode is unchanged.", "背景服務資源持續超限，已暫時恢復 BIOS 自動，已儲存的模式未修改。" } },
            { "CPU 温度读取失效，已临时恢复 BIOS 自动，保存的模式未修改。", new[] { "CPU temperature readings failed. BIOS automatic control was restored temporarily; the saved mode is unchanged.", "CPU 溫度讀取失效，已暫時恢復 BIOS 自動，已儲存的模式未修改。" } },
            { "BIOS 自动", new[] { "BIOS auto", "BIOS 自動" } },
            { "全速覆盖", new[] { "Full speed", "全速覆寫" } },
            { "全速保底", new[] { "Max floor", "全速保底" } },
            { "探测转速响应", new[] { "Fan probe", "探測轉速回應" } },
            { "导出当前数据", new[] { "Export", "匯出目前資料" } },
            { "最低 RPM", new[] { "Min RPM", "最低 RPM" } },
            { "正在连接本机传感器…", new[] { "Connecting to the background service…", "正在連線本機感測器…" } },
            { "传感器", new[] { "Sensor", "感測器" } },
            { "最低", new[] { "Min", "最低" } },
            { "最高", new[] { "Max", "最高" } },
            { "状态", new[] { "Status", "狀態" } },
            { "不可用", new[] { "Unavailable", "無法使用" } },
            { "当前温度", new[] { "Temperature", "目前溫度" } },
            { "风扇转速", new[] { "Fan speed", "風扇轉速" } },
            { "功耗", new[] { "Power", "功耗" } },
            { "频率", new[] { "Clock", "頻率" } },
            { "保存", new[] { "Save", "儲存" } },
            { "取消", new[] { "Cancel", "取消" } },
            { "启动失败", new[] { "Unable to start", "啟動失敗" } },
            { "模式未知", new[] { "Unknown mode", "模式未知" } },
            { "等待采样", new[] { "Pending", "等待取樣" } },
            { "{0}，已应用，当前状态已更新。", new[] { "{0}: applied and saved.", "{0}，已套用，目前狀態已更新。" } },
            { "已导出：{0}", new[] { "Exported: {0}", "已匯出：{0}" } },
            { "导出完成", new[] { "Export complete", "匯出完成" } },
            { "导出失败", new[] { "Export failed", "匯出失敗" } },
            { "当前数据已导出到：\n{0}", new[] { "The current data was exported to:\n{0}", "目前資料已匯出到：\n{0}" } },
            { "无法导出当前数据：\n{0}", new[] { "Unable to export the current data:\n{0}", "無法匯出目前資料：\n{0}" } },
            { "打开所在文件夹",new[] { "Show in folder", "開啟所在資料夾" } },
            { "确定", new[] { "OK", "確定" } },
            { "检测风扇控制接口？", new[] { "Check the fan control interface?", "檢測風扇控制介面？" } },
            { "首次使用需要确认这台电脑的 Dell 风扇接口可以控制：风扇将全速运行约 15 秒，结束后自动交回 BIOS 控制。\n\n只读检查已通过。检测不通过时只生成报告，不改变任何设置。",
                new[] { "Before first use DellFans confirms that this computer's Dell fan interface can be controlled: the fan runs at full speed for about 15 seconds, then control returns to the BIOS automatically.\n\nThe read-only checks have passed. If the check fails, only a report is written; no setting changes.",
                        "首次使用需要確認這台電腦的 Dell 風扇介面可以控制：風扇將全速運轉約 15 秒，結束後自動交回 BIOS 控制。\n\n唯讀檢查已通過。檢測不通過時只產生報告，不變更任何設定。" } },
            { "开始检测", new[] { "Start check", "開始檢測" } },
            { "正在启动后台控速…", new[] { "Starting the background controller…", "正在啟動背景控速…" } },
            { "已取消管理员授权，后台控速未启动；重新打开窗口可再次启动。", new[] { "Administrator permission was declined, so the background controller did not start. Reopen the window to try again.", "已取消系統管理員授權，背景控速未啟動；重新開啟視窗可再次啟動。" } },
            { "后台控速未运行 · 重新打开窗口可启动（需要管理员授权）", new[] { "Background controller not running · reopen the window to start it (administrator permission needed)", "背景控速未執行 · 重新開啟視窗可啟動（需要系統管理員授權）" } },
            { "当前电脑不支持", new[] { "This computer is not supported", "目前電腦不支援" } },
            { "这台电脑的 Dell 风扇接口检测未通过，DellFans 不会控制风扇，风扇保持 BIOS 自动。\n\n欢迎到 {0} 反馈你的机型。",
                new[] { "This computer's Dell fan interface did not pass the check. DellFans will not control the fan; it stays on BIOS automatic.\n\nYou are welcome to report your model at {0}.",
                        "這台電腦的 Dell 風扇介面檢測未通過，DellFans 不會控制風扇，風扇維持 BIOS 自動。\n\n歡迎到 {0} 回報你的機型。" } },
            { "这台电脑的 Dell 风扇接口检测未通过，DellFans 不会控制风扇，风扇保持 BIOS 自动。\n\n检测报告已保存到：\n{0}\n\n欢迎把报告提交到 {1}，帮助支持更多机型。报告不含序列号、用户名和计算机名。",
                new[] { "This computer's Dell fan interface did not pass the check. DellFans will not control the fan; it stays on BIOS automatic.\n\nThe check report was saved to:\n{0}\n\nPlease submit it at {1} to help support more models. The report contains no serial number, user name or computer name.",
                        "這台電腦的 Dell 風扇介面檢測未通過，DellFans 不會控制風扇，風扇維持 BIOS 自動。\n\n檢測報告已儲存到：\n{0}\n\n歡迎把報告提交到 {1}，協助支援更多機型。報告不含序號、使用者名稱和電腦名稱。" } },
            { "尚未检测风扇控制接口 · 风扇保持 BIOS 自动；重新打开窗口可开始检测", new[] { "Fan control interface not checked yet · the fan stays on BIOS automatic; reopen the window to start the check", "尚未檢測風扇控制介面 · 風扇維持 BIOS 自動；重新開啟視窗可開始檢測" } },
            { "尚未检测风扇控制接口，风扇保持 BIOS 自动；重新打开窗口可开始检测。", new[] { "The fan control interface was not checked; the fan stays on BIOS automatic. Reopen the window to start the check.", "尚未檢測風扇控制介面，風扇維持 BIOS 自動；重新開啟視窗可開始檢測。" } },
            { "当前电脑不支持 · 风扇保持 BIOS 自动", new[] { "This computer is not supported · the fan stays on BIOS automatic", "目前電腦不支援 · 風扇維持 BIOS 自動" } },
            { "正在检测风扇控制接口（风扇将短暂全速，约 20 秒）…", new[] { "Checking the fan control interface (the fan briefly runs at full speed, about 20 seconds)…", "正在檢測風扇控制介面（風扇將短暫全速，約 20 秒）…" } },
            { "检测通过：这台电脑支持 DellFans 风扇控制。", new[] { "Check passed: this computer supports DellFans fan control.", "檢測通過：這台電腦支援 DellFans 風扇控制。" } },
            { "导出到 {0}", new[] { "Export to {0}", "匯出到 {0}" } },
            { "打开导出文件夹", new[] { "Open the export folder", "開啟匯出資料夾" } },
            { "正在探测转速；完成后恢复已保存配置…", new[] { "Probing the fan; the saved profile will be restored…", "正在探測轉速；完成後恢復已儲存設定…" } },
            { "探测完成", new[] { "Calibration complete", "探測完成" } },
            { "操作未完成，请导出诊断数据查看详情。", new[] { "Unable to complete this operation. Export diagnostic data for details.", "操作未完成，請匯出診斷資料查看詳情。" } },
            { "后台服务正在初始化传感器，请稍候…", new[] { "The background service is initializing sensors. Please wait…", "背景服務正在初始化感測器，請稍候…" } },
            { "后台操作超时，请读取状态确认结果。", new[] { "The background operation timed out. Refresh the status to check the result.", "背景操作逾時，請讀取狀態確認結果。" } },
            { "后台服务尚未完成风扇恢复，独立监护仍有效。", new[] { "Fan recovery is still pending; the independent watchdog remains active.", "背景服務尚未完成風扇恢復，獨立監護仍有效。" } },
            { "配置无效，未启用接管：", new[] { "The configuration is invalid; fan control was not enabled.", "設定無效，未啟用風扇控制。" } },
            { "后台温控异常，已临时恢复 BIOS 自动，配置已保留：", new[] { "Background temperature control failed. BIOS automatic control was requested; saved settings are retained.", "背景溫控異常，已請求暫時恢復 BIOS 自動，設定已保留。" } },
            { "转速控制异常，已临时恢复 BIOS 自动：", new[] { "Fan-speed control failed. BIOS automatic control was requested.", "轉速控制異常，已請求暫時恢復 BIOS 自動。" } },
            { "短时探测完成后自动恢复原配置。", new[] { "The saved profile will be restored after the short fan probe.", "短暫探測完成後自動恢復原設定。" } },
            { "等待有效 CPU 温度；暂不接管 BIOS。", new[] { "Waiting for a valid CPU temperature; BIOS remains in control.", "等待有效 CPU 溫度；暫不接管 BIOS。" } },
            { "等待本机控制接口就绪；暂不接管 BIOS。", new[] { "Waiting for the fan-control interface; BIOS remains in control.", "等待本機控制介面就緒；暫不接管 BIOS。" } },
            { "当前机型或 BIOS 的手动控制接口未通过验证，保存的配置未修改。", new[] { "Manual control is not validated for this model or BIOS. Saved settings are unchanged.", "目前機型或 BIOS 的手動控制介面未通過驗證，儲存的設定未修改。" } },
            { "本机的手动控速接口不可用。", new[] { "Manual fan control is unavailable on this PC.", "本機的手動控速介面無法使用。" } },
            { "已有探测正在运行。", new[] { "A fan probe is already running.", "已有探測正在執行。" } },
            { "前一次恢复尚未完成，请先恢复 BIOS 自动模式。", new[] { "Recovery is still pending. Restore BIOS automatic mode before applying a target.", "前一次恢復尚未完成，請先恢復 BIOS 自動模式。" } },
            { "等待有效 CPU 温度采样后再应用设置。", new[] { "Wait for a valid CPU temperature sample before applying settings.", "等待有效 CPU 溫度取樣後再套用設定。" } },
            { "CPU 温度较高，请先使用全速覆盖或等待温度下降。", new[] { "CPU temperature is high. Use full speed or wait for the temperature to fall.", "CPU 溫度較高，請先使用全速覆寫或等待溫度下降。" } },
            { "控制循环中断，已请求恢复自动模式；请重新应用转速设置。", new[] { "The control loop was interrupted and automatic recovery was requested. Reapply the speed setting to resume.", "控制迴圈中斷，已請求恢復自動模式；請重新套用轉速設定。" } },
            { "控速线程被系统延迟超过 3 秒（如 CPU 满载），已临时交回 BIOS 自动；10 秒后自动恢复接管。", new[] { "The control thread was delayed by more than 3 s (for example by a full CPU load); BIOS automatic took over and control resumes after 10 s.", "控速執行緒被系統延遲超過 3 秒（如 CPU 滿載），已暫時交回 BIOS 自動；10 秒後自動恢復接管。" } },
            { "独立监护进程已退出，已交回 BIOS 自动；将重新启动监护后恢复接管。", new[] { "The independent watchdog exited; BIOS automatic took over and control resumes with a new watchdog.", "獨立監護程序已退出，已交回 BIOS 自動；將重新啟動監護後恢復接管。" } },
            { "独立监护进程已恢复风扇自动控制，请重新应用设置。", new[] { "The independent watchdog restored automatic fan control. Reapply the setting to resume.", "獨立監護程序已恢復風扇自動控制，請重新套用設定。" } },
            { "CPU 温度采样失效，已请求恢复 BIOS 自动控制。", new[] { "CPU temperature samples are unavailable. BIOS automatic recovery was requested.", "CPU 溫度取樣失效，已請求恢復 BIOS 自動控制。" } },
            { "CPU 温度过高或采样中断，已启用保护全速。点击 BIOS 自动可恢复。", new[] { "CPU temperature is too high or sampling stopped. Protective full speed is enabled; select BIOS auto to restore automatic control.", "CPU 溫度過高或取樣中斷，已啟用保護全速。點選 BIOS 自動可恢復。" } },
            { "程序资源持续超限，已恢复 BIOS 自动调速并降低界面刷新频率。", new[] { "Resource limits were exceeded. BIOS automatic control was restored and the interface refresh rate was reduced.", "程式資源持續超限，已恢復 BIOS 自動調速並降低介面更新頻率。" } },
            { "实际风扇转速", new[] { "Actual fan speed", "實際風扇轉速" } },
            { "核心最高 / 平均", new[] { "Core max / average", "核心最高 / 平均" } },
            { "概览与控制", new[] { "Overview & control", "概覽與控制" } },
            { "传感器详情", new[] { "Sensor details", "感測器詳情" } },
            { "最低转速", new[] { "Minimum speed", "最低轉速" } },
            { "{0}°C 起升 · {1}°C 全速", new[] { "Ramp {0}°C · full {1}°C", "{0}°C 起升 · {1}°C 全速" } },
            { "探测仅检查约 15 秒转速响应，结束后恢复保存配置；不是自动校准。", new[] { "The probe checks fan response for about 15 seconds, then restores the saved profile. It does not calibrate the fan.", "探測僅檢查約 15 秒轉速回應，結束後恢復儲存設定；不是自動校準。" } },
            { "恢复 BIOS 自动控制？", new[] { "Restore BIOS automatic control?", "恢復 BIOS 自動控制？" } },
            { "确认恢复", new[] { "Confirm restore", "確認恢復" } },
            { "后台断连 · 最后读数已过期，控制状态无法确认", new[] { "Disconnected · last readings are stale; control status is unknown", "背景斷線 · 最後讀數已過期，控制狀態無法確認" } },
            { "读数已过期 · 等待后台新快照", new[] { "Stale readings · waiting for a new service snapshot", "讀數已過期 · 等待背景服務新快照" } },
            { "服务正在初始化，控制暂不可用", new[] { "The service is initializing; controls are temporarily unavailable", "服務正在初始化，控制暫時無法使用" } },
            { "当前机型 / BIOS 未提供手动 RPM 控制", new[] { "Manual RPM control is unavailable for this model / BIOS", "目前機型 / BIOS 未提供手動 RPM 控制" } },
            { "保存失败，输入已保留，可重试。", new[] { "Save failed. Your input is retained; try again.", "儲存失敗，輸入已保留，可重試。" } },
            { "后台已连接", new[] { "Service connected", "背景服務已連線" } },
            { "重试连接", new[] { "Reconnect", "重試連線" } },
            { "离线预览", new[] { "Offline preview", "離線預覽" } },
            { "后台 CPU {0:0.00}% · 工作集 {1:0.0} MiB · {2}", new[] { "Background CPU {0:0.00}% · Working set {1:0.0} MiB · {2}", "背景 CPU {0:0.00}% · 工作集 {1:0.0} MiB · {2}" } },
            { "风扇", new[] { "Fan", "風扇" } },
            { "等待快照", new[] { "Waiting for snapshot", "等待快照" } },
            { "已过期", new[] { "Stale", "已過期" } },
            { "后台断连", new[] { "Disconnected", "背景斷線" } },
            { "更新 {0}", new[] { "Updated {0}", "更新 {0}" } },
            { "温控运行", new[] { "Thermal control active", "溫控執行中" } },
            { "正在探测", new[] { "Probing", "正在探測" } },
            { "故障暂停", new[] { "Paused after error", "故障暫停" } },
            { "保护全速", new[] { "Protective full speed", "保護全速" } },
            { "等待温度", new[] { "Waiting for CPU", "等待溫度" } },
            { "白色", new[] { "White", "白色" } },
            { "紫黑色", new[] { "Purple and black", "紫黑色" } },
            { "打开语言文件夹", new[] { "Open language folder", "開啟語言資料夾" } },
            { "打开皮肤文件夹", new[] { "Open skin folder", "開啟面板外觀資料夾" } },
            { "扩展文件无效（使用内置设置）", new[] { "Invalid extension file (using built-in settings)", "擴充檔案無效（使用內建設定）" } },
            { "重新加载皮肤", new[] { "Reload skins", "重新載入面板外觀" } },
            { "扩展资源", new[] { "Extension resources", "擴充資源" } },
            { "已忽略：{0}（{1}）", new[] { "Ignored: {0} ({1})", "已忽略：{0}（{1}）" } },
            { "后台控速程序未运行。", new[] { "The background controller is not running.", "背景控速程式未執行。" } },
            { "主页", new[] { "Home", "主頁" } },
            { "详情", new[] { "Details", "詳情" } },
            { "CPU 温度", new[] { "CPU temperature", "CPU 溫度" } },
            { "最热硬盘", new[] { "Hottest disk", "最熱硬碟" } },
            { "目标 {0} RPM", new[] { "Target {0} RPM", "目標 {0} RPM" } },
            { "核心最高 {0}°C · 平均 {1}°C", new[] { "Hottest core {0}°C · average {1}°C", "核心最高 {0}°C · 平均 {1}°C" } },
            { "应用", new[] { "Apply", "套用" } },
            { "本次最低 / 最高", new[] { "Session min / max", "本次最低 / 最高" } },
            { "本次最低 / 最高 {0} / {1}", new[] { "Session min / max {0} / {1}", "本次最低 / 最高 {0} / {1}" } },
            { "本次最高", new[] { "Session peak", "本次最高" } },
            { "本次最高 {0}°C", new[] { "Session peak {0}°C", "本次最高 {0}°C" } },
            { "最近 10 分钟", new[] { "Last 10 minutes", "最近 10 分鐘" } },
            { "10 分钟前", new[] { "10 min ago", "10 分鐘前" } },
            { "现在", new[] { "Now", "現在" } },
            { "等待数据", new[] { "Waiting for data", "等待資料" } },
            { "目标", new[] { "Target", "目標" } },
            { "实际", new[] { "Actual", "實際" } },
            { "控制状态", new[] { "Control state", "控制狀態" } },
            { "硬盘", new[] { "Disk", "硬碟" } },
            { "型号", new[] { "Model", "型號" } },
            { "事件", new[] { "Events", "事件" } },
            { "暂无事件", new[] { "No events yet", "暫無事件" } },
            { "DellFans 进程合计", new[] { "DellFans processes total", "DellFans 程序合計" } },
            { "内存", new[] { "Memory", "記憶體" } },
            { "{0} 个进程", new[] { "{0} processes", "{0} 個程序" } },
            { "外观", new[] { "Appearance", "外觀" } },
            { "更多", new[] { "More", "更多" } },
            { "通用", new[] { "General", "通用" } },
            { "显示 deck", new[] { "Show deck", "顯示 deck" } },
            { "在桌面角落显示转速与温度的小卡片，由托盘程序提供。", new[] { "A small card with fan speed and temperature in the corner of the desktop, provided by the tray program.", "在桌面角落顯示轉速與溫度的小卡片，由系統匣程式提供。" } },
            { "登录后启动", new[] { "Start at sign-in", "登入後啟動" } },
            { "登录 Windows 后自动运行托盘程序。", new[] { "Run the tray program automatically after you sign in to Windows.", "登入 Windows 後自動執行系統匣程式。" } },
            { "工具", new[] { "Tools", "工具" } },
            { "关于", new[] { "About", "關於" } },
            { "版本 {0}", new[] { "Version {0}", "版本 {0}" } },
            { "黑色", new[] { "Black", "黑色" } },
            { "DellFans 与 Dell Inc. 无关；Dell 是其所有者的商标。", new[] { "DellFans is not affiliated with Dell Inc. Dell is a trademark of its owner.", "DellFans 與 Dell Inc. 無關；Dell 是其所有者的商標。" } },
            { "初始化中", new[] { "Initializing", "初始化中" } },
            { "模式切换为 {0}", new[] { "Mode changed to {0}", "模式切換為 {0}" } },
            { "控制状态：{0}", new[] { "Control state: {0}", "控制狀態：{0}" } },
            { "关闭", new[] { "Close", "關閉" } },
            { "BIOS 控制 · 选择模式后立即生效", new[] { "BIOS in control · a mode takes effect as soon as you choose it", "BIOS 控制 · 選擇模式後立即生效" } },
            { "关闭窗口后继续按已保存模式运行", new[] { "keeps running the saved mode after the window closes", "關閉視窗後繼續依已儲存模式執行" } },
            // PawnIO driver notice, consent and install result
            { "CPU 温度驱动（PawnIO）未安装", new[] { "CPU temperature driver (PawnIO) is not installed", "CPU 溫度驅動（PawnIO）未安裝" } },
            { "CPU 温度驱动（PawnIO）需要重新安装", new[] { "CPU temperature driver (PawnIO) needs reinstalling", "CPU 溫度驅動（PawnIO）需要重新安裝" } },
            { "当前使用 Intel ESIF，精度较低，仍可控速", new[] { "Using Intel ESIF: lower accuracy, fan control still works", "目前使用 Intel ESIF，精度較低，仍可控速" } },
            { "无法读取 CPU 温度，风扇保持 BIOS 自动", new[] { "CPU temperature cannot be read; the fan stays on BIOS automatic", "無法讀取 CPU 溫度，風扇保持 BIOS 自動" } },
            { "安装驱动…", new[] { "Install driver…", "安裝驅動…" } },
            { "以后再说", new[] { "Not now", "以後再說" } },
            { "驱动已安装，重启电脑后生效", new[] { "Driver installed; restart the PC to activate it", "驅動已安裝，重新啟動電腦後生效" } },
            { "正在安装…", new[] { "Installing…", "正在安裝…" } },
            { "请在弹出的管理员确认窗口中选择“是”。", new[] { "Choose Yes in the administrator prompt.", "請在彈出的管理員確認視窗中選擇「是」。" } },
            { "驱动已安装，CPU 温度将在几秒内恢复读取。", new[] { "Driver installed. CPU temperature readings resume in a few seconds.", "驅動已安裝，CPU 溫度將在幾秒內恢復讀取。" } },
            { "驱动已安装，重启电脑后生效。", new[] { "Driver installed. Restart the PC for it to take effect.", "驅動已安裝，重新啟動電腦後生效。" } },
            { "已取消：未授予管理员权限，没有安装任何内容。", new[] { "Cancelled: administrator permission was not granted, nothing was installed.", "已取消：未授予管理員權限，沒有安裝任何內容。" } },
            { "安装失败：{0}", new[] { "Installation failed: {0}", "安裝失敗：{0}" } },
            { "安装失败（退出码 {0}）。", new[] { "Installation failed (exit code {0}).", "安裝失敗（結束碼 {0}）。" } },
            { "安装 CPU 温度驱动（PawnIO）？", new[] { "Install the CPU temperature driver (PawnIO)?", "安裝 CPU 溫度驅動（PawnIO）？" } },
            { "安装", new[] { "Install", "安裝" } },
            { "将安装 PawnIO {0} 内核驱动（作者 namazso，官方签名发行版），DellFans 用它读取 CPU 温度（MSR）。\n\n下载地址：{1}\n\n校验：下载后先核对 SHA-256 指纹，再验证 Authenticode 签名者为 CN={2}；任何一项不符，安装程序都不会运行。\n\n许可证：驱动为 GPL-2.0，不随 DellFans 分发，只在你同意后从上述官方地址下载。\n\n需要管理员权限，系统会弹出确认窗口。安装后可在 Windows“应用和功能”中卸载。",
                new[] { "This installs the PawnIO {0} kernel driver (by namazso, the official signed release). DellFans uses it to read the CPU temperature (MSR).\n\nDownload from: {1}\n\nChecks: the download must match the pinned SHA-256 fingerprint and carry an Authenticode signature from CN={2}. If either check fails, the installer is not run.\n\nLicense: the driver is GPL-2.0 and is not bundled with DellFans; it is downloaded from the official address above only after you agree.\n\nNeeds administrator rights; Windows will ask you to confirm. You can uninstall it later from Windows Apps and features.",
                    "將安裝 PawnIO {0} 核心驅動（作者 namazso，官方簽章發行版），DellFans 用它讀取 CPU 溫度（MSR）。\n\n下載位址：{1}\n\n驗證：下載後先核對 SHA-256 指紋，再驗證 Authenticode 簽章者為 CN={2}；任何一項不符，安裝程式都不會執行。\n\n授權：驅動為 GPL-2.0，不隨 DellFans 散布，只在你同意後從上述官方位址下載。\n\n需要系統管理員權限，系統會彈出確認視窗。安裝後可在 Windows「應用程式與功能」中解除安裝。" } }
        };
        public static string T(string source)
        {
            if (source != null && activePack != null)
            {
                string translated;
                if (activePack.Translations.TryGetValue(source, out translated)) return translated;
                string[] english; return text.TryGetValue(source, out english) ? english[0] : source;
            }
            if (source == null || language == "zh-Hans") return source;
            string[] value; return text.TryGetValue(source, out value) ? value[language == "en" ? 0 : 1] : source;
        }
        public static string F(string source, params object[] args)
        {
            try { return string.Format(CultureInfo.CurrentCulture, T(source), args); }
            catch (FormatException)
            {
                if (activePack == null) throw;
                string[] english;
                return string.Format(CultureInfo.CurrentCulture, text.TryGetValue(source, out english) ? english[0] : source, args);
            }
        }
        public static string Error(string message)
        {
            string first = "";
            foreach (string line in (message ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.Trim().Length != 0) { first = line.Trim(); break; }
            if (text.ContainsKey(first)) return T(first);
            string[] prefixes = { "配置无效，未启用接管：",
                "后台温控异常，已临时恢复 BIOS 自动，配置已保留：", "转速控制异常，已临时恢复 BIOS 自动：" };
            foreach (string prefix in prefixes)
                if (first.StartsWith(prefix, StringComparison.Ordinal)) return T(prefix).TrimEnd('：', ':');
            return T("操作未完成，请导出诊断数据查看详情。");
        }
        public static void Apply(Control root)
        {
            if (!(root is TextBoxBase) && !(root is UpDownBase)) root.Text = T(root.Text);
            foreach (Control child in root.Controls) Apply(child);
        }
    }
}
