using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace DellFans
{
    // Starts a program elevated and waits for it. Tests inject a fake; the real one is the only place that runs DellFans.Cli.exe.
    public interface IElevatedStarter
    {
        // Throws Win32Exception (NativeErrorCode 1223) when the user declines the UAC prompt.
        int Run(string file, string arguments, string verb);
    }

    public sealed class ShellElevatedStarter : IElevatedStarter
    {
        public int Run(string file, string arguments, string verb)
        {
            ProcessStartInfo info = new ProcessStartInfo(file, arguments) { UseShellExecute = true, Verb = verb, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(file) };
            using (Process process = Process.Start(info)) { process.WaitForExit(); return process.ExitCode; }
        }
    }

    public enum DriverInstallKind { Installed, RestartRequired, Cancelled, Failed }

    public sealed class DriverInstallOutcome
    {
        public DriverInstallKind Kind;
        public string Message; // the CLI's own text (English), when it wrote one
        public int ExitCode;
    }

    // What DellFans.Cli.exe writes to --result-file.
    public sealed class DriverInstallReport
    {
        public string Outcome { get; set; }
        public string Message { get; set; }
        public int ExitCode { get; set; }
    }

    // the window asks first, then runs `DellFans.Cli.exe --install-sensor-driver --result-file <tmp>` elevated and maps its exit code.
    public static class DriverInstallFlow
    {
        public const string Verb = "runas";
        const int CancelledByUser = 1223; // ERROR_CANCELLED

        public static string Arguments(string resultFile) { return "--install-sensor-driver --result-file \"" + resultFile + "\""; }

        // Blocking: call from a worker thread. The caller creates resultFile (empty) and deletes it afterwards.
        public static DriverInstallOutcome Run(IElevatedStarter starter, string cliPath, string resultFile)
        {
            int code;
            try { code = starter.Run(cliPath, Arguments(resultFile), Verb); }
            catch (Win32Exception e)
            { return new DriverInstallOutcome { Kind = e.NativeErrorCode == CancelledByUser ? DriverInstallKind.Cancelled : DriverInstallKind.Failed, Message = e.NativeErrorCode == CancelledByUser ? null : e.Message, ExitCode = e.NativeErrorCode }; }
            string message = ReadMessage(resultFile);
            DriverInstallKind kind = code == 0 ? DriverInstallKind.Installed : code == 3010 ? DriverInstallKind.RestartRequired : DriverInstallKind.Failed;
            return new DriverInstallOutcome { Kind = kind, Message = kind == DriverInstallKind.Failed ? message : null, ExitCode = code };
        }

        static string ReadMessage(string path)
        {
            try
            {
                if (new FileInfo(path).Length > 65536) return null;
                DriverInstallReport report = MiniJson.Deserialize<DriverInstallReport>(File.ReadAllText(path), 65536);
                string text = report == null ? null : report.Message;
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch (Exception) { return null; } // empty, missing or malformed: fall back to the exit code
        }

        public static string Describe(DriverInstallOutcome outcome)
        {
            switch (outcome.Kind)
            {
                case DriverInstallKind.Installed: return UiText.T("驱动已安装，CPU 温度将在几秒内恢复读取。");
                case DriverInstallKind.RestartRequired: return UiText.T("驱动已安装，重启电脑后生效。");
                case DriverInstallKind.Cancelled: return UiText.T("已取消：未授予管理员权限，没有安装任何内容。");
                default: return outcome.Message != null ? UiText.F("安装失败：{0}", outcome.Message) : UiText.F("安装失败（退出码 {0}）。", outcome.ExitCode);
            }
        }
    }
}
