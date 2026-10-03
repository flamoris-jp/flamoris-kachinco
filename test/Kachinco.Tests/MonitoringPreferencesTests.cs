using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class MonitoringPreferencesTests
{
    [TestMethod]
    public void PreferenceIsBoundedValidatedAndAtomicallyReplacedOutsideTheProject()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); string path = Path.Combine(folder, "playback.json");
        try
        {
            var store = new MonitoringPreferences(path); Assert.AreEqual(1d, store.Load().Value);
            Assert.IsTrue(store.Save(.3).Success); Assert.AreEqual(.3, new MonitoringPreferences(path).Load().Value);
            foreach (double invalid in new[] { double.NaN, double.NegativeInfinity, -1, 1.001 })
            { Assert.IsFalse(store.Save(invalid).Success); Assert.AreEqual(.3, store.Load().Value); }
            Assert.IsTrue(store.Save(0).Success); Assert.AreEqual(0d, store.Load().Value);
            Assert.AreEqual(1, Directory.GetFiles(folder).Length, "No leaked temporary files.");
            foreach (string invalid in new[] { "{}", "null", "[]", "{\"monitoringGain\":true}", "{\"monitoringGain\":\"0.5\"}", "{\"monitoringGain\":1.01}", "{\"monitoringGain\":1e309}", "{\"monitoringGain\":0,\"monitoringGain\":1}", "{\"monitoringGain\":1,\"extra\":0}", new string(' ', 4097) })
            { File.WriteAllText(path, invalid); Assert.IsFalse(store.Load().Success, invalid[..Math.Min(80, invalid.Length)]); }
            Assert.IsTrue(store.Save(1).Success); Assert.AreEqual(1d, store.Load().Value);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
