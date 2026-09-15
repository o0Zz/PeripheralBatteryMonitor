namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Converts a single-cell Li-Po terminal voltage into a rough charge percentage, for
    /// devices that only implement ADC_MEASUREMENT: the mapping lives in the vendor's software,
    /// not in the device.
    ///
    /// Solaar's table, and an <b>approximation</b> -- flat between roughly 3.7 V and 4.0 V, so
    /// a few millivolts of noise there move the result by several percent, and it accounts for
    /// neither load nor temperature. Expect the right band, not the same number G HUB shows.
    ///
    /// Must stay ordered from highest voltage to lowest.
    /// </summary>
    public static class LogitechVoltageCurve
    {
        private static readonly int[,] curve = new int[,]
        {
            //  mV,  percent
            { 4186, 100 },
            { 4067,  90 },
            { 3989,  80 },
            { 3922,  70 },
            { 3859,  60 },
            { 3811,  50 },
            { 3778,  40 },
            { 3751,  30 },
            { 3717,  20 },
            { 3671,  10 },
            { 3646,   5 },
            { 3579,   2 },
            { 3500,   0 },
        };

        /// <summary>
        /// Values outside the curve clamp to 100 / 0; in between, the bracketing points are
        /// interpolated so the reading moves smoothly instead of snapping between rows.
        /// </summary>
        public static int ToPercentage(int millivolts)
        {
            int last = curve.GetLength(0) - 1;

            if (millivolts >= curve[0, 0])
                return curve[0, 1];
            if (millivolts <= curve[last, 0])
                return curve[last, 1];

            for (int i = 0; i < last; i++)
            {
                int highMv = curve[i, 0], highPct = curve[i, 1];
                int lowMv = curve[i + 1, 0], lowPct = curve[i + 1, 1];

                if (millivolts > lowMv)
                {
                    int span = highMv - lowMv;
                    if (span <= 0)
                        return lowPct;
                    return lowPct + ((millivolts - lowMv) * (highPct - lowPct)) / span;
                }
            }

            return curve[last, 1];
        }
    }
}
