using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using PeripheralBatteryMonitor.Diagnostics;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// Writes an unhandled exception to the diagnostic log before it takes the process down.
    ///
    /// Without this a crash is invisible: the log simply stops mid-session, which is
    /// indistinguishable from the user closing the app or killing it from Task Manager --
    /// and the person with the crash is not the person who can read the code.
    /// </summary>
    internal static class CrashLog
    {
        /// <summary>
        /// Call from <see cref="Program"/> after <see cref="EmbeddedAssemblies.Install"/> and
        /// before the first window: naming <see cref="Log"/> loads Core, so this cannot run any
        /// earlier, and <see cref="Application.SetUnhandledExceptionMode"/> must precede every
        /// window for the WinForms half to take effect.
        /// </summary>
        public static void Install()
        {
                //Explicit rather than left at Automatic, which consults an .exe.config this app
                //is forbidden to ship -- so the mode would depend on a file that is never there.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        }

        /// <summary>
        /// The UI thread, which is where the poll tick and every HID transaction run. This one
        /// is survivable and deliberately survived: a throw on one tick must not cost the user
        /// their tray icon and the menu that reaches Exit. Returning from here swallows it, so
        /// the log line is the only record that it happened.
        /// </summary>
        private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            Write("unhandled exception on the UI thread (recovered)", e.Exception);
        }

        /// <summary>
        /// Any other thread -- a WinRT <c>DeviceWatcher</c> callback, or the worker that
        /// restarts the radio. Nothing can stop the process here; this only gets the reason
        /// into the file first. <c>Log</c> is AutoFlush, so the line is on disk before the CLR
        /// tears the process down.
        /// </summary>
        private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Write(e.IsTerminating ? "unhandled exception, process is terminating" : "unhandled exception",
                  e.ExceptionObject as Exception);

            if (!(e.ExceptionObject is Exception))
                Log.Write("Crash", "non-Exception thrown: " + e.ExceptionObject);
        }

        /// <summary>
        /// A faulted Task nobody waited on. Not fatal on net48 by default, but it is the one
        /// failure the synchronous <c>AsTask().Wait(timeout)</c> pattern throughout Core can
        /// produce silently: a call that times out leaves its task running, and whatever it
        /// throws afterwards lands here and nowhere else.
        /// </summary>
        private static void OnUnobservedTask(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Write("unobserved task exception", e.Exception);
            e.SetObserved();
        }

        private static void Write(string what, Exception e)
        {
                //ToString(), not Message: the stack trace and the inner exceptions are the whole
                //point, and an AggregateException's own Message names nothing useful.
            Log.Write("Crash", "---- " + what + " ----");
            Log.Write("Crash", e == null ? "(no exception object)" : e.ToString());
        }
    }
}
