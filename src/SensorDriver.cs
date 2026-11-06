using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace DellFans
{
    public enum PawnIoState { Installed, Missing, HalfUninstalled, NeedsRestart }

    public interface IPawnIoEnvironment
    {
        bool DeviceOpens();
        bool ServiceExists();
        bool ServiceRunning();
    }

    public static class PawnIo
    {
        public static bool RestartPending; // set in this process after the installer asked for a restart

        public static PawnIoState Status(IPawnIoEnvironment env, bool restartPending)
        {
            if (env.DeviceOpens()) return PawnIoState.Installed;
            if (restartPending) return PawnIoState.NeedsRestart;
            if (!env.ServiceExists()) return PawnIoState.Missing;
            // A running driver whose device object is gone is what a vetoed uninstall leaves behind; only the installer repairs it.
            return env.ServiceRunning() ? PawnIoState.HalfUninstalled : PawnIoState.NeedsRestart;
        }
        public static PawnIoState Status() { return Status(new WindowsPawnIoEnvironment(), RestartPending); }
    }

    public sealed class WindowsPawnIoEnvironment : IPawnIoEnvironment
    {
        const string ServiceName = "PawnIO";
        public bool DeviceOpens()
        {
            using (Microsoft.Win32.SafeHandles.SafeFileHandle h = SensorNative.CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                return !h.IsInvalid;
        }
        public bool ServiceExists() { return Find() != null; }
        public bool ServiceRunning()
        {
            using (ServiceController c = Find()) return c != null && c.Status == ServiceControllerStatus.Running;
        }
        static ServiceController Find()
        {
            foreach (ServiceController c in ServiceController.GetDevices())
            {
                if (string.Equals(c.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase)) return c;
                c.Dispose();
            }
            return null;
        }
    }

    public interface IInstallerHost
    {
        bool IsElevated();
        void Download(string url, string path);
        Stream OpenLocked(string path); // FileShare.Read: nobody can write, rename or delete the file while the stream is open
        string Sha256Hex(Stream locked);
        // Authenticode verification (WinVerifyTrust on the held handle) plus the signer's certificate subject; null when the signature is invalid.
        string VerifiedSignerSubject(string path, Stream locked);
        int Run(string path, string arguments, TimeSpan timeout); // throws TimeoutException (after killing the process) when it does not finish
        bool DriverLoads(); // the PawnIO device opens after the installer finished
    }

    public enum SensorDriverInstallOutcome { Installed, RestartRequired, Failed }

    public sealed class SensorDriverInstallResult
    {
        public SensorDriverInstallOutcome Outcome;
        public string Message;
        public int ExitCode;
    }

    // DellFans.Cli.exe --install-sensor-driver. Version and hash match Install-SensorDriver.ps1.
    public static class SensorDriverInstaller
    {
        public const string Version = "2.2.0";
        public const string Url = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
        public const string Sha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
        public const string SignerCommonName = "namazso.eu";

        public static bool SignerIsExpected(string subject)
        { return subject != null && Regex.IsMatch(subject, @"(^|,\s*)CN=" + Regex.Escape(SignerCommonName) + @"(,|$)", RegexOptions.IgnoreCase); }

        public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

        // One FileStream (FileShare.Read) is opened right after the download and held through hashing, WinVerifyTrust and the installer run,
        // so the bytes that were hashed and verified are the bytes that execute. The directory itself is admin-only (SecureDirectory).
        public static SensorDriverInstallResult Install(IInstallerHost host, string directory)
        {
            if (!host.IsElevated())
                return Fail("Installing the PawnIO driver needs administrator rights; run DellFans.Cli.exe --install-sensor-driver from an elevated prompt. Nothing was downloaded.");
            string installer = Path.Combine(directory, "PawnIO_setup-" + Version + ".exe");
            host.Download(Url, installer);
            bool mismatch = false;
            using (Stream locked = host.OpenLocked(installer))
            {
                if (!string.Equals(host.Sha256Hex(locked), Sha256, StringComparison.OrdinalIgnoreCase)) mismatch = true;
                else
                {
                    string signer = host.VerifiedSignerSubject(installer, locked);
                    if (!SignerIsExpected(signer))
                        return Fail("PawnIO installer signature check failed (signer: " + (signer ?? "invalid") + "); nothing was run.");
                    int code;
                    try { code = host.Run(installer, "-install -silent", RunTimeout); }
                    catch (System.TimeoutException) { return Fail("PawnIO installer timed out after " + RunTimeout.TotalMinutes + " minutes and was stopped."); }
                    if (code == 3010)
                    {
                        PawnIo.RestartPending = true;
                        return new SensorDriverInstallResult { Outcome = SensorDriverInstallOutcome.RestartRequired, ExitCode = code, Message = "PawnIO " + Version + " installed. A restart is required before CPU sensors are available." };
                    }
                    if (code != 0) return new SensorDriverInstallResult { Outcome = SensorDriverInstallOutcome.Failed, ExitCode = code, Message = "PawnIO installer returned " + code + "." };
                    // The installer reports success when a stale driver-store package without PawnIO.sys already exists; only a loaded driver counts.
                    if (!host.DriverLoads())
                        return new SensorDriverInstallResult { Outcome = SensorDriverInstallOutcome.Failed, ExitCode = code, Message = "The PawnIO installer reported success, but the driver did not load (PawnIO.sys missing or blocked). Uninstall PawnIO in Apps & features, remove the leftover driver package with `pnputil /delete-driver <oemNN.inf> /uninstall /force`, then install again." };
                    return new SensorDriverInstallResult { Outcome = SensorDriverInstallOutcome.Installed, ExitCode = 0, Message = "Signed PawnIO " + Version + " installed." };
                }
            }
            if (mismatch) try { File.Delete(installer); } catch { }
            return Fail("PawnIO installer SHA-256 mismatch; nothing was run.");
        }

        static SensorDriverInstallResult Fail(string message) { return new SensorDriverInstallResult { Outcome = SensorDriverInstallOutcome.Failed, Message = message }; }

        // CLI arguments: --install-sensor-driver [--result-file <path>]. Returns the result file (null when absent); throws ArgumentException for anything else.
        public static string ResultFileArgument(string[] args)
        {
            if (args.Length == 1) return null;
            if (args.Length == 3 && args[1] == "--result-file" && args[2].Length > 0) return args[2];
            throw new ArgumentException("Usage: --install-sensor-driver [--result-file <path>]");
        }

        // CLI entry: fresh directory with a protected DACL under %ProgramData%\DellFans\downloads, removed afterwards.
        public static int RunCli() { return RunCli(null); }
        public static int RunCli(string resultFile)
        {
            string directory;
            try { directory = SecureDirectory.CreateFresh(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DellFans", "downloads")); }
            catch (IOException e) { return Report(Fail("Cannot prepare a protected download directory: " + e.Message), resultFile); }
            catch (UnauthorizedAccessException e) { return Report(Fail("Cannot prepare a protected download directory (run as administrator): " + e.Message), resultFile); }
            try { return Report(Install(new WindowsInstallerHost(), directory), resultFile); }
            catch (Exception e) { return Report(Fail(e.GetBaseException().Message), resultFile); } // e.g. no network: the GUI gets the reason too
            finally { try { Directory.Delete(directory, true); } catch { } }
        }

        // Prints the outcome, copies it to the GUI's result file, and returns the process exit code (0, 3010 restart, 1 failed).
        public static int Report(SensorDriverInstallResult result, string resultFile)
        {
            (result.Outcome == SensorDriverInstallOutcome.Failed ? Console.Error : Console.Out).WriteLine(result.Message);
            WriteResultFile(resultFile, result);
            return result.Outcome == SensorDriverInstallOutcome.Failed ? 1 : result.Outcome == SensorDriverInstallOutcome.RestartRequired ? 3010 : 0;
        }

        // This process may be elevated while the path came from a normal user, so only the empty ordinary file the GUI just created is written.
        static void WriteResultFile(string path, SensorDriverInstallResult result)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                FileInfo info = new FileInfo(Path.GetFullPath(path));
                if (!info.Exists || info.Length != 0 || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                { Console.Error.WriteLine("Result file ignored: it must be an existing empty file."); return; }
                string json = MiniJson.Serialize(new Dictionary<string, object> { { "Outcome", result.Outcome.ToString() }, { "Message", result.Message }, { "ExitCode", result.ExitCode } });
                File.WriteAllText(info.FullName, json, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { Console.Error.WriteLine("Result file not written: " + e.Message); }
        }
    }

    // Directories whose DACL is explicit (SYSTEM + Administrators, inheritance off) and whose path holds no reparse point.
    public static class SecureDirectory
    {
        static DirectorySecurity Protected()
        {
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            return security;
        }

        static void Ensure(string path)
        {
            DirectoryInfo info = new DirectoryInfo(path);
            if (info.Exists) info.SetAccessControl(Protected()); // fails closed when the directory is not ours to re-secure
            else info.Create(Protected());
        }

        static bool IsReparse(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        // Makes `path` (created when missing) and everything below it owned by Administrators and writable only by SYSTEM and Administrators.
        // Refuses a junction or symbolic link on the path or anywhere inside it, so a folder a normal user pre-created under
        // %ProgramData% cannot redirect the controller's writes. Existing files are kept; only their owner and explicit ACEs change.
        public static void ProtectRoot(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd('\\');
            for (string p = full; p != null; p = Path.GetDirectoryName(p))
                if (Directory.Exists(p) && IsReparse(p)) throw new IOException("Refusing reparse point: " + p);
            SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            DirectoryInfo root = new DirectoryInfo(full);
            if (!root.Exists) root.Create(Protected());
            if (IsReparse(full)) throw new IOException("Refusing reparse point: " + full);
            DirectorySecurity top = Protected(); top.SetOwner(admins);
            root.SetAccessControl(top); // locked first, so nobody adds entries while the contents are examined
            Stack<string> pending = new Stack<string>(); pending.Push(full);
            while (pending.Count > 0)
            {
                foreach (string item in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if (IsReparse(item)) throw new IOException("Refusing reparse point: " + item);
                    bool folder = Directory.Exists(item);
                    FileSystemSecurity security = folder ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
                    security.SetOwner(admins); security.SetAccessRuleProtection(false, false); // inherit only from the protected root
                    if (folder) { new DirectoryInfo(item).SetAccessControl((DirectorySecurity)security); pending.Push(item); }
                    else new FileInfo(item).SetAccessControl((FileSecurity)security);
                }
            }
        }
        // Returns a new empty protected directory below baseDirectory; baseDirectory and its parent are created / re-secured first.
        public static string CreateFresh(string baseDirectory)
        {
            string full = Path.GetFullPath(baseDirectory), parent = Path.GetDirectoryName(full);
            for (string p = full; p != null; p = Path.GetDirectoryName(p))
                if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing reparse point: " + p);
            Ensure(parent); Ensure(full);
            string fresh = Path.Combine(full, Guid.NewGuid().ToString("N"));
            new DirectoryInfo(fresh).Create(Protected());
            return fresh;
        }
    }

    public sealed class WindowsInstallerHost : IInstallerHost
    {
        public bool DriverLoads() { return new WindowsPawnIoEnvironment().DeviceOpens(); }
        public bool IsElevated()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        public void Download(string url, string path)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            using (WebClient client = new WebClient()) client.DownloadFile(url, path);
        }
        public Stream OpenLocked(string path) { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        public string Sha256Hex(Stream locked)
        {
            locked.Position = 0;
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(locked)).Replace("-", "");
        }
        public string VerifiedSignerSubject(string path, Stream locked)
        {
            if (!AuthenticodeVerifier.IsTrusted(path, ((FileStream)locked).SafeFileHandle.DangerousGetHandle())) return null;
            try { using (X509Certificate certificate = X509Certificate.CreateFromSignedFile(path)) return new X509Certificate2(certificate).Subject; }
            catch (CryptographicException) { return null; }
        }
        public int Run(string path, string arguments, TimeSpan timeout)
        {
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, arguments)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden }))
            {
                if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(); } catch { } throw new System.TimeoutException(); }
                return p.ExitCode;
            }
        }
    }

    public static class AuthenticodeVerifier
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFileInfo { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustData
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks;
            public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
            public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
        }
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

        // WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2) on an embedded signature; revocation lookups are skipped so it also works offline.
        public static bool IsTrusted(string path) { return IsTrusted(path, IntPtr.Zero); }

        // hFile: an already open handle to the same file (verification then reads that handle, not the path).
        public static bool IsTrusted(string path, IntPtr hFile)
        {
            Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            WinTrustFileInfo file = new WinTrustFileInfo { pcwszFilePath = path, hFile = hFile };
            file.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
            IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(file, filePointer, false);
                WinTrustData data = new WinTrustData { dwUIChoice = 2, fdwRevocationChecks = 0, dwUnionChoice = 1, pFile = filePointer, dwStateAction = 1, dwProvFlags = 0x10 };
                data.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
                int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                data.dwStateAction = 2; // close
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                return result == 0;
            }
            finally { Marshal.DestroyStructure(filePointer, typeof(WinTrustFileInfo)); Marshal.FreeHGlobal(filePointer); }
        }
    }
}
