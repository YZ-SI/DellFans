using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Windows.Forms;
using DellFans;

internal static class ExternalLanguageTests
{
    static int passed;
    static string directory;
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); passed++; }
    static void Write(string file, string json)
    { File.WriteAllText(Path.Combine(directory, file), json, new UTF8Encoding(false)); }
    static void Reset()
    {
        foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
        UiText.InitializePacks(directory);
    }
    static string Pack(string culture, string displayName, string key, object value)
    {
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
            { "schemaVersion", 1 }, { "culture", culture }, { "displayName", displayName },
            { "translations", new Dictionary<string, object> { { key, value } } }
        });
    }
    static void LoadMatchingPack()
    {
        Write("fr.json", "{\"schemaVersion\":1,\"culture\":\"fr\",\"displayName\":\"Français\",\"translations\":{\"保存\":\"Enregistrer\"}}");
        Check(UiText.InitializePacks(directory).Length == 0, "A valid French pack was rejected");
        UiText.Select("fr-FR");
        Check(UiText.Language == "fr" && UiText.T("保存") == "Enregistrer", "The French system culture did not use the neutral French pack");
        Check(UiText.T("取消") == "Cancel", "Missing external translations must fall back to English");
        Console.WriteLine("PASS valid local pack, language fallback, and English missing-key fallback");
    }
    static void FormatArgumentsArePreserved()
    {
        Write("fr.json", "{\"schemaVersion\":1,\"culture\":\"fr\",\"displayName\":\"Français\",\"translations\":{\"更新 {0}\":\"Mis à jour {1}\"}}");
        Check(UiText.InitializePacks(directory).Length == 1, "A translation changed its argument index without being rejected");
        UiText.Select("fr-FR");
        Check(UiText.F("更新 {0}", "12:34") == "Updated 12:34", "Rejected format must use the built-in English message");
        Write("fr.json", "{\"schemaVersion\":1,\"culture\":\"fr\",\"displayName\":\"Français\",\"translations\":{\"后台 CPU {0:0.00}% · 工作集 {1:0.0} MiB · {2}\":\"CPU {0:Q}% {1:0.0} MiB {2}\"}}");
        Check(UiText.InitializePacks(directory).Length == 1, "A translation changed a numeric format to an unsafe specifier");
        Write("fr.json", "{\"schemaVersion\":1,\"culture\":\"fr\",\"displayName\":\"Français\",\"translations\":{\"更新 {0}\":\"{0} : mise à jour\"}}");
        Check(UiText.InitializePacks(directory).Length == 0 && UiText.F("更新 {0}", "12:34") == "12:34 : mise à jour", "A translation could not reorder a preserved placeholder");
        Console.WriteLine("PASS rejected argument/format changes and accepted preserved formatting");
    }
    static void TranslatorCanExportAndValidate()
    {
        string template = UiText.ExportTemplate("de", "Deutsch");
        Check(UiText.ValidatePack(template).Length == 0, "The exported English source template could not be validated");
        DictionaryCheck(template);
        Write("de.json", template);
        Check(UiText.InitializePacks(directory).Length == 0, "An exported template could not be loaded");
        UiText.Select("de-DE");
        Check(UiText.T("保存") == "Save", "The translator template is not filled with English source text");
        Check(UiText.ValidatePack("{broken").Length > 0, "Malformed JSON was not reported by the public validator");
        Check(UiText.PacksDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DellFans", "Languages"), "The default language directory is not per-user");
        Console.WriteLine("PASS translator export round-trip, public validation, and per-user default path");
    }
    // a bad external file (including malformed \u escapes, which throw FormatException) skips only that file and is listed.
    static void IgnoredFilesAreListedAndLocalized()
    {
        Reset(); Write("de.json", Pack("de", "Deutsch", "保存", "Speichern"));
        Write("broken.json", "{\"schemaVersion\":1,\"culture\":\"fr\",\"displayName\":\"\\u00\",\"translations\":{}}");
        Write("fr.json", "{broken");
        string[] diagnostics = UiText.InitializePacks(directory);
        Check(diagnostics.Length == 2, "Two bad files must produce two diagnostics");
        UiText.Select("de-DE"); Check(UiText.T("保存") == "Speichern", "A malformed neighbor prevented a valid pack");
        IgnoredFile[] ignored = UiText.IgnoredFiles;
        Check(ignored.Length == 2 && ignored.Any(i => i.FileName == "broken.json" && i.Reason.Length > 0) && ignored.Any(i => i.FileName == "fr.json"),
            "The ignored list must hold (file name, reason) for exactly the bad files");
        IgnoredFile one = new IgnoredFile { FileName = "x.json", Reason = "Invalid theme JSON." };
        UiText.SetIgnored("languages", new[] { one });
        UiText.Select("zh-Hans"); Check(UiText.IgnoredSummary() == "已忽略：x.json（Invalid theme JSON.）", "zh-CN summary: " + UiText.IgnoredSummary());
        UiText.Select("zh-Hant"); Check(UiText.IgnoredSummary() == "已忽略：x.json（Invalid theme JSON.）", "zh-TW summary: " + UiText.IgnoredSummary());
        UiText.Select("en"); Check(UiText.IgnoredSummary() == "Ignored: x.json (Invalid theme JSON.)", "en summary: " + UiText.IgnoredSummary());
        UiText.SetIgnored("languages", null);
        Check(UiText.IgnoredSummary() == "", "No ignored files means no summary");
        Reset(); UiText.InitializePacks(directory);
        Console.WriteLine("PASS bad language files (including malformed escapes) are skipped alone and listed as 已忽略：文件名（原因） in zh-CN/zh-TW/en");
    }
    static void DictionaryCheck(string json)
    {
        var root = new JavaScriptSerializer().DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
        var translations = root["translations"] as System.Collections.Generic.Dictionary<string, object>;
        Check((int)root["schemaVersion"] == 1 && (string)root["culture"] == "de" && (string)root["displayName"] == "Deutsch", "Template metadata did not preserve the translator's culture/display name");
        Check((string)translations["更新 {0}"] == "Updated {0}", "The template lost the source format placeholder");
    }
    static void ExtensionMenuCaptions()
    {
        string[,] captions = {
            { "白色", "White", "白色" }, { "黑色", "Black", "黑色" }, { "紫黑色", "Purple and black", "紫黑色" },
            { "打开语言文件夹", "Open language folder", "開啟語言資料夾" }, { "打开皮肤文件夹", "Open skin folder", "開啟面板外觀資料夾" },
            { "扩展文件无效（使用内置设置）", "Invalid extension file (using built-in settings)", "擴充檔案無效（使用內建設定）" },
            { "重新加载皮肤", "Reload skins", "重新載入面板外觀" },
            { "扩展资源", "Extension resources", "擴充資源" }
        };
        for (int i = 0; i < captions.GetLength(0); i++)
        {
            UiText.Select("en-US"); Check(UiText.T(captions[i, 0]) == captions[i, 1], "Missing English extension caption: " + captions[i, 0]);
            UiText.Select("zh-Hant"); Check(UiText.T(captions[i, 0]) == captions[i, 2], "Missing Traditional Chinese extension caption: " + captions[i, 0]);
            UiText.Select("zh-Hans"); Check(UiText.T(captions[i, 0]) == captions[i, 0], "Simplified Chinese extension caption changed: " + captions[i, 0]);
        }
        Console.WriteLine("PASS extension menu captions in all three built-in languages");
    }
    static void FallbackNeverBorrowsAnUnmatchedPack()
    {
        foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
        Write("en.json", "{\"schemaVersion\":1,\"culture\":\"en\",\"displayName\":\"Custom English\",\"translations\":{\"保存\":\"Custom Save\"}}");
        Write("zh-Hans.json", "{\"schemaVersion\":1,\"culture\":\"zh-Hans\",\"displayName\":\"Custom Chinese\",\"translations\":{\"保存\":\"Custom Chinese Save\"}}");
        Check(UiText.InitializePacks(directory).Length == 0, "Valid built-in culture overrides were rejected");
        UiText.Select("de-DE"); Check(UiText.T("保存") == "Save", "An unmatched German culture borrowed the custom English pack");
        UiText.Select("zh-CN"); Check(UiText.T("保存") == "保存", "A region-only Chinese culture borrowed an unmatched script pack");
        UiText.Select("en-GB"); Check(UiText.T("保存") == "Custom Save", "A matching English culture did not use the neutral English pack");
        UiText.Select("zh-Hans"); Check(UiText.T("保存") == "Custom Chinese Save", "An exact script culture did not use its pack");
        Console.WriteLine("PASS built-in fallback only uses external packs matching the requested culture");
    }
    static void ExactCultureAndReload()
    {
        Reset();
        Write("fr.json", Pack("fr", "Français", "保存", "Enregistrer"));
        Write("fr-ca.json", Pack("fr-CA", "Français canadien", "保存", "Sauvegarder"));
        Check(UiText.InitializePacks(directory).Length == 0, "Exact and neutral packs were rejected");
        UiText.Select("FR-ca"); Check(UiText.Language == "fr-CA" && UiText.T("保存") == "Sauvegarder", "Exact culture did not win over neutral language");
        UiText.Select("fr-FR"); Check(UiText.T("保存") == "Enregistrer", "A different French region did not use the neutral pack");
        string device = "Dell ACPI C:\\diagnostics\\fan.json";
        Check(UiText.T(device) == device, "An unknown technical identifier was changed by an external language");
        using (Label label = new Label { Text = "保存" })
        using (TextBox input = new TextBox { Text = "保存" })
        using (NumericUpDown number = new NumericUpDown { Maximum = 10000, Value = 2500 })
        {
            UiText.Apply(label); UiText.Apply(input); UiText.Apply(number);
            Check(label.Text == "Enregistrer" && input.Text == "保存" && number.Value == 2500, "External translation changed editing text or numeric input");
        }
        Write("fr.json", "{broken");
        Check(UiText.InitializePacks(directory).Length == 1 && UiText.T("保存") == "Save", "Invalid reload did not re-resolve the selected culture to English");
        Check(UiText.Resolve("fr-FR") == "en" && UiText.Resolve("fr-CA") == "fr-CA", "A regional pack leaked to a different region");
        string[] copy = UiText.PackDiagnostics; copy[0] = "changed";
        Check(UiText.PackDiagnostics[0] != "changed", "A caller could alter retained pack diagnostics");
        Write("fr.json", Pack("fr", "Français", "保存", "Rétabli"));
        Check(UiText.InitializePacks(directory).Length == 0 && UiText.T("保存") == "Rétabli", "Reload lost the original requested culture");
        Check(UiText.ValidatePack(Pack("de", "Deutsch", "保存", "Speichern")).Length == 0 && UiText.T("保存") == "Rétabli", "Validation changed the active language");
        Console.WriteLine("PASS exact culture precedence, safe reload, immutable diagnostics, and editing text preservation");
    }
    static void InvalidContentIsRejected()
    {
        string[] invalid = {
            "{broken", "{}", Pack("fr", "Français", "保存", "Save").Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            Pack("../fr", "Français", "保存", "Save"), Pack("fr", "<script>", "保存", "Save"), Pack("fr", "line\nname", "保存", "Save"),
            Pack("fr", null, "保存", "Save"), Pack("fr", "Français", "unknown message ID", "Save"), Pack("fr", "Français", "保存", null),
            Pack("fr", "Français", "保存", 123), Pack("fr", "Français", "保存", "<b>Save</b>"), Pack("fr", "Français", "保存", "Save\0"),
            Pack("fr", "Français", "保存", new string('x', 4097)), Pack("fr", "Français", "更新 {0}", "Updated {0"),
            Pack("fr", "Français", "更新 {0}", "Updated {0} {0}"), Pack("fr", "Français", "更新 {0}", "Updated"),
            Pack("fr", "Français", "更新 {0}", "Updated {0,999}"), Pack("fr", "Français", "保存", "Save").Replace("\"culture\":", "\"extra\":true,\"culture\":")
        };
        for (int i = 0; i < invalid.Length; i++) Check(UiText.ValidatePack(invalid[i]).Length > 0, "Invalid pack was accepted, case " + i);
        Reset(); Write("fr.json", "{broken"); Write("de.json", Pack("de", "Deutsch", "保存", "Speichern"));
        Check(UiText.InitializePacks(directory).Length == 1, "One broken pack prevented independent loading or was not diagnosed");
        UiText.Select("de-DE"); Check(UiText.T("保存") == "Speichern", "A broken neighboring pack prevented a valid language from loading");
        UiText.Select("zh-HK"); Check(UiText.T("保存") == "儲存", "Invalid external content changed the existing Traditional Chinese rule");
        Console.WriteLine("PASS invalid schema, metadata, markup, unknown IDs, values, formats, and independent valid files");
    }
    static void FileBoundariesAreEnforced()
    {
        Reset();
        string json = Pack("fr", "Français", "保存", "Enregistrer");
        string atLimit = json + new string(' ', 65536 - Encoding.UTF8.GetByteCount(json));
        Write("fr.json", atLimit);
        Check(UiText.InitializePacks(directory).Length == 0, "A pack exactly at 64 KiB was rejected");
        Write("fr.json", atLimit + " ");
        Check(UiText.InitializePacks(directory).Length == 1, "A file larger than 64 KiB was accepted");
        Check(UiText.ValidatePack(atLimit + " ").Length == 1, "The validator did not enforce the same byte limit as the file loader");
        Reset(); Write("a.json", json); Write("b.json", json); Write("c.json", json);
        Check(UiText.InitializePacks(directory).Length == 2, "Duplicate cultures were not diagnosed");
        UiText.Select("fr-FR"); Check(UiText.T("保存") == "Save", "Ambiguous duplicate culture packs were applied");
        Reset();
        for (int i = 0; i < 33; i++) Write(i.ToString("00") + ".json", json);
        Check(UiText.InitializePacks(directory).Length == 1 && UiText.T("保存") == "Save", "More than 32 packs were not bounded and ignored");
        Reset();
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "nested", "fr.json"), json);
        Check(UiText.InitializePacks(directory).Length == 0 && UiText.T("保存") == "Save", "The loader searched outside the top-level language folder");
        Check(UiText.InitializePacks(@"\\example.invalid\languages").Length == 1, "A UNC path was not rejected before file access");
        Console.WriteLine("PASS exact byte bound, duplicate cultures, file-count bound, top-level-only files, and rejected UNC paths");
    }
    static int Main(string[] args)
    {
        directory = Path.Combine(Path.GetTempPath(), "DellFans-LanguageCase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            LoadMatchingPack();
            FormatArgumentsArePreserved();
            TranslatorCanExportAndValidate();
            ExtensionMenuCaptions();
            FallbackNeverBorrowsAnUnmatchedPack();
            ExactCultureAndReload();
            InvalidContentIsRejected();
            FileBoundariesAreEnforced();
            IgnoredFilesAreListedAndLocalized();
            if (args.Length == 1)
            {
                File.WriteAllText(args[0], UiText.ExportTemplate("en", "English source template"), new UTF8Encoding(false));
                Console.WriteLine("Exported English source template: " + new FileInfo(args[0]).Length + " bytes");
            }
            Console.WriteLine("RESULT " + passed + " checks passed; public resource APIs and temporary local files only");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
        finally { UiText.InitializePacks(Path.Combine(directory, "missing")); Directory.Delete(directory, true); }
    }
}
