using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

// Offline: inspects freshly built executables (manifests) and calls pure static methods by reflection. Nothing is started,
// no service, pipe or hardware is contacted (the service lookup uses a name that does not exist).
internal static class DeAdminTests
{
    static int passed;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    static string Level(string exe)
    {
        Match match = Regex.Match(Encoding.UTF8.GetString(File.ReadAllBytes(exe)), "requestedExecutionLevel\\s+level=\"(\\w+)\"");
        if (!match.Success) throw new Exception("No embedded requestedExecutionLevel in " + exe);
        return match.Groups[1].Value;
    }
    static int Main(string[] args)
    {
        try
        {
            string bin = args[0];
            Check(Level(Path.Combine(bin, "DellFans.exe")) == "asInvoker", "The interface must run as the invoking user (asInvoker)");
            Check(Level(Path.Combine(bin, "DellFans.Cli.exe")) == "requireAdministrator", "The CLI keeps requireAdministrator");
            Check(Level(Path.Combine(bin, "DellFans.Service.exe")) == "requireAdministrator", "The service keeps requireAdministrator");
            Console.WriteLine("PASS DellFans.exe is asInvoker; DellFans.Cli.exe and DellFans.Service.exe stay requireAdministrator");

            const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Assembly cli = Assembly.LoadFrom(Path.Combine(bin, "DellFans.Cli.exe"));
            MethodInfo needs = cli.GetType("DellFans.Program", true).GetMethod("RequiresAdministrator", all);
            Check(needs != null, "Program.RequiresAdministrator must exist");
            Func<string[], bool> requires = a => (bool)needs.Invoke(null, new object[] { a });
            foreach (string[] gui in new[] { new string[0], new[] { "--gui-rpm", "3000" }, new[] { "--startup", "enable" }, new[] { "--startup", "disable" },
                new[] { "--render-preview", "--input", "a.json" } })
                Check(!requires(gui), "Interface and unprivileged commands must not require administrator: " + string.Join(" ", gui));
            foreach (string[] privileged in new[] { new[] { "--configure", "--start-temp", "65", "--full-temp", "85" }, new[] { "--test-fan" } })
                Check(requires(privileged), "Administrator-only commands keep the check: " + string.Join(" ", privileged));
            Console.WriteLine("PASS the interface no longer requires administrator; --configure and --test-fan keep the check");

            // A controller that is not running is a state: the client never starts anything itself.
            Type client = cli.GetType("DellFans.ThermalClient", true);
            object instance = null;
            try { instance = Activator.CreateInstance(client); }
            catch (TargetInvocationException e) { throw new Exception("Creating the client must not depend on or start the service: " + e.InnerException.Message); }
            ((IDisposable)instance).Dispose();
            MethodInfo send = cli.GetType("DellFans.ControlPipeClient", true).GetMethod("SendToHost", all);
            Check(send != null, "ControlPipeClient.SendToHost must exist");
            Type request = cli.GetType("DellFans.ServiceRequest", true);
            object status = Activator.CreateInstance(request); request.GetProperty("Command").SetValue(status, "Status", null);
            try { send.Invoke(null, new object[] { "DellFans.Test.NoSuchPipe." + Guid.NewGuid().ToString("N"), status }); Check(false, "A missing controller must raise an error"); }
            catch (TargetInvocationException e)
            {
                Check(e.InnerException.GetType().Name == "ServiceNotRunningException" && e.InnerException is InvalidOperationException, "A missing controller must raise ServiceNotRunningException, got " + e.InnerException.GetType().Name);
                Check(e.InnerException.Message == "后台控速程序未运行。", "Unexpected message: " + e.InnerException.Message);
            }
            Console.WriteLine("PASS the client creates without the controller and reports ServiceNotRunningException instead of starting it");
            Console.WriteLine("RESULT " + passed + " checks passed");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); return 1; }
    }
}
