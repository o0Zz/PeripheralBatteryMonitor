using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using PeripheralBatteryMonitor.Diagnostics;

namespace System.Runtime.CompilerServices
{
        //net48 does not ship it; the compiler only looks the attribute up by name.
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace PeripheralBatteryMonitor.Tests
{
    /// <summary>
    /// The decoders log every frame, and <see cref="Log"/> has no switch by design. Without
    /// this, a test run appends to the user's real <c>%LOCALAPPDATA%</c> log -- possibly while
    /// the tray app is writing to it.
    /// </summary>
    internal static class SilenceLog
    {
        [ModuleInitializer]
        internal static void Install()
        {
            FieldInfo disabled = typeof(Log).GetField("disabled", BindingFlags.NonPublic | BindingFlags.Static);
            if (disabled == null)
                throw new InvalidOperationException("Log.disabled is gone; tests would write to the real log file.");
            disabled.SetValue(null, true);
        }
    }
}
