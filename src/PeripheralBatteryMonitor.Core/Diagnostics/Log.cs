using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PeripheralBatteryMonitor.Diagnostics
{
    /// <summary>
    /// The app's one diagnostic channel, and the bottom of Core's dependency graph: it depends
    /// on nothing, so every other folder may call it without inverting the one-way rule the
    /// folder split exists for.
    ///
    /// <b>Always on, with no switch anywhere.</b> The file opens itself on the first line
    /// written and rolls when it gets big. Every piece of machinery between a user with a
    /// problem and a readable file was one more way for the log to be missing in exactly the
    /// session worth reading.
    ///
    /// Hand-rolled rather than <c>TraceSource</c>: rotation is the one thing
    /// <c>System.Diagnostics</c> does not ship, so a custom listener gets written either way --
    /// and constructing a <c>TraceSource</c> drags in <c>ConfigurationManager</c>, which hunts
    /// for an <c>.exe.config</c> this app is forbidden to ship. <c>Debug.WriteLine</c>, which
    /// this replaced, is <c>[Conditional("DEBUG")]</c> and so absent from the only build anyone
    /// reporting an issue ever runs.
    /// </summary>
    public static class Log
    {
            //Also covers the roll-over, so a write can never land between the rename and the
            //reopen. WinRT watcher threads log through here too.
        private static readonly object gate = new object();

        private static StreamWriter writer;

            //Counted rather than queried, so the roll check costs nothing. Seeded from the
            //file's existing length on open, because appending to a 900 KB log must roll after
            //100 KB and not after another megabyte.
        private static long written;

            //Latched on the first write failure, so a full disk does not throw a first-chance
            //exception on every poll tick for the rest of the session.
        private static bool disabled;

            //One generation of rotation: the session worth reading is usually the one before
            //the restart. At the default five-minute poll this is roughly a week.
        private const long MAX_BYTES = 1024 * 1024;

        private const string SEPARATOR = "-------------------------------------------------------------------------------";

        /// <summary>
        /// Deliberately not next to the executable: users download one bare .exe and leave it
        /// in Downloads, Program Files or on a share -- all places where creating a file beside
        /// it fails or lands somewhere they will never find it.
        /// </summary>
        public static string FilePath
        {
            get { return Path.Combine(DirectoryPath, "log.txt"); }
        }

        /// <summary>The folder <see cref="FilePath"/> lives in, which is what the tray entry opens.</summary>
        public static string DirectoryPath
        {
            get
            {
                    //GetFolderPath returns "" rather than throwing when the profile is damaged.
                    //Path.Combine("", x) then yields a *relative* path, putting the log wherever
                    //a shortcut's "Start in" happens to point.
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (String.IsNullOrEmpty(local) || !Path.IsPathRooted(local))
                    local = Path.GetTempPath();

                return Path.Combine(local, "PeripheralBatteryMonitor");
            }
        }

            //Whose device the current thread is working on, if anything said so. ThreadStatic
            //because the poll tick and a WinRT watcher callback can both be inside a read at
            //once, on different threads, and a shared field would label one with the other's
            //device.
        [ThreadStatic] private static string scope;

        /// <summary>
        /// Name the device every line written on this thread belongs to, until the returned
        /// handle is disposed.
        ///
        /// The low-level lines are the ones that need it: <c>[Centurion] -&gt; [8] 51 06 ...</c>
        /// says nothing about *which* device answered, and a transport cannot say -- it holds a
        /// handle, not a device. Threading a name through every transport and provider signature
        /// to reach the log would be a worse trade than an ambient one set at the single place
        /// that knows it, <c>BatteryDevice.UpdateBatteryLevel</c>.
        /// </summary>
        public static IDisposable Scope(string device)
        {
            Restore restore = new Restore(scope);
            scope = device;
            return restore;
        }

        private class Restore : IDisposable
        {
            private readonly string previous;
            private bool done;

            public Restore(string previous)
            {
                this.previous = previous;
            }

            public void Dispose()
            {
                    //Nested scopes restore in order; disposing twice must not resurrect one.
                if (done)
                    return;
                done = true;
                scope = previous;
            }
        }

            //Wide enough for the longest category in the tree ("SteelSeries"). A longer value
            //is cut to fit: the columns are the point, and one ragged line breaks the eye's
            //run down the file.
        private const int CATEGORY_WIDTH = 11;
        private const int DEVICE_WIDTH = 24;

            //A cut name ends in this, so it can never be read as a different, shorter device.
            //Nothing is lost by cutting: the full name is printed once by the Discovery line
            //that first reports the interface.
        private const char TRUNCATED = '…';

        /// <summary>
        /// One line, in fixed columns so the eye can run down one of them:
        /// <c>2026-09-11 14:03:22.417 |  1 | Centurion   | PRO X 2 LIGHTSPEED       | -&gt; [8] 51 …</c>
        ///
        /// The thread id is a column of its own rather than a detail of the message because
        /// WinRT <c>DeviceWatcher</c> callbacks log from arbitrary threads and the file
        /// interleaves; the device is the <see cref="Scope"/>, blank when nothing set one.
        /// </summary>
        public static void Write(string category, string message)
        {
            string device = scope;

            lock (gate)
            {
                if (!EnsureOpen())
                    return;

                try
                {
                    writer.WriteLine("{0} | {1,2} | {2} | {3} | {4}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        Thread.CurrentThread.ManagedThreadId,
                        Column(category, CATEGORY_WIDTH),
                        Column(device, DEVICE_WIDTH),
                        message);

                        //Close enough: being a few bytes out only moves where the roll happens.
                    written += message.Length + CATEGORY_WIDTH + DEVICE_WIDTH + 40;
                    if (written >= MAX_BYTES)
                        Roll();
                }
                catch (Exception)
                {
                        //Give up for the session rather than throw on a timer tick, where
                        //nothing catches it and the process dies.
                    Close();
                    disabled = true;
                }
            }
        }

        /// <summary>Exactly <paramref name="width"/> characters, padded or cut to fit.</summary>
        private static string Column(string value, int width)
        {
            if (String.IsNullOrEmpty(value))
                return new string(' ', width);
            if (value.Length < width)
                return value.PadRight(width);
            if (value.Length == width)
                return value;
            return value.Substring(0, width - 1) + TRUNCATED;
        }

        /// <summary>
        /// A raw frame as hex -- what makes a vendor protocol debuggable at a distance, since
        /// the person with the hardware is not the person who can read the code.
        /// </summary>
        public static void WriteHex(string category, string label, byte[] data, int length)
        {
            if (data == null)
            {
                Write(category, label + " <null>");
                return;
            }

            if (length < 0 || length > data.Length)
                length = data.Length;

                //The declared length is printed, so the zero padding a fixed-size report is
                //filled out with adds nothing but width -- two thirds of a HID++ long frame.
            int meaningful = length;
            while (meaningful > 0 && data[meaningful - 1] == 0)
                meaningful--;

            StringBuilder hex = new StringBuilder(label.Length + 3 * meaningful);
            hex.Append(label).Append(' ').Append('[').Append(length).Append("] ");
            for (int i = 0; i < meaningful; i++)
            {
                if (i > 0)
                    hex.Append(' ');
                hex.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            Write(category, hex.ToString());
        }

        /// <summary>
        /// Lazily, and never from a static constructor or field initializer: a throw there would
        /// poison the type, and every one of the dozens of unguarded call sites would then raise
        /// <c>TypeInitializationException</c>. That is a crashing tray app, not a missing log.
        /// </summary>
        private static bool EnsureOpen()
        {
            if (disabled)
                return false;
            if (writer != null)
                return true;

            try
            {
                Directory.CreateDirectory(DirectoryPath);
                Open(FilePath, FileMode.Append);
                return true;
            }
            catch (Exception)
            {
                Close();
                disabled = true;
                return false;
            }
        }

        private static void Open(string path, FileMode mode)
        {
                //FileShare.Delete is load-bearing: without it Explorer refuses to delete the log
                //while the app runs, and deleting it is the only opt-out a user has left. A
                //mid-session delete then silently loses lines until the next roll, which is the
                //right trade for being able to get rid of it at all.
            FileStream stream = new FileStream(path, mode, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            written = stream.Length;

                //A tray app is ended from Task Manager or by a logoff, and the unflushed tail is
                //exactly the part worth reading. A WriteFile per line, not a FlushFileBuffers, so
                //it stays cheap even where AppData is redirected to a share.
            writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.AutoFlush = true;

            WriteBanner();
        }

        /// <summary>
        /// The header block every generation of the file opens with. Called from
        /// <see cref="Open"/>, which is what makes it the first thing in the file -- the log
        /// opens on the first line anything writes, well before the startup snapshot gets a turn.
        ///
        /// <b>Here rather than in <c>DiagnosticReport</c>, which used to print a shorter
        /// version.</b> That one was several lines *into* the file, under the traffic that had
        /// already opened it, and written once per process -- so a log that rolled during a long
        /// session arrived with nothing in it naming the build, OS or version behind any of it.
        ///
        /// Its own try/catch, because <see cref="EnsureOpen"/>'s treats a throw as "this machine
        /// cannot be logged to" and latches the whole channel off.
        /// </summary>
        private static void WriteBanner()
        {
            try
            {
                Assembly entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                AssemblyName name = entry.GetName();

                string repository = Metadata(entry, "RepositoryUrl");
                string built = Metadata(entry, "BuildDate");

                WriteRaw(SEPARATOR);
                WriteRaw(name.Name + " " + name.Version
                    + (built == null ? "" : " (build date: " + built + ")")
                    + (repository == null ? "" : " - " + repository));
                WriteRaw("OS version: " + Environment.OSVersion
                    + " - 64-bit OS: " + Environment.Is64BitOperatingSystem
                    + ", 64-bit process: " + Environment.Is64BitProcess);
                WriteRaw("CLR version: " + Environment.Version
                    + " - Culture: ui=" + CultureInfo.CurrentUICulture.Name
                    + ", formatting=" + CultureInfo.CurrentCulture.Name);
                WriteRaw("Log opened: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " local (UTC" + DateTime.Now.ToString("zzz", CultureInfo.InvariantCulture) + ")");
                WriteRaw(SEPARATOR);

                    //Names the columns every line below is written in, so the layout documents
                    //itself in the one place a reader always starts.
                WriteRaw(String.Format("{0} | {1,2} | {2} | {3} | {4}",
                    "date       time        ", "th",
                    Column("category", CATEGORY_WIDTH), Column("device", DEVICE_WIDTH), "message"));
                WriteRaw(SEPARATOR);
            }
            catch (Exception)
            {
                    //The log carries on without a header.
            }
        }

        /// <summary>
        /// From <c>Directory.Build.props</c>. There is no <c>AssemblyInfo.cs</c> here and no
        /// file beside the exe to read, so this is the only channel build-time facts have into a
        /// running copy.
        /// </summary>
        private static string Metadata(Assembly assembly, string key)
        {
            foreach (AssemblyMetadataAttribute attribute in
                     (AssemblyMetadataAttribute[])assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
            {
                if (String.Equals(attribute.Key, key, StringComparison.Ordinal)
                    && !String.IsNullOrWhiteSpace(attribute.Value))
                    return attribute.Value;
            }
            return null;
        }

        /// <summary>
        /// No timestamp, thread id or category: the banner is a block about the file, not an
        /// event in it.
        ///
        /// It does not roll. <see cref="Roll"/> calls <see cref="Open"/>, which calls the
        /// banner, so rolling here would recurse; the next ordinary <see cref="Write"/> does it
        /// instead, a few dozen bytes late.
        /// </summary>
        private static void WriteRaw(string line)
        {
            writer.WriteLine(line);
            written += line.Length + 2;
        }

        /// <summary>
        /// <b>The writer has to be closed first.</b> Renaming a file this process holds open
        /// fails with a sharing violation, which would trip the give-up latch and stop the log
        /// dead the first time it reached the cap. (The handle does carry FileShare.Delete, so
        /// the rename would in fact succeed -- but closing first is the only way the reopened
        /// writer's byte count starts from zero.)
        /// </summary>
        private static void Roll()
        {
            string path = FilePath;
            string previous = Path.ChangeExtension(path, ".1.txt");

            Close();

            bool rotated = false;
            try
            {
                if (File.Exists(previous))
                    File.Delete(previous);      //no File.Move(src, dst, overwrite) on .NET Framework
                File.Move(path, previous);
                rotated = true;
            }
            catch (Exception)
            {
                    //A text editor or antivirus holding the previous generation. Truncate
                    //instead.
            }

            try
            {
                    //Truncate rather than reopen in append mode: appending would put the byte
                    //count straight back over the cap and every subsequent line would attempt
                    //the same doomed rename.
                Open(path, rotated ? FileMode.Append : FileMode.Create);
            }
            catch (Exception)
            {
                Close();
                disabled = true;
            }
        }

        private static void Close()
        {
            if (writer == null)
                return;

            try { writer.Dispose(); }
            catch (Exception) { }
            writer = null;
        }
    }
}
