using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DellFans
{
    public enum CallerLevel { None = 0, Interactive = 1, Administrator = 2 }

    // Who may send which command, and what a request may contain. Both are decided here, in the service, never in the client.
    public static class ControlPolicy
    {
        // Configure (temperature curve) no longer exists.
        static readonly Dictionary<string, CallerLevel> required = new Dictionary<string, CallerLevel>(StringComparer.Ordinal)
        {
            { "Status", CallerLevel.Interactive }, { "Exit", CallerLevel.Interactive },
            { "SetMode", CallerLevel.Interactive }, { "Capability", CallerLevel.Interactive }, { "RunCapabilityProbe", CallerLevel.Interactive },
            { "Probe", CallerLevel.Administrator }
        };
        public static CallerLevel Required(string command)
        { CallerLevel level; return command != null && required.TryGetValue(command, out level) ? level : CallerLevel.Administrator; }
        // Throws ArgumentException for anything the service would not act on.
        public static void Validate(ServiceRequest request)
        {
            if (request == null) throw new ArgumentException("Request is empty.");
            string command = request.Command;
            if (command == null || command.Length > 32 || !required.ContainsKey(command)) throw new ArgumentException("Unsupported service command.");
            if (request.Mode != null && command != "SetMode") throw new ArgumentException(command + " does not take Mode.");
            if (request.Consent.HasValue && command != "RunCapabilityProbe") throw new ArgumentException(command + " does not take Consent.");
            switch (command)
            {
                case "Status":
                    if (request.MinimumRpm.HasValue) throw new ArgumentException("Status takes only Detail.");
                    break;
                case "SetMode":
                    if (!FanModes.IsValid(request.Mode)) throw new ArgumentException("Mode must be quiet, balanced, performance, custom or auto.");
                    if (request.Mode == FanModes.Custom)
                    {
                        if (!request.MinimumRpm.HasValue) throw new ArgumentException("Custom mode requires MinimumRpm.");
                        RpmRange.Validate(request.MinimumRpm.Value);
                    }
                    else if (request.MinimumRpm.HasValue) throw new ArgumentException("MinimumRpm belongs to custom mode only.");
                    if (request.Detail.HasValue) throw new ArgumentException("SetMode does not take Detail.");
                    break;
                case "RunCapabilityProbe":
                    // The test runs the fan at full speed for 15 s; the service will not start it without the user's explicit yes.
                    if (request.Consent != true) throw new ArgumentException("RunCapabilityProbe requires Consent=true.");
                    if (request.MinimumRpm.HasValue || request.Detail.HasValue) throw new ArgumentException(command + " takes only Consent.");
                    break;
                default:
                    if (request.MinimumRpm.HasValue || request.Detail.HasValue) throw new ArgumentException(command + " takes no fields.");
                    break;
            }
        }
        // A write command with fields this version does not know (for example SetMode with curve fields) is refused, not half-applied.
        public static void RejectUnknownFields(ServiceRequest request, IList<string> unknown)
        {
            if (request != null && request.Command != "Status" && unknown != null && unknown.Count > 0)
                throw new ArgumentException(request.Command + " has unsupported field(s): " + string.Join(", ", unknown) + ". Use SetMode.");
        }
        // Network logons are refused outright, whatever else the token holds. Only INTERACTIVE logons (console and remote desktop)
        // or administrators/SYSTEM are served; service, batch and anonymous tokens are not.
        public static CallerLevel Classify(bool isAdministrator, bool isSystem, IEnumerable<SecurityIdentifier> groups)
        {
            HashSet<SecurityIdentifier> held = new HashSet<SecurityIdentifier>(groups);
            if (held.Contains(new SecurityIdentifier(WellKnownSidType.NetworkSid, null))) return CallerLevel.None;
            if (isSystem || isAdministrator) return CallerLevel.Administrator;
            return held.Contains(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null)) ? CallerLevel.Interactive : CallerLevel.None;
        }
        // `identity` must be the impersonated client token. A non-elevated administrator has Administrators as deny-only, so IsInRole is false.
        public static CallerLevel Classify(WindowsIdentity identity)
        {
            if (identity.IsAnonymous || identity.IsGuest) return CallerLevel.None;
            bool system = identity.User != null && identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid);
            return Classify(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), system, identity.Groups.Cast<SecurityIdentifier>());
        }
    }

    // Multi-instance control pipe. The first instance carries FILE_FLAG_FIRST_PIPE_INSTANCE so a squatter that already owns the name makes
    // the service fail loudly (reported and retried) instead of silently serving an attacker's pipe; later instances are blocked by the DACL,
    // which grants INTERACTIVE no right to create instances.
    public sealed class ControlPipeServer : IDisposable
    {
        public const int MaxInstances = 4;
        const uint FirstPipeInstanceFlag = 0x00080000; // FILE_FLAG_FIRST_PIPE_INSTANCE
        readonly string name;
        readonly Func<ServiceRequest, ServiceReply> handler;
        readonly int requestTimeoutMilliseconds;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly List<Thread> threads = new List<Thread>();
        readonly List<NamedPipeServerStream> live = new List<NamedPipeServerStream>();
        public Func<WindowsIdentity, CallerLevel> Classifier = ControlPolicy.Classify;
        public Action<Exception> OnError;
        public int RetryDelayMilliseconds = 2000;
        internal volatile CallerLevel LastCallerLevel;
        internal volatile TokenImpersonationLevel LastCallerImpersonation;
        public ControlPipeServer(string pipeName, Func<ServiceRequest, ServiceReply> handler, int requestTimeoutMilliseconds = 5000)
        { name = pipeName; this.handler = handler; this.requestTimeoutMilliseconds = requestTimeoutMilliseconds; }

        public static PipeSecurity BuildAcl()
        {
            PipeSecurity acl = new PipeSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return acl;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);
        // .NET Framework rejects the FILE_FLAG_FIRST_PIPE_INSTANCE bit in PipeOptions, so the handle is created natively and wrapped.
        public static NamedPipeServerStream CreateInstance(string pipeName, bool first)
        {
            byte[] descriptor = BuildAcl().GetSecurityDescriptorBinaryForm();
            IntPtr memory = Marshal.AllocHGlobal(descriptor.Length);
            try
            {
                Marshal.Copy(descriptor, 0, memory, descriptor.Length);
                SecurityAttributes attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = memory, Inherit = 0 };
                // PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED [| FILE_FLAG_FIRST_PIPE_INSTANCE]; PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS.
                // Remote rejection matters despite the NETWORK deny: an SMB loopback (\\127.0.0.1\pipe\...) keeps the caller's own token (seen on the real machine).
                SafePipeHandle handle = CreateNamedPipe(@"\\.\pipe\" + pipeName, 0x00000003u | 0x40000000u | (first ? FirstPipeInstanceFlag : 0u), 0x00000008u, MaxInstances, 65536, 65536, 0, ref attributes);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException("Cannot create control pipe instance (" + (first ? "first, " : "") + "error " + error + "); the name may be in use.", new Win32Exception(error));
                }
                try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
                catch { handle.Dispose(); throw; }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        void Report(Exception error) { Action<Exception> report = OnError; if (report != null) try { report(error); } catch { } }

        // Throws when the name is already taken.
        public void Start()
        {
            NamedPipeServerStream first = CreateInstance(name, true);
            try { StartListeners(first); } catch { first.Dispose(); throw; }
        }
        // Never throws and never ends the process: a squatted name is reported through OnError and retried.
        public void StartResilient()
        {
            Thread supervisor = new Thread(delegate()
            {
                NamedPipeServerStream first = null;
                while (first == null)
                {
                    if (stop.WaitOne(0)) return;
                    try { first = CreateInstance(name, true); }
                    catch (Exception error) { Report(error); if (stop.WaitOne(RetryDelayMilliseconds)) return; }
                }
                try { StartListeners(first); } catch (Exception error) { first.Dispose(); Report(error); }
            }) { IsBackground = true, Name = "DellFans IPC supervisor" };
            lock (threads) threads.Add(supervisor);
            supervisor.Start();
        }
        void StartListeners(NamedPipeServerStream first)
        {
            lock (threads)
                for (int i = 0; i < MaxInstances; i++)
                {
                    NamedPipeServerStream initial = i == 0 ? first : null;
                    Thread listener = new Thread(delegate() { Listen(initial); }) { IsBackground = true, Name = "DellFans IPC " + i };
                    threads.Add(listener); listener.Start();
                }
        }
        void Listen(NamedPipeServerStream pipe)
        {
            while (!stop.WaitOne(0))
            {
                try
                {
                    if (pipe == null) pipe = CreateInstance(name, false);
                    lock (live) live.Add(pipe);
                    try { using (pipe) Serve(pipe); }
                    finally { lock (live) live.Remove(pipe); }
                }
                catch (Exception error)
                {
                    if (stop.WaitOne(0)) return;
                    Report(error); if (stop.WaitOne(RetryDelayMilliseconds)) return;
                }
                pipe = null;
            }
        }
        void Serve(NamedPipeServerStream pipe)
        {
            Task waiting = pipe.WaitForConnectionAsync();
            while (!waiting.Wait(500)) { if (stop.WaitOne(0)) return; }
            waiting.GetAwaiter().GetResult();
            ServiceReply reply;
            try { List<string> unknown; ServiceRequest request = PipeProtocol.Read<ServiceRequest>(pipe, 65536, requestTimeoutMilliseconds, out unknown); reply = Process(pipe, request, unknown); }
            catch (Exception e) { reply = new ServiceReply { Success = false, Error = e.Message }; }
            if (stop.WaitOne(0)) return;
            try { PipeProtocol.Write(pipe, reply); } catch { }
        }
        // Identity is read from the impersonated client token after the request has arrived (the first read is what makes impersonation
        // legal). The client connects at Identification level, so the token can be inspected but never used to act as the client.
        ServiceReply Process(NamedPipeServerStream pipe, ServiceRequest request, IList<string> unknown)
        {
            CallerLevel level = CallerLevel.None; TokenImpersonationLevel impersonation = TokenImpersonationLevel.None;
            pipe.RunAsClient(delegate()
            {
                using (WindowsIdentity client = WindowsIdentity.GetCurrent()) { impersonation = client.ImpersonationLevel; level = Classifier(client); }
            });
            LastCallerImpersonation = impersonation; LastCallerLevel = level;
            if (level == CallerLevel.None) return new ServiceReply { Success = false, Error = "Access denied: this logon may not use the DellFans service." };
            ControlPolicy.Validate(request); ControlPolicy.RejectUnknownFields(request, unknown);
            if (level < ControlPolicy.Required(request.Command))
                return new ServiceReply { Success = false, Error = "Access denied: " + request.Command + " requires an administrator." };
            return handler(request);
        }
        public void Dispose()
        {
            stop.Set();
            lock (live) foreach (NamedPipeServerStream pipe in live.ToArray()) try { pipe.Dispose(); } catch { }
            Thread[] all; lock (threads) all = threads.ToArray();
            DateTime end = DateTime.UtcNow.AddSeconds(5); // one shared bound: shutdown must not wait on every thread in turn
            foreach (Thread thread in all)
                if (thread != Thread.CurrentThread) thread.Join(Math.Max(0, (int)(end - DateTime.UtcNow).TotalMilliseconds));
        }
    }

    // The background controller (DellFans.Service.exe --portable) is not running. The interface starts it with the user's consent (UAC).
    public sealed class ServiceNotRunningException : InvalidOperationException
    {
        public ServiceNotRunningException() : base("后台控速程序未运行。") { }
    }

    public static class ControlPipeClient
    {
        // The only program the clients talk to: DellFans.Service.exe next to the client, running elevated.
        public static string HostImage { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DellFans.Service.exe"); } }
        // Sends only to the background controller next to this client, and only after its process proved to be that program, elevated.
        // An unelevated program cannot start an elevated process, so a squatter on the pipe name fails this check even from the same folder.
        public static Snapshot SendToHost(string pipeName, ServiceRequest request)
        {
            if (!PipeExists(pipeName)) throw new ServiceNotRunningException();
            string expected = HostImage;
            return Send(pipeName, request, delegate (int pid) { return IsTrustedHost(pid, expected); });
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool WaitNamedPipe(string name, uint timeout);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // False only when no instance of the pipe exists at all (ERROR_FILE_NOT_FOUND); a busy pipe still exists.
        public static bool PipeExists(string pipeName)
        {
            if (WaitNamedPipe(@"\\.\pipe\" + pipeName, 1)) return true;
            return Marshal.GetLastWin32Error() != 2;
        }
        // The process image is exactly `expectedImage` and its token is elevated.
        public static bool IsTrustedHost(int pid, string expectedImage)
        {
            IntPtr process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (process == IntPtr.Zero) return false;
            try
            {
                System.Text.StringBuilder name = new System.Text.StringBuilder(1024); int size = name.Capacity;
                if (!QueryFullProcessImageName(process, 0, name, ref size)) return false;
                if (!string.Equals(Path.GetFullPath(name.ToString()), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase)) return false;
                IntPtr token;
                if (!OpenProcessToken(process, 0x0008, out token)) return false; // TOKEN_QUERY
                try { int elevated, returned; return GetTokenInformation(token, 20, out elevated, 4, out returned) && elevated != 0; } // TokenElevation
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }
        public static void RequireServerProcess(NamedPipeClientStream pipe, Func<int, bool> trusted)
        {
            uint actual;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out actual)) throw new Win32Exception();
            if (!trusted((int)actual))
                throw new InvalidOperationException("The pipe server is not the DellFans background controller (process " + actual + "); the request was not sent.");
        }
        // Tests name the expected server process directly.
        public static Snapshot Send(string pipeName, ServiceRequest request, int expectedServerPid)
        { return Send(pipeName, request, delegate (int pid) { return expectedServerPid > 0 && pid == expectedServerPid; }); }
        public static Snapshot Send(string pipeName, ServiceRequest request, Func<int, bool> trusted)
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
            {
                pipe.Connect(4000);
                RequireServerProcess(pipe, trusted);
                PipeProtocol.Write(pipe, request);
                ServiceReply reply = PipeProtocol.Read<ServiceReply>(pipe, 4 * 1024 * 1024);
                if (!reply.Success) throw new InvalidOperationException(reply.Error ?? "Background request failed.");
                return reply.Snapshot;
            }
        }
    }
}
