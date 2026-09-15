using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PeripheralBatteryMonitor.Diagnostics
{
    /// <summary>
    /// The app's one diagnostic channel, and the bottom of Core's dependency graph: this
    /// file depends on nothing -- not on <c>Contracts/</c>, not on <c>Hid/</c>, not on the
    /// project root -- so every other folder may call it without inverting the one-way rule
    /// the folder split exists for. Put it anywhere else and <c>Hid/</c> logging a failed
    /// CreateFile would make the dependency graph circular.
    ///
    /// <b>Always on, with no switch anywhere.</b> There is no setting, no registry value and
    /// nothing to start: the file opens itself on the first line written and rolls when it
    /// gets big. Every piece of machinery that used to stand between a user with a problem
    /// and a readable file was one more way for the log to be missing in exactly the session
    /// worth reading.
    ///
    /// It replaces <c>Debug.WriteLine</c>, which was never the diagnostic channel it looked
    /// like: <c>[Conditional("DEBUG")]</c> strips it from a Release build, which is the only
    /// build anyone reporting an issue has ever run. (<c>TRACE</c>, by contrast, *is* defined
    /// in Release by the SDK -- but nothing here calls <c>Trace</c> either, which sidesteps
    /// the whole class of hazard for free.)
    ///
    /// Hand-rolled rather than built on <c>TraceSource</c>, deliberately. Rotation is the one
    /// thing <c>System.Diagnostics</c> does not ship -- <c>TextWriterTraceListener</c> only
    /// appends -- so a custom listener would have to be written anyway, and constructing a
    /// <c>TraceSource</c> drags in <c>ConfigurationManager</c>: it loads
    /// <c>System.Configuration.dll</c> and parses <c>machine.config</c> looking for an
    /// <c>.exe.config</c> that this app is forbidden to ship (CI fails the build on any file
    /// beside the exe). That is the price of configurability we have made impossible by
    /// construction.
    /// </summary>
    public static class Log
    {
            //Every write goes through this, including the ones from WinRT watcher threads --
            //a Bluetooth callback constructs a BatteryDevice, which calls providers, which
            //log. Nothing here may interleave two lines. It also covers the roll-over, so a
            //write can never land between the rename and the reopen.
        private static readonly object gate = new object();

        private static StreamWriter writer;

            //Bytes in the file the writer currently holds, counted rather than queried so the
            //roll check costs nothing. Seeded from the file's existing length on open, because
            //appending to a 900 KB log must roll after 100 KB and not after another megabyte.
        private static long written;

            //Latched the first time the file cannot be written. Not a user-facing switch --
            //there is none any more -- but a way to stop after the first failure instead of
            //throwing a first-chance exception on every poll tick for the rest of the session,
            //which makes the app look pathological under a debugger.
        private static bool disabled;

            //One generation of rotation. The session worth reading is usually the one before
            //the restart, and two files is as much history as a support log needs. At the
            //default five-minute poll this is roughly a week.
        private const long MAX_BYTES = 1024 * 1024;

            //Wide enough for the longest banner line, so the block reads as a block.
        private const string SEPARATOR = "-------------------------------------------------------------------------------";

        /// <summary>
        /// <c>%LOCALAPPDATA%\PeripheralBatteryMonitor\log.txt</c>.
        ///
        /// Deliberately not next to the executable. The README tells users to download one
        /// bare .exe and they routinely leave it in Downloads, drop it in Program Files or
        /// run it off a share -- all places where creating a file beside it fails or lands
        /// somewhere the user will never find. A logger that cannot open its own file is
        /// worse than no logger at all.
        ///
        /// Reading a known folder is not a UI call, so Core may do this -- and Core now owns
        /// the whole thing, since there is no longer a switch for the App to hold.
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
                    //GetFolderPath returns "" rather than throwing when the profile is
                    //damaged or the folder is unavailable. Path.Combine("", x) then yields a
                    //*relative* path, which would put the log next to the working directory --
                    //wherever a shortcut's "Start in" happens to point. Harmless while the log
                    //was opt-in and off by default; reachable on every such machine now that
                    //it always runs.
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (String.IsNullOrEmpty(local) || !Path.IsPathRooted(local))
                    local = Path.GetTempPath();

                return Path.Combine(local, "PeripheralBatteryMonitor");
            }
        }

        /// <summary>
        /// One line: <c>2026-09-11 14:03:22.417 [ 1] [Logitech] message</c>. The category is
        /// the bracketed prefix the code already used with <c>Debug.WriteLine</c>; the number
        /// before it is the managed thread id, because WinRT <c>DeviceWatcher</c> callbacks
        /// log from arbitrary threads and the file would otherwise interleave with no way to
        /// see that it had.
        /// </summary>
        public static void Write(string category, string message)
        {
            lock (gate)
            {
                if (!EnsureOpen())
                    return;

                try
                {
                    writer.WriteLine("{0} [{1,2}] [{2}] {3}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        Thread.CurrentThread.ManagedThreadId, category, message);

                        //Close enough: the exact byte count depends on the encoding and the
                        //newline, and being a few bytes out only moves where the roll happens.
                    written += message.Length + category.Length + 40;
                    if (written >= MAX_BYTES)
                        Roll();
                }
                catch (Exception)
                {
                        //Disk full, file deleted under us, permissions changed. Give up for
                        //the rest of the session rather than throwing on a timer tick, where
                        //nothing catches it and the process dies.
                    Close();
                    disabled = true;
                }
            }
        }

        /// <summary>
        /// A raw frame, as hex. This is what makes a vendor protocol debuggable at a distance:
        /// the person who has the hardware is not the person who can read the code.
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

            StringBuilder hex = new StringBuilder(label.Length + 3 * length);
            hex.Append(label).Append(' ').Append('[').Append(length).Append("] ");
            for (int i = 0; i < length; i++)
            {
                if (i > 0)
                    hex.Append(' ');
                hex.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            Write(category, hex.ToString());
        }

        /// <summary>
        /// Open the file on first use. Called under <see cref="gate"/>.
        ///
        /// Lazily, and never from a static constructor or a field initializer: a throw there
        /// would poison the type, and then every one of the several dozen unguarded call
        /// sites -- on the poll tick and on WinRT callback threads -- would start raising
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
                //FileShare.Delete is load-bearing now that the handle is held for the life of
                //a process that runs for weeks: without it Explorer refuses to delete the log
                //while the app is running, and deleting it is the only opt-out a user has left.
                //A mid-session delete silently loses lines until the next roll reopens the
                //file, which is the right trade for being able to get rid of it at all.
            FileStream stream = new FileStream(path, mode, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            written = stream.Length;

                //AutoFlush, because a tray app is ended from Task Manager or by a logoff, not
                //by a clean shutdown -- and the unflushed tail is exactly the part worth
                //reading. This is a WriteFile per line, not a FlushFileBuffers, so it stays
                //cheap even where AppData is redirected to a network share.
            writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.AutoFlush = true;

            WriteBanner();
        }

        /// <summary>
        /// The header block every generation of the file opens with: which build wrote this,
        /// onto what, and where to send it. Called from <see cref="Open"/> under
        /// <see cref="gate"/>, which is what makes it the first thing in the file -- the log
        /// opens on the first line anything writes, and that line is a HID enumeration from
        /// the <c>Settings</c> constructor, well before the startup snapshot gets a turn.
        ///
        /// <b>Here rather than in <c>DiagnosticReport</c>, which used to print a shorter
        /// version of it.</b> Two reasons, and the second is the one that matters. It was
        /// several lines *into* the file, under the traffic that had already opened it. And
        /// it was written once per process, so a log that rolled during a long session -- the
        /// long sessions being the ones worth reading -- arrived with nothing in it saying
        /// what build, what OS or what version produced any of it.
        ///
        /// Its own try/catch, because <see cref="EnsureOpen"/>'s treats a throw as "this
        /// machine cannot be logged to" and latches the whole channel off. A header is never
        /// worth losing the log over.
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
                    + " local (UTC" + DateTime.Now.ToString("zzz", CultureInfo.InvariantCulture) + ")"
                    + " - every line below is local time");
                WriteRaw(SEPARATOR);
            }
            catch (Exception)
            {
                    //Reflection over the entry assembly, an attribute that is not there, a
                    //culture that will not name itself. The log carries on without a header.
            }
        }

        /// <summary>
        /// One <c>AssemblyMetadata</c> value, or null. These come from
        /// <c>Directory.Build.props</c>; there is no <c>AssemblyInfo.cs</c> in this repo and
        /// no file beside the exe to read either, so this is the only channel build-time
        /// facts have into a running copy.
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
        /// A line with no timestamp, thread id or category -- the banner is a block about the
        /// file as a whole, not an event in it. Called under <see cref="gate"/> with
        /// <see cref="writer"/> already open.
        ///
        /// It does not roll. <see cref="Roll"/> is what calls <see cref="Open"/>, which calls
        /// the banner, so rolling here would recurse; the next ordinary
        /// <see cref="Write"/> does it instead, a few dozen bytes late.
        /// </summary>
        private static void WriteRaw(string line)
        {
            writer.WriteLine(line);
            written += line.Length + 2;
        }

        /// <summary>
        /// Start a new file, keeping one previous generation. Called under <see cref="gate"/>.
        ///
        /// <b>The writer has to be closed first.</b> Renaming a file this process holds open
        /// fails with a sharing violation, which would trip the give-up latch and stop the
        /// log dead the first time it reached the cap -- silently, which is worse than growing
        /// unbounded. (The handle does carry FileShare.Delete, so the rename would in fact
        /// succeed; closing first is still the shape that can be reasoned about, and it is the
        /// only way the reopened writer's byte count starts from zero.)
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
                    //Something is holding the previous generation -- a text editor with it
                    //open, an antivirus mid-scan. Fall through and truncate instead.
            }

            try
            {
                    //Truncate when the rename did not work, rather than reopening in append
                    //mode. Appending would put the byte count straight back over the cap and
                    //every subsequent line would attempt the same doomed rename; losing the
                    //history is the worse outcome, but a log that grows without limit on a
                    //machine where nobody asked for one is worse still.
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
