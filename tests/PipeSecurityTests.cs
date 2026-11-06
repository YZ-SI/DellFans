using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DellFans;

// Offline: every pipe uses a unique DellFans.Test.<guid> name. The real DellFans.Control.v2 pipe and service are never contacted.
internal static class PipeSecurityTests
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static string NewName() { return "DellFans.Test." + Guid.NewGuid().ToString("N"); }
    static ServiceReply Ok(ServiceRequest request) { return new ServiceReply { Success = true }; }
    static SecurityIdentifier Sid(WellKnownSidType type) { return new SecurityIdentifier(type, null); }

    static void AclMatchesContract()
    {
        using (NamedPipeServerStream pipe = ControlPipeServer.CreateInstance(NewName(), true))
        {
            PipeSecurity security = pipe.GetAccessControl();
            Require(security.AreAccessRulesProtected, "Pipe DACL must not inherit");
            SecurityIdentifier network = Sid(WellKnownSidType.NetworkSid), interactive = Sid(WellKnownSidType.InteractiveSid);
            SecurityIdentifier system = Sid(WellKnownSidType.LocalSystemSid), admins = Sid(WellKnownSidType.BuiltinAdministratorsSid);
            List<PipeAccessRule> rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
            Require(rules.Count == 4, "Expected exactly SYSTEM, Administrators, INTERACTIVE and NETWORK entries, found " + rules.Count);
            Func<SecurityIdentifier, PipeAccessRule> rule = sid => rules.Single(r => r.IdentityReference.Equals(sid));
            Require(rule(network).AccessControlType == AccessControlType.Deny, "NETWORK must be denied");
            Require(rule(system).AccessControlType == AccessControlType.Allow && (rule(system).PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl, "SYSTEM must have full control");
            Require(rule(admins).AccessControlType == AccessControlType.Allow && (rule(admins).PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl, "Administrators must have full control");
            PipeAccessRule user = rule(interactive);
            Require(user.AccessControlType == AccessControlType.Allow && (user.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite, "INTERACTIVE must read/write");
            Require((user.PipeAccessRights & (PipeAccessRights.CreateNewInstance | PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership)) == 0,
                "INTERACTIVE must not be able to create instances or change the DACL");
        }
    }
    // PIPE_REJECT_REMOTE_CLIENTS: an SMB loopback path keeps the caller's own token, so only the flag stops it (seen on the real machine).
    static void RemoteClientsAreRejected()
    {
        string name = NewName();
        using (NamedPipeServerStream pipe = ControlPipeServer.CreateInstance(name, true))
        {
            bool refused = false;
            using (NamedPipeClientStream remote = new NamedPipeClientStream("127.0.0.1", name, PipeDirection.InOut))
                try { remote.Connect(3000); } catch (UnauthorizedAccessException) { refused = true; } catch (IOException) { refused = true; } catch (TimeoutException) { refused = true; }
            Require(refused, @"A client reaching the pipe over SMB (\\127.0.0.1\pipe\...) must be refused");
            using (NamedPipeClientStream local = new NamedPipeClientStream(".", name, PipeDirection.InOut)) local.Connect(3000); // the same instance still serves local clients
        }
    }
    static void FirstInstanceFlagStopsSquatting()
    {
        string name = NewName();
        // A squatter that owns the name before the service starts: Start must fail loudly, not adopt it.
        using (NamedPipeServerStream squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (ControlPipeServer server = new ControlPipeServer(name, Ok))
        {
            bool refused = false;
            try { server.Start(); } catch (IOException) { refused = true; } catch (UnauthorizedAccessException) { refused = true; }
            Require(refused, "A pre-existing pipe of the same name must make the first instance fail");
        }
        // And the service's own first instance, when the name is free, is accepted and a second "first" instance is not.
        using (ControlPipeServer real = new ControlPipeServer(name = NewName(), Ok))
        {
            real.Start();
            bool refused = false;
            try { using (ControlPipeServer.CreateInstance(name, true)) { } } catch (IOException) { refused = true; }
            Require(refused, "Only one instance can be the first instance");
        }
    }
    static void FourClientsAreServedConcurrently()
    {
        string name = NewName();
        int inside = 0, peak = 0;
        ManualResetEventSlim release = new ManualResetEventSlim();
        using (ControlPipeServer server = new ControlPipeServer(name, delegate(ServiceRequest request)
        {
            int now = Interlocked.Increment(ref inside); int seen; while ((seen = peak) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            release.Wait(10000); Interlocked.Decrement(ref inside); return new ServiceReply { Success = true };
        }))
        {
            server.Start();
            List<Task<Snapshot>> calls = new List<Task<Snapshot>>();
            for (int i = 0; i < ControlPipeServer.MaxInstances; i++)
                calls.Add(Task.Run(delegate { return ControlPipeClient.Send(name, new ServiceRequest { Command = "Status" }, Process.GetCurrentProcess().Id); }));
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (peak < ControlPipeServer.MaxInstances && DateTime.UtcNow < deadline) Thread.Sleep(20);
            release.Set();
            Require(peak == ControlPipeServer.MaxInstances, "Up to " + ControlPipeServer.MaxInstances + " requests must be in flight at once, peak was " + peak);
            Require(Task.WaitAll(calls.ToArray(), 5000), "All concurrent clients must be answered");
        }
    }
    static void StalledClientsCannotStarveOthers()
    {
        string name = NewName();
        using (ControlPipeServer server = new ControlPipeServer(name, Ok, 800))
        {
            server.Start();
            List<NamedPipeClientStream> stalled = new List<NamedPipeClientStream>();
            try
            {
                for (int i = 0; i < ControlPipeServer.MaxInstances; i++)
                {
                    NamedPipeClientStream idle = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                    idle.Connect(2000); stalled.Add(idle); // connected, never sends a byte
                }
                Stopwatch clock = Stopwatch.StartNew();
                Snapshot reply = ControlPipeClient.Send(name, new ServiceRequest { Command = "Status" }, Process.GetCurrentProcess().Id);
                Require(clock.ElapsedMilliseconds < 8000, "Idle connections must be dropped by a total request deadline");
            }
            finally { foreach (NamedPipeClientStream s in stalled) s.Dispose(); }
        }
    }
    static void ClientIdentityAndPrivilegeSplit()
    {
        string name = NewName();
        List<string> executed = new List<string>();
        using (ControlPipeServer server = new ControlPipeServer(name, delegate(ServiceRequest request) { lock (executed) executed.Add(request.Command); return new ServiceReply { Success = true }; }))
        {
            server.Start();
            int pid = Process.GetCurrentProcess().Id;
            ControlPipeClient.Send(name, new ServiceRequest { Command = "Probe" }, pid);
            Require(server.LastCallerLevel == CallerLevel.Administrator, "An elevated administrator client must be classified Administrator, was " + server.LastCallerLevel);
            Require(server.LastCallerImpersonation == TokenImpersonationLevel.Identification, "The server must only ever get an Identification-level token, was " + server.LastCallerImpersonation);
            // An interactive, non-administrator caller: Status/SetMode yes, Probe no.
            server.Classifier = identity => CallerLevel.Interactive;
            ControlPipeClient.Send(name, new ServiceRequest { Command = "Status" }, pid);
            ControlPipeClient.Send(name, new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 2000 }, pid);
            ControlPipeClient.Send(name, new ServiceRequest { Command = "SetMode", Mode = "auto" }, pid);
            ControlPipeClient.Send(name, new ServiceRequest { Command = "SetMode", Mode = "quiet" }, pid);
            ControlPipeClient.Send(name, new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 3000 }, pid);
            foreach (ServiceRequest forbidden in new[] { new ServiceRequest { Command = "Probe" } })
            {
                int before; lock (executed) before = executed.Count;
                bool denied = false;
                try { ControlPipeClient.Send(name, forbidden, pid); } catch (InvalidOperationException e) { denied = e.Message.Contains("Access denied"); }
                lock (executed) Require(denied && executed.Count == before, forbidden.Command + " must be refused for a non-administrator without reaching the handler");
            }
            server.Classifier = identity => CallerLevel.None;
            bool none = false;
            try { ControlPipeClient.Send(name, new ServiceRequest { Command = "Status" }, pid); } catch (InvalidOperationException e) { none = e.Message.Contains("Access denied"); }
            Require(none, "A caller that is neither interactive nor administrator gets nothing");
        }
    }
    static void ClassificationRejectsNetworkLogons()
    {
        SecurityIdentifier network = Sid(WellKnownSidType.NetworkSid), interactive = Sid(WellKnownSidType.InteractiveSid);
        Require(ControlPolicy.Classify(false, false, new[] { interactive }) == CallerLevel.Interactive, "Interactive logon");
        Require(ControlPolicy.Classify(true, false, new[] { interactive }) == CallerLevel.Administrator, "Administrator logon");
        Require(ControlPolicy.Classify(true, false, new[] { network }) == CallerLevel.None, "Network logon is refused even for administrators");
        Require(ControlPolicy.Classify(false, false, new[] { network, interactive }) == CallerLevel.None, "Network logon is refused");
        Require(ControlPolicy.Classify(false, false, new SecurityIdentifier[0]) == CallerLevel.None, "Service/batch logons without INTERACTIVE are refused");
        Require(ControlPolicy.Classify(true, true, new SecurityIdentifier[0]) == CallerLevel.Administrator, "SYSTEM is an administrator");
    }
    static void RequestFieldsAreValidated()
    {
        Func<ServiceRequest, bool> bad = request => { try { ControlPolicy.Validate(request); return false; } catch (ArgumentException) { return true; } };
        Require(bad(null), "null request");
        Require(bad(new ServiceRequest()), "missing command");
        Require(bad(new ServiceRequest { Command = "Shutdown" }), "unknown command");
        Require(bad(new ServiceRequest { Command = new string('A', 4000) }), "oversized command");
        Require(bad(new ServiceRequest { Command = "status" }), "commands are case sensitive");
        Require(bad(new ServiceRequest { Command = "Configure" }), "Configure (the old temperature curve) is no longer a command");
        Require(bad(new ServiceRequest { Command = "SetMode" }) && bad(new ServiceRequest { Command = "SetMode", Mode = "turbo" }), "SetMode needs a known mode");
        Require(bad(new ServiceRequest { Command = "SetMode", Mode = "custom" }) && bad(new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 999 }), "custom needs an in-range minimum");
        Require(bad(new ServiceRequest { Command = "SetMode", Mode = "quiet", MinimumRpm = 2000 }), "only custom takes a minimum");
        Require(bad(new ServiceRequest { Command = "Status", MinimumRpm = 2000 }), "Status takes no RPM");
        Require(bad(new ServiceRequest { Command = "Probe", Detail = true }), "Probe takes no fields");
        ControlPolicy.Validate(new ServiceRequest { Command = "Status" });
        ControlPolicy.Validate(new ServiceRequest { Command = "Status", Detail = false });
        ControlPolicy.Validate(new ServiceRequest { Command = "SetMode", Mode = "balanced" });
        ControlPolicy.Validate(new ServiceRequest { Command = "SetMode", Mode = "auto" });
        ControlPolicy.Validate(new ServiceRequest { Command = "Probe" });
        string name = NewName(); int calls = 0;
        using (ControlPipeServer server = new ControlPipeServer(name, delegate(ServiceRequest r) { Interlocked.Increment(ref calls); return new ServiceReply { Success = true }; }))
        {
            server.Start();
            bool rejected = false;
            try { ControlPipeClient.Send(name, new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 90000 }, Process.GetCurrentProcess().Id); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && calls == 0, "An out-of-range request must be refused by the pipe layer before the handler runs");
        }
    }
    static void ClientRefusesAnImpostorServer()
    {
        string name = NewName(); int calls = 0;
        using (ControlPipeServer server = new ControlPipeServer(name, delegate(ServiceRequest r) { Interlocked.Increment(ref calls); return new ServiceReply { Success = true }; }))
        {
            server.Start();
            int self = Process.GetCurrentProcess().Id;
            ControlPipeClient.Send(name, new ServiceRequest { Command = "Status" }, self);
            bool refused = false;
            try { ControlPipeClient.Send(name, new ServiceRequest { Command = "Status" }, self + 4); }
            catch (InvalidOperationException e) { refused = e.Message.Contains("not the DellFans background controller"); }
            Require(refused, "A server that is not the expected process must be refused");
            Require(calls == 1, "The request must not be sent to an impostor");
            Require(ControlPipeClient.PipeExists(name), "A listening pipe exists");
            // The real check: the server must be DellFans.Service.exe next to the client, elevated. This test process is neither.
            refused = false;
            try { ControlPipeClient.Send(name, new ServiceRequest { Command = "Status" }, delegate (int pid) { return ControlPipeClient.IsTrustedHost(pid, ControlPipeClient.HostImage); }); }
            catch (InvalidOperationException e) { refused = e.Message.Contains("not the DellFans background controller"); }
            Require(refused && calls == 1, "A server that is not DellFans.Service.exe next to the client is refused");
            string own = Process.GetCurrentProcess().MainModule.FileName;
            bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Require(ControlPipeClient.IsTrustedHost(self, own) == elevated, "The image path matches; trust then depends only on elevation");
            Require(!ControlPipeClient.IsTrustedHost(self, own + ".other.exe"), "Another image path is never trusted");
        }
        Require(!ControlPipeClient.PipeExists("DellFans.Test.NoSuchPipe." + Guid.NewGuid().ToString("N")), "A missing pipe does not exist");
    }
    static void ServerSurvivesSquattedNameAndRetries()
    {
        string name = NewName(); List<Exception> errors = new List<Exception>();
        NamedPipeServerStream squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using (ControlPipeServer server = new ControlPipeServer(name, Ok))
        {
            server.OnError = delegate(Exception e) { lock (errors) errors.Add(e); };
            server.RetryDelayMilliseconds = 100;
            server.StartResilient(); // must not throw or crash the process on the background thread
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline) { lock (errors) if (errors.Count > 0) break; Thread.Sleep(20); }
            lock (errors) Require(errors.Count > 0, "A squatted name must be reported");
            squatter.Dispose();
            ControlPipeClient.Send(name, new ServiceRequest { Command = "Status" }, Process.GetCurrentProcess().Id);
        }
    }
    static int Main()
    {
        try
        {
            AclMatchesContract(); Console.WriteLine("PASS pipe DACL: SYSTEM/Administrators full, INTERACTIVE read/write only, NETWORK denied");
            RemoteClientsAreRejected(); Console.WriteLine("PASS remote (SMB) clients are rejected by PIPE_REJECT_REMOTE_CLIENTS; local clients are served");
            FirstInstanceFlagStopsSquatting(); Console.WriteLine("PASS first instance uses FILE_FLAG_FIRST_PIPE_INSTANCE; squatted or added instances are refused");
            FourClientsAreServedConcurrently(); Console.WriteLine("PASS " + ControlPipeServer.MaxInstances + " clients are served concurrently");
            StalledClientsCannotStarveOthers(); Console.WriteLine("PASS idle connections are dropped by a total request deadline");
            ClientIdentityAndPrivilegeSplit(); Console.WriteLine("PASS client identity (Identification level) splits Status/SetMode from Probe");
            ClassificationRejectsNetworkLogons(); Console.WriteLine("PASS network, service and batch logons are classified as no access");
            RequestFieldsAreValidated(); Console.WriteLine("PASS every request field is validated before the handler");
            ClientRefusesAnImpostorServer(); Console.WriteLine("PASS client refuses a server that is not the elevated DellFans.Service.exe next to it");
            ServerSurvivesSquattedNameAndRetries(); Console.WriteLine("PASS squatted pipe name is reported and retried instead of crashing the service");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); return 1; }
    }
}
