using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// The interface language: every piece of text the windows show.
    ///
    /// One <c>key = value</c> file per language, embedded in the exe. **Not .resx**: satellite
    /// assemblies are DLLs in subfolders and this application ships as a single file, so they
    /// are not an option here — and a plain text file is something a translator can open
    /// without Visual Studio.
    ///
    /// <c>en.lang</c> is the master and the per-key fallback, so a half-finished translation
    /// shows English rather than raw key names.
    /// </summary>
    internal static class Strings
    {
        private const string Prefix = "PeripheralBatteryMonitor.Languages.";

        private const string Suffix = ".lang";

        private const string FallbackCode = "en";

        private static readonly Dictionary<string, string> english =
            Load(FallbackCode) ?? new Dictionary<string, string>(StringComparer.Ordinal);

        private static Dictionary<string, string> current = english;

        internal static IList<Language> Available
        {
            get
            {
                List<Language> found = new List<Language>();

                foreach (string resource in typeof(Strings).Assembly.GetManifestResourceNames())
                {
                    if (!resource.StartsWith(Prefix, StringComparison.Ordinal) ||
                        !resource.EndsWith(Suffix, StringComparison.Ordinal))
                        continue;

                    string code = resource.Substring(Prefix.Length, resource.Length - Prefix.Length - Suffix.Length);
                    Dictionary<string, string> table = Load(code);
                    if (table == null)
                        continue;

                        //Named in its own language, never translated: someone looking for their
                        //language in an interface they cannot read is looking for the word
                        //"Français", not for "French".
                    string name;
                    found.Add(new Language(code, table.TryGetValue("@name", out name) ? name : code));
                }

                found.Sort(delegate (Language a, Language b)
                {
                    return String.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                });
                return found;
            }
        }

        /// <summary>
        /// Null or empty follows Windows; an unknown code falls back to English.
        /// </summary>
        internal static void Use(string code)
        {
            string wanted = String.IsNullOrWhiteSpace(code)
                ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                : code.Trim().ToLowerInvariant();

            Dictionary<string, string> table = Load(wanted);
            if (table == null)
            {
                wanted = FallbackCode;
                table = english;
            }

            current = table;

                //UI culture only, never CurrentCulture. CurrentCulture decides how numbers parse
                //and format, and this app talks to devices over binary protocols and writes
                //DWORDs to the registry -- none of that may change because the window is in
                //German. The language of the interface must not reach anything but the interface.
            Thread.CurrentThread.CurrentUICulture = ResolveCulture(wanted);
        }

        /// <summary>
        /// The translation, else the English text, else the key itself -- visible, never a crash.
        /// </summary>
        internal static string Get(string key)
        {
            string text;
            if (current.TryGetValue(key, out text) || english.TryGetValue(key, out text))
                return text;
            return key;
        }

        internal static string Format(string key, params object[] args)
        {
            string template = Get(key);
            try
            {
                return String.Format(CultureInfo.CurrentCulture, template, args);
            }
            catch (FormatException)
            {
                    //A translator mangled a placeholder. Showing the raw template is ugly but it
                    //is still readable text, which is better than taking the tray app down.
                return template;
            }
        }

        private static Dictionary<string, string> Load(string code)
        {
            using (Stream stream = typeof(Strings).Assembly.GetManifestResourceStream(Prefix + code + Suffix))
            {
                if (stream == null)
                    return null;

                Dictionary<string, string> table = new Dictionary<string, string>(StringComparer.Ordinal);

                    //UTF-8, and detectEncodingFromByteOrderMarks left on so a translator who
                    //saved with a BOM in Notepad is not punished for it.
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed[0] == '#')
                            continue;

                        int equals = trimmed.IndexOf('=');
                        if (equals <= 0)
                            continue;

                            //Everything after the first '=' is the text, so '=' needs no escaping.
                        table[trimmed.Substring(0, equals).Trim()] = Unescape(trimmed.Substring(equals + 1).Trim());
                    }
                }

                return table;
            }
        }

        private static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0)
                return value;

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(value[i]);
                    continue;
                }

                i++;
                switch (value[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    default: builder.Append(value[i]); break;
                }
            }
            return builder.ToString();
        }

        private static CultureInfo ResolveCulture(string code)
        {
            try
            {
                return CultureInfo.GetCultureInfo(code);
            }
            catch (CultureNotFoundException)
            {
                    //A language file named after something Windows does not know as a culture.
                    //The text still works; only the culture object is unavailable.
                return CultureInfo.InvariantCulture;
            }
        }

        internal sealed class Language
        {
            internal string Code { get; private set; }

            internal string Name { get; private set; }

            internal Language(string code, string name)
            {
                this.Code = code;
                this.Name = name;
            }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}
