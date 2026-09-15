using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PeripheralBatteryMonitor
{
    static class Program
    {
        private const string SingleInstanceMutex = @"Local\o0Zz.PeripheralBatteryMonitor";

        [STAThread]
        static void Main()
        {
            bool isFirstInstance;
            using (Mutex mutex = new Mutex(true, SingleInstanceMutex, out isFirstInstance))
            {
                if (!isFirstInstance)
                    return;

                EmbeddedAssemblies.Install();
                Run();

                    //Held for the complete message-loop lifetime, though otherwise unused.
                GC.KeepAlive(mutex);
            }
        }

            //Split out and never inlined: Run mentions Settings, whose fields are Core types,
            //so the JIT would try to load PeripheralBatteryMonitor.Core before Main's first
            //statement had a chance to install the resolver.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run()
        {
                //No Application.SetHighDpiMode on net48 -- the DPI mode comes from app.manifest.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

                //Before any window exists. Every form reads its text in its constructor, so a
                //language chosen after the first one is built would arrive too late for it --
                //and the Settings form is built once and kept alive for the whole session.
            Strings.Use(ReadLanguage());

            Application.Run(new Settings());
        }

        /// <summary>
        /// Read here rather than in the Settings form, where every other setting is loaded: this
        /// one has to be known before that form's constructor runs. The form still writes it.
        /// </summary>
        private static string ReadLanguage()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Settings.RegistryPath, false))
                {
                    if (key == null)
                        return "";
                    object value = key.GetValue("Language", "");
                    return value == null ? "" : value.ToString();
                }
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
