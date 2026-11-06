using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using DellFans;

internal static class LocalizationTests
{
    static int passed;
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); passed++; }
    static void CultureResolution()
    {
        string[,] cases = {
            { "zh-CN", "zh-Hans" }, { "zh-SG", "zh-Hans" }, { "zh-Hans", "zh-Hans" }, { "zh-CHS", "zh-Hans" },
            { "zh-TW", "zh-Hant" }, { "zh-HK", "zh-Hant" }, { "zh-MO", "zh-Hant" }, { "zh-Hant", "zh-Hant" }, { "zh-CHT", "zh-Hant" },
            { "zh-Hans-HK", "zh-Hans" }, { "zh-Hant-CN", "zh-Hant" }, { "ZH-hAnT-tW", "zh-Hant" },
            { "en-US", "en" }, { "en-GB", "en" }, { "fr-FR", "en" }, { "ja-JP", "en" }, { "zhfoo", "en" }, { "zh-bogus", "en" }, { null, "en" }, { "", "en" }
        };
        for (int i = 0; i < cases.GetLength(0); i++)
            Check(UiText.Resolve(cases[i, 0]) == cases[i, 1], "Culture resolves incorrectly: " + (cases[i, 0] ?? "<null>"));
        UiText.Select("zh-TW"); Check(UiText.Language == "zh-Hant", "Select must use the same culture boundary");
        Console.WriteLine("PASS Chinese script/region boundaries and English fallback");
    }
    static void ControlsKeepInput()
    {
        UiText.Select("en-US");
        using (Form form = new Form { Text = "探测转速响应" })
        using (Panel panel = new Panel())
        using (Label label = new Label { Text = "系统风扇" })
        using (Button button = new Button { Text = "保存" })
        using (TextBox input = new TextBox { Text = "保存" })
        using (RichTextBox rich = new RichTextBox { Text = "取消" })
        using (NumericUpDown number = new NumericUpDown { Maximum = 10000, DecimalPlaces = 2, Value = 2500.25M })
        {
            form.Controls.Add(panel); panel.Controls.Add(label); panel.Controls.Add(button);
            panel.Controls.Add(input); panel.Controls.Add(rich); panel.Controls.Add(number);
            string numericText = number.Text;
            UiText.Apply(form);
            Check(form.Text == "Fan probe" && label.Text == "System fan" && button.Text == "Save", "Apply must translate captions recursively");
            Check(input.Text == "保存" && rich.Text == "取消", "Apply changed an editable text value");
            Check(number.Value == 2500.25M && number.Text == numericText, "Apply changed a numeric value or its editing text");
        }
        Console.WriteLine("PASS recursive caption translation preserves text and numeric input");
    }
    static void ErrorPresentation()
    {
        UiText.Select("en");
        string raw = "\r\n  \n等待有效 CPU 温度采样后再应用设置。\r\nParameter name: temperature";
        Check(UiText.Error(raw) == "Wait for a valid CPU temperature sample before applying settings.", "Leading blank lines hid the useful localized error");
        Check(UiText.Error("后台控速程序未运行。") == "The background controller is not running.", "A controller connection error was not presented in English");
        Check(UiText.Error("配置无效，未启用接管：C:\\diagnostics\\fan-settings.json: raw detail") == "The configuration is invalid; fan control was not enabled.", "A wrapped configuration error was not localized safely");
        Check(UiText.Error("\r\nUnknown provider failure C:\\diagnostics\\fan.json") == "Unable to complete this operation. Export diagnostic data for details.", "Unknown diagnostics need a localized overview");
        Check(UiText.Error(null) == "Unable to complete this operation. Export diagnostic data for details.", "An empty error needs a useful overview");
        UiText.Select("zh-Hant");
        Check(UiText.Error(raw) == "等待有效 CPU 溫度取樣後再套用設定。", "The useful error was not translated into Traditional Chinese");
        Check(UiText.Error("后台操作超时，请读取状态确认结果。") == "背景操作逾時，請讀取狀態確認結果。", "Operation timeout was not translated into Traditional Chinese");
        UiText.Select("zh-Hans");
        Check(UiText.Error(raw) == "等待有效 CPU 温度采样后再应用设置。", "Simplified Chinese must retain the useful first error line");
        Console.WriteLine("PASS known service errors, leading blank lines and unknown-diagnostic summaries");
    }
    static void CompactPanelPresentation()
    {
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            UiText.Select("en-US");
            Check(UiText.T("实际风扇转速") == "Actual fan speed", "The compact fan card is not translated");
            Check(UiText.T("核心最高 / 平均") == "Core max / average", "The compact temperature cards are not translated");
            Check(UiText.T("概览与控制") == "Overview & control" && UiText.T("传感器详情") == "Sensor details", "Compact navigation is not translated");
            Check(UiText.F("{0} · {1:N0}–{2:N0} RPM · {3}°C 起调", UiText.T("静音"), 1500, 3000, 55) == "Quiet · 1,500–3,000 RPM · starts at 55°C", "Mode range formatting is not translated");
            Check(UiText.T("不可用") == "Unavailable" && UiText.T("等待采样") == "Pending", "Missing readings are not translated");
            Check(UiText.T("后台断连 · 最后读数已过期，控制状态无法确认") == "Disconnected · last readings are stale; control status is unknown", "Disconnection must not imply confirmed control");
            Check(UiText.T("将交回 BIOS 自动控制，服务不再接管风扇。已保存的自定义最低转速会保留。") == "Control returns to the BIOS and the service stops taking over the fan. The saved custom minimum is kept.", "The automatic-control confirmation does not explain the configuration change");
            Check(UiText.T("保存失败，输入已保留，可重试。") == "Save failed. Your input is retained; try again.", "Save failure must explain retained edits");
            Check(UiText.T("探测仅检查约 15 秒转速响应，结束后恢复保存配置；不是自动校准。") == "The probe checks fan response for about 15 seconds, then restores the saved profile. It does not calibrate the fan.", "The probe must not be presented as automatic calibration");
            string device = @"Dell Inc. / NVMe #1 / C:\diagnostics\fan.json";
            Check(UiText.T(device) == device, "Raw device names and technical paths must remain unchanged");
            UiText.Select("zh-Hant");
            Check(UiText.T("实际风扇转速") == "實際風扇轉速" && UiText.T("重试连接") == "重試連線", "Traditional Chinese cards and reconnection are incomplete");
            Check(UiText.T("后台断连 · 最后读数已过期，控制状态无法确认") == "背景斷線 · 最後讀數已過期，控制狀態無法確認", "Traditional Chinese disconnection is incomplete");
            Check(UiText.T("将交回 BIOS 自动控制，服务不再接管风扇。已保存的自定义最低转速会保留。") == "將交回 BIOS 自動控制，服務不再接管風扇。已儲存的自訂最低轉速會保留。", "Traditional Chinese confirmation is incomplete");
            UiText.Select("zh-Hans");
            Check(UiText.T("保存失败，输入已保留，可重试。") == "保存失败，输入已保留，可重试。", "Simplified Chinese source text must be retained");
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
        Console.WriteLine("PASS compact cards, resource formatting, missing data, confirmations and disconnected state");
    }
    static string InitialLanguage() { return UiText.Language; }
    static void AutomaticSystemLanguage()
    {
        string[,] cases = { { "zh-CN", "zh-Hans" }, { "zh-HK", "zh-Hant" }, { "en-GB", "en" }, { "de-DE", "en" } };
        for (int i = 0; i < cases.GetLength(0); i++)
        {
            ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath, "--automatic " + cases[i, 0]) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process child = Process.Start(start))
            {
                if (!child.WaitForExit(5000)) { child.Kill(); throw new Exception("Automatic-language test did not finish"); }
                Check(child.ExitCode == 0 && child.StandardOutput.ReadToEnd().Trim() == cases[i, 1], "Initial language ignored CurrentUICulture: " + cases[i, 0]);
            }
        }
        Console.WriteLine("PASS initial UI language follows system UI culture in fresh processes");
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--automatic")
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(args[1]);
                Console.WriteLine(InitialLanguage());
                return 0;
            }
            AutomaticSystemLanguage();
            CultureResolution();
            ControlsKeepInput();
            ErrorPresentation();
            CompactPanelPresentation();
            Console.WriteLine("RESULT " + passed + " checks passed; localization and offline controls only");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
    }
}
