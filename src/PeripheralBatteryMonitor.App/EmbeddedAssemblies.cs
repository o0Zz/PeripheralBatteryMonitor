using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// Serves the application's managed dependencies out of its own resources, so
    /// PeripheralBatteryMonitor.exe ships as a single file with nothing beside it.
    /// </summary>
    internal static class EmbeddedAssemblies
    {
        private const string Prefix = "PeripheralBatteryMonitor.Embedded.";

        private static readonly Dictionary<string, Assembly> Resolved =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Must run before any method mentioning a type from an embedded assembly is
        /// JIT-compiled.
        /// </summary>
        internal static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;

                //The CLR asks for these on the first localised resource access and they always
                //miss here; answering null immediately keeps that off the resource-stream path.
            if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                return null;

            lock (Resolved)
            {
                Assembly cached;
                if (Resolved.TryGetValue(name, out cached))
                    return cached;

                Assembly loaded = Load(name);

                    //Cached even when null: the CLR keeps asking for a name that is not here.
                Resolved[name] = loaded;
                return loaded;
            }
        }

        private static Assembly Load(string name)
        {
            Assembly self = typeof(EmbeddedAssemblies).Assembly;

            using (Stream stream = self.GetManifestResourceStream(Prefix + name + ".dll"))
            {
                if (stream == null)
                    return null;

                byte[] image = new byte[stream.Length];
                int offset = 0;
                while (offset < image.Length)
                {
                    int read = stream.Read(image, offset, image.Length - offset);
                    if (read == 0)
                    {
                            //A partial image would fail far more confusingly inside Assembly.Load.
                        return null;
                    }

                    offset += read;
                }

                return Assembly.Load(image);
            }
        }
    }
}
