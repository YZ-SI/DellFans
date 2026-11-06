using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// JIT-compiles the service and watchdog entry paths of the built exe and checks that neither loads
// WinForms or System.Drawing. Nothing is invoked; no service, pipe or hardware access.
internal static class StartupLoadTests
{
    static bool Loaded(string name) { return AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == name); }
    static int Main(string[] args)
    {
        Assembly exe = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type program = exe.GetType("DellFans.Program", true);
        string[] backgroundTypes = { "DellFans.PortableHost", "DellFans.ThermalServiceRuntime", "DellFans.ThermalEngine", "DellFans.FanWatchdog",
            "DellFans.ReportFiles", "DellFans.PipeProtocol", "DellFans.ControlPipeServer", "DellFans.ControlPolicy", "DellFans.FloorSettingsStore", "DellFans.ModeController", "DellFans.MiniJson" };
        var methods = new[] { "Main", "RunHost", "RunWatchdog" }.Select(name => program.GetMethod(name, all))
            .Concat(backgroundTypes.SelectMany(name => exe.GetType(name, true).GetMethods(all).Cast<MethodBase>().Concat(exe.GetType(name, true).GetConstructors(all))))
            .ToList();
        if (methods.Take(3).Any(m => m == null)) { Console.Error.WriteLine("FAIL Program must expose Main, RunHost and RunWatchdog"); return 1; }
        foreach (MethodBase method in methods.Where(m => !m.IsAbstract && !m.ContainsGenericParameters))
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            foreach (string ui in new[] { "System.Windows.Forms", "System.Drawing", "System.Web.Extensions" })
                if (Loaded(ui)) { Console.Error.WriteLine("FAIL JIT of " + method.DeclaringType.Name + "." + method.Name + " loaded " + ui); return 1; }
        }
        Console.WriteLine("PASS " + methods.Count + " service/watchdog methods JIT-compiled without loading WinForms, System.Drawing or System.Web.Extensions (JavaScriptSerializer)");
        return 0;
    }
}
