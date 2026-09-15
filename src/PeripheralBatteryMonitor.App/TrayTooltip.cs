using System;
using System.Collections.Generic;
using System.Text;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// Turns one line per device into the single string <c>NotifyIcon.Text</c> can hold.
    ///
    /// <b>That is a 64-character buffer *including* its terminator</b>, so 63 is the most
    /// WinForms accepts -- it throws <c>ArgumentOutOfRangeException</c> at 64, on the polling
    /// tick, which kills the tray app. <see cref="Fit"/> is the only thing allowed to build it.
    /// </summary>
    internal static class TrayTooltip
    {
        private const int Limit = 63;
        private const string More = "…";

        /// <summary>
        /// One device's line, plus where the name sits inside it, so the name can be shortened
        /// without touching the reading beside it. Located once at construction, because the
        /// surrounding wording is translated and cannot be assumed to be a prefix or a suffix.
        /// </summary>
        internal sealed class Line
        {
            internal readonly string Name;
            internal readonly string Text;
            private readonly int nameIndex;

            internal Line(string name, string text)
            {
                Name = name ?? "";
                Text = text ?? "";
                nameIndex = Name.Length == 0
                    ? -1
                    : Text.IndexOf(Name, StringComparison.Ordinal);
            }

            internal int NameLength { get { return nameIndex < 0 ? 0 : Name.Length; } }

            internal int MinimumLength
            {
                get { return nameIndex < 0 ? Text.Length : Text.Length - Name.Length + More.Length; }
            }

            internal string Render(int nameLength)
            {
                if (nameIndex < 0 || nameLength >= Name.Length)
                    return Text;

                    //Every shortened name ends in "…", so it can never masquerade as a
                    //different full device name.
                string shortened = nameLength <= More.Length
                    ? More
                    : Name.Substring(0, nameLength - More.Length) + More;

                return Text.Substring(0, nameIndex) + shortened +
                    Text.Substring(nameIndex + Name.Length);
            }
        }

        /// <summary>
        /// Three escalating steps: everything fits; else shorten names fairly (round-robin, so
        /// no line is starved) and mark each shortened one, which keeps every device *and* every
        /// reading represented; else fall back to the caller's order, whole lines only, closing
        /// with an ellipsis line.
        ///
        /// That last step never cuts inside a line -- "Logi M650 L: 80%" cut mid-name reads as a
        /// different device -- and stops at the first line that does not fit rather than skipping
        /// to a shorter one, because the caller's order is a priority order.
        /// </summary>
        internal static string Fit(IList<Line> lines)
        {
            if (lines.Count == 0)
                return "";

            string all = Join(lines, null);
            if (all.Length <= Limit)
                return all;

            int minimum = lines.Count - 1; //newline separators
            foreach (Line line in lines)
                minimum += line.MinimumLength;

            if (minimum <= Limit)
                return Join(lines, ShareNameSpace(lines, Limit - minimum));

            int taken;
            string text = TakeLines(lines, Limit, out taken);
            if (taken == lines.Count)
                return text;

                //Reserved before the fit, not carved out of it.
            text = TakeLines(lines, Limit - More.Length - 1, out taken);
            if (taken == 0)
                return lines[0].Text.Substring(0, Limit - More.Length) + More;

            return text + "\n" + More;
        }

        /// <summary>
        /// One character at a time across the lines that can still use one, so a long name
        /// cannot eat the budget a short one needed.
        /// </summary>
        private static int[] ShareNameSpace(IList<Line> lines, int spare)
        {
            int[] nameLengths = new int[lines.Count];
            for (int i = 0; i < lines.Count; i++)
                nameLengths[i] = lines[i].NameLength == 0 ? 0 : More.Length;

            bool expanded = true;
            while (spare > 0 && expanded)
            {
                expanded = false;
                for (int i = 0; i < lines.Count && spare > 0; i++)
                {
                    if (nameLengths[i] >= lines[i].NameLength)
                        continue;

                    nameLengths[i]++;
                    spare--;
                    expanded = true;
                }
            }

            return nameLengths;
        }

        private static string Join(IList<Line> lines, int[] nameLengths)
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i != 0)
                    text.Append('\n');

                text.Append(lines[i].Render(nameLengths == null
                    ? lines[i].NameLength
                    : nameLengths[i]));
            }
            return text.ToString();
        }

        private static string TakeLines(IList<Line> lines, int limit, out int taken)
        {
            StringBuilder text = new StringBuilder();
            taken = 0;

            foreach (Line line in lines)
            {
                int cost = (text.Length == 0 ? 0 : 1) + line.Text.Length;
                if (text.Length + cost > limit)
                    break;

                if (text.Length != 0)
                    text.Append('\n');

                text.Append(line.Text);
                taken++;
            }

            return text.ToString();
        }
    }
}
