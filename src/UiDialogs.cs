using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DellFans
{
    // Every modal window of the interface is a SkinnedDialog (borderless, painted in the skin's palette).
    public static class UiDialogs
    {
        // Tests answer the confirmation without opening a window.
        internal static Func<IWin32Window, bool> ConfirmOverride = null; // only tests assign it; the initializer silences CS0649
        // Tests receive the finished dialog instead of it being shown, drive it and return its result.
        internal static Func<Form, IWin32Window, DialogResult> Presenter = null;
        static DialogResult Present(SkinnedDialog dialog, IWin32Window owner)
        { using (dialog) return Presenter != null ? Presenter(dialog, owner) : dialog.ShowCentered(owner); }
        public static bool ConfirmAutomatic(IWin32Window owner)
        {
            if (ConfirmOverride != null) return ConfirmOverride(owner);
            return Present(CreateConfirmAutomatic(), owner) == DialogResult.OK;
        }
        // PawnIO is installed only after this consent. Tests answer it without opening a window.
        internal static Func<IWin32Window, bool> DriverConsentOverride = null;
        public static string DriverConsentMessage()
        {
            return UiText.F("将安装 PawnIO {0} 内核驱动（作者 namazso，官方签名发行版），DellFans 用它读取 CPU 温度（MSR）。\n\n下载地址：{1}\n\n校验：下载后先核对 SHA-256 指纹，再验证 Authenticode 签名者为 CN={2}；任何一项不符，安装程序都不会运行。\n\n许可证：驱动为 GPL-2.0，不随 DellFans 分发，只在你同意后从上述官方地址下载。\n\n需要管理员权限，系统会弹出确认窗口。安装后可在 Windows“应用和功能”中卸载。",
                SensorDriverInstaller.Version, SensorDriverInstaller.Url, SensorDriverInstaller.SignerCommonName);
        }
        // Enter and Escape always take the safe, closing button; a destructive or installing choice needs a deliberate click.
        static DialogChoice[] Confirm(string label, string name)
        {
            return new[] { new DialogChoice { Name = name, Text = label, Result = DialogResult.OK, Primary = true },
                new DialogChoice { Name = "CancelAutomaticButton", Text = UiText.T("取消"), Result = DialogResult.Cancel } };
        }
        internal static SkinnedDialog CreateConfirmAutomatic(float? scale = null)
        { return new SkinnedDialog("ConfirmAutomaticDialog", UiText.T("恢复 BIOS 自动控制？"), UiText.T("将交回 BIOS 自动控制，服务不再接管风扇。已保存的自定义最低转速会保留。"), Confirm(UiText.T("确认恢复"), "ConfirmAutomaticButton"), 1, 460, scale); }
        internal static SkinnedDialog CreateDriverConsent(float? scale = null)
        { return new SkinnedDialog("DriverConsentDialog", UiText.T("安装 CPU 温度驱动（PawnIO）？"), DriverConsentMessage(), Confirm(UiText.T("安装"), "DriverInstallButton"), 1, 540, scale); }
        internal static SkinnedDialog CreateMessage(string message, string title, float? scale = null)
        {
            return new SkinnedDialog("MessageDialog", title, message,
                new[] { new DialogChoice { Name = "CancelAutomaticButton", Text = UiText.T("关闭"), Result = DialogResult.Cancel, Primary = true } }, 0, 460, scale);
        }
        public const string IssuesUrl = "https://github.com/YZ-SI/DellFans/issues";
        // Tests answer the capability dialogs without opening a window.
        internal static Func<IWin32Window, bool> CapabilityConsentOverride = null;
        internal static Func<IWin32Window, string, bool> UnsupportedOverride = null;
        internal static SkinnedDialog CreateCapabilityConsent(float? scale = null)
        {
            return new SkinnedDialog("CapabilityConsentDialog", UiText.T("检测风扇控制接口？"),
                UiText.T("首次使用需要确认这台电脑的 Dell 风扇接口可以控制：风扇将全速运行约 15 秒，结束后自动交回 BIOS 控制。\n\n只读检查已通过。检测不通过时只生成报告，不改变任何设置。"),
                Confirm(UiText.T("开始检测"), "CapabilityTestButton"), 1, 500, scale);
        }
        internal static SkinnedDialog CreateUnsupported(string reportPath, float? scale = null)
        {
            string text = reportPath == null
                ? UiText.F("这台电脑的 Dell 风扇接口检测未通过，DellFans 不会控制风扇，风扇保持 BIOS 自动。\n\n欢迎到 {0} 反馈你的机型。", IssuesUrl)
                : UiText.F("这台电脑的 Dell 风扇接口检测未通过，DellFans 不会控制风扇，风扇保持 BIOS 自动。\n\n检测报告已保存到：\n{0}\n\n欢迎把报告提交到 {1}，帮助支持更多机型。报告不含序列号、用户名和计算机名。", reportPath, IssuesUrl);
            DialogChoice close = new DialogChoice { Name = "UnsupportedCloseButton", Text = UiText.T("关闭"), Result = DialogResult.OK, Primary = true };
            DialogChoice[] choices = reportPath == null ? new[] { close }
                : new[] { new DialogChoice { Name = "OpenFolderButton", Text = UiText.T("打开所在文件夹"), Result = DialogResult.Yes }, close };
            return new SkinnedDialog("UnsupportedDialog", UiText.T("当前电脑不支持"), text, choices, choices.Length - 1, 540, scale);
        }
        public static bool ConfirmCapabilityTest(IWin32Window owner)
        {
            if (CapabilityConsentOverride != null) return CapabilityConsentOverride(owner);
            return Present(CreateCapabilityConsent(), owner) == DialogResult.OK;
        }
        // True when the user asked to see the report in its folder.
        public static bool ShowUnsupported(IWin32Window owner, string reportPath)
        {
            if (UnsupportedOverride != null) return UnsupportedOverride(owner, reportPath);
            return Present(CreateUnsupported(reportPath), owner) == DialogResult.Yes;
        }
        internal static SkinnedDialog CreateExportDone(string path, float? scale = null)
        {
            return new SkinnedDialog("ExportDoneDialog", UiText.T("导出完成"), UiText.F("当前数据已导出到：\n{0}", path),
                new[] { new DialogChoice { Name = "OpenFolderButton", Text = UiText.T("打开所在文件夹"), Result = DialogResult.Yes },
                    new DialogChoice { Name = "ExportOkButton", Text = UiText.T("确定"), Result = DialogResult.OK, Primary = true } }, 1, 520, scale);
        }
        public static bool ConfirmDriverInstall(IWin32Window owner)
        {
            if (DriverConsentOverride != null) return DriverConsentOverride(owner);
            return Present(CreateDriverConsent(), owner) == DialogResult.OK;
        }
        public static void ShowMessage(string message, string title) { ShowMessage(null, message, title); }
        public static void ShowMessage(IWin32Window owner, string message, string title) { Present(CreateMessage(message, title), owner); }
        // True when the user asked to see the file in its folder.
        public static bool ShowExportDone(IWin32Window owner, string path)
        { return Present(CreateExportDone(path), owner) == DialogResult.Yes; }

        // Offline previews of every dialog in the current skin and language.
        internal static int RenderPreviews(string folder, float scale, string suffix)
        {
            Directory.CreateDirectory(folder);
            string path = @"C:\Users\Example\AppData\Local\DellFans\Reports\dellfans-20260101-120000-000.json";
            string report = @"C:\Users\Example\AppData\Local\DellFans\reports\capability-report-20260101T120000000Z.json";
            SkinnedDialog[] all = { CreateConfirmAutomatic(scale), CreateDriverConsent(scale), CreateMessage(UiText.Error("The service is not running."), UiText.T("导出失败"), scale), CreateExportDone(path, scale),
                CreateCapabilityConsent(scale), CreateUnsupported(report, scale) };
            string[] names = { "confirm", "driver", "error", "export-done", "capability", "unsupported" };
            for (int i = 0; i < all.Length; i++)
                using (SkinnedDialog d = all[i]) using (Bitmap image = d.Snapshot()) image.Save(Path.Combine(folder, names[i] + suffix + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            return all.Length;
        }
    }
}
