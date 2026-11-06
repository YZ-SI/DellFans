using System;
using System.Linq;
using System.Threading;

namespace DellFans
{
    // The real machine behind ICapabilityHardware. Only the service builds one; tests use fakes and never reach this class.
    public sealed class LiveCapabilityHardware : ICapabilityHardware
    {
        readonly ThermalEngine engine;
        public LiveCapabilityHardware(ThermalEngine engine) { this.engine = engine; }
        public string Model { get { return engine.Model; } }
        public string BiosVersion { get { return engine.BiosVersion; } }
        public string WindowsBuild { get { return Environment.OSVersion.Version.ToString(); } }
        public string CpuName { get { return SensorLayer.ProcessorName(); } }
        public void CheckDellWmi()
        {
            DellFanBackend fan = engine.Fan;
            if (fan == null)
            {
                string problem = engine.InitializationProblem ?? "the Dell WMI interface did not initialize";
                if (problem.IndexOf("Dell systems only", StringComparison.OrdinalIgnoreCase) >= 0) throw new NotSupportedException(problem);
                throw new InvalidOperationException(problem);
            }
            fan.Verify();
        }
        public int ReadSmmRpm() { using (DellSmmFan smm = new DellSmmFan(Model, BiosVersion, true)) return smm.ReadRpm(); }
        public string ReadBiosMode()
        {
            if (engine.Fan == null) throw new InvalidOperationException("the Dell BIOS interface is unavailable");
            return engine.Fan.ReadMode();
        }
        public string CpuTemperatureSource()
        {
            Snapshot sample = engine.Poll();
            if (!sample.CpuControlTemperatureC.HasValue) throw new InvalidOperationException("no usable CPU temperature (source: " + (sample.SensorSource ?? "None") + ")");
            return sample.SensorSource;
        }
        public int StorageTemperatureCount()
        { return engine.Poll().Sensors.Count(r => r.HardwareType == "Storage" && r.Type == "Temperature" && r.Value.HasValue); }
        // Shortcut: the Rust watchdog session belongs to the RPM controller. For this test the fail-safe is BIOS full speed (loud, never hot);
        // the guard restores BIOS automatic when the test ends or the service is stopped mid-test.
        public IDisposable ArmWatchdog() { return new Guard(engine); }
        public void SetFullSpeed() { engine.SetFullPersistent(); }
        public void RestoreAutomatic() { engine.SetAutomatic(); }
        public void Wait(int milliseconds) { Thread.Sleep(milliseconds); }
        sealed class Guard : IDisposable
        {
            readonly ThermalEngine engine;
            public Guard(ThermalEngine engine) { this.engine = engine; }
            public void Dispose() { try { engine.Restore(); } catch (Exception) { } }
        }
    }
}
