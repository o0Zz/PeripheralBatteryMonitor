using PeripheralBatteryMonitor.Providers;
using PeripheralBatteryMonitor.Providers.Logitech;
using PeripheralBatteryMonitor.Providers.Razer;
using PeripheralBatteryMonitor.Providers.Sony;
using PeripheralBatteryMonitor.Providers.SteelSeries;
using Xunit;

namespace PeripheralBatteryMonitor.Tests
{
    public class SonyDecoderTests
    {
        [Theory]
        [InlineData(0x08, 85)]     //discharging, 8/10
        [InlineData(0x0A, 100)]    //10/10 caps at 100, not 105
        [InlineData(0x13, 35)]     //charging, 3/10
        [InlineData(0x25, 100)]    //full
        [InlineData(0xA5, null)]   //voltage or temperature out of range
        [InlineData(0xF0, null)]   //charging error
        public void DualSense(int status, int? expected)
        {
            Assert.Equal(expected, SonyBatteryProvider.DecodeDualSense((byte)status));
        }

        [Theory]
        [InlineData(0x05, 55)]     //no cable, 5/10
        [InlineData(0x0E, 100)]    //no cable, out of range reads full
        [InlineData(0x13, 35)]     //cable, charging 3/10
        [InlineData(0x1B, 100)]    //cable, charged
        [InlineData(0x1E, null)]   //cable, charging fault
        public void DualShock4(int status, int? expected)
        {
            Assert.Equal(expected, SonyBatteryProvider.DecodeDualShock4((byte)status));
        }
    }

    public class CenturionDecoderTests
    {
        private static byte[] Frame(byte kind)
        {
            byte[] frame = new byte[CenturionBattery.FRAME_SIZE];
            frame[0] = CenturionBattery.REPORT_CENTURION;
            frame[1] = kind;
            return frame;
        }

        [Fact]
        public void BatteryFrame()
        {
            byte[] frame = Frame(0x0B);
            frame[8] = 0x04;
            frame[10] = 57;
            frame[12] = 0x02;
            Assert.Equal(57, CenturionBattery.Decode(frame, frame.Length));
        }

        [Fact]
        public void BatteryFrameWithWrongTagIsIgnored()
        {
            byte[] frame = Frame(0x0B);
            frame[8] = 0x05;
            frame[10] = 57;
            Assert.Null(CenturionBattery.Decode(frame, frame.Length));
        }

        [Fact]
        public void LevelAbove100IsRejected()
        {
            byte[] frame = Frame(0x0B);
            frame[8] = 0x04;
            frame[10] = 101;
            Assert.Null(CenturionBattery.Decode(frame, frame.Length));
        }

        [Fact]
        public void PowerEventAndAckCarryNoLevel()
        {
            Assert.Null(CenturionBattery.Decode(Frame(0x05), CenturionBattery.FRAME_SIZE));
            Assert.Null(CenturionBattery.Decode(Frame(0x03), CenturionBattery.FRAME_SIZE));
        }

        [Fact]
        public void OtherReportOrShortReadIsIgnored()
        {
            byte[] frame = Frame(0x0B);
            frame[8] = 0x04;
            frame[10] = 57;
            Assert.Null(CenturionBattery.Decode(frame, 12));

            frame[0] = 0x11;
            Assert.Null(CenturionBattery.Decode(frame, frame.Length));
        }
    }

    public class SteelSeriesParserTests
    {
        private static int? Parse(ushort productId, byte[] reply, out SteelSeriesBatteryProvider.Verdict verdict)
        {
            SteelSeriesBatteryProvider.NovaModel model = SteelSeriesBatteryProvider.ModelFor(productId);
            Assert.NotNull(model);
            return SteelSeriesBatteryProvider.Parse(reply, reply.Length, model, out verdict);
        }

        [Fact]
        public void Nova7DiscreteLevel()
        {
            SteelSeriesBatteryProvider.Verdict verdict;
            Assert.Equal(75, Parse(0x2202, new byte[] { 0, 0xB0, 0, 3, 1, 0, 0, 0 }, out verdict));
            Assert.Equal(SteelSeriesBatteryProvider.Verdict.Level, verdict);
        }

        [Fact]
        public void Nova7Offline()
        {
            SteelSeriesBatteryProvider.Verdict verdict;
            Assert.Null(Parse(0x2202, new byte[] { 0, 0xB0, 0, 3, 0, 0, 0, 0 }, out verdict));
            Assert.Equal(SteelSeriesBatteryProvider.Verdict.Offline, verdict);
        }

        [Fact]
        public void Nova7DiscreteOutOfRangeIsANotification()
        {
            SteelSeriesBatteryProvider.Verdict verdict;
            Assert.Null(Parse(0x2202, new byte[] { 0, 0xB0, 0, 5, 1, 0, 0, 0 }, out verdict));
            Assert.Equal(SteelSeriesBatteryProvider.Verdict.NotAStatusFrame, verdict);
        }

        [Fact]
        public void Nova7PercentageFirmware()
        {
            SteelSeriesBatteryProvider.Verdict verdict;
            Assert.Equal(64, Parse(0x22A1, new byte[] { 0, 0xB0, 0, 64, 1, 0, 0, 0 }, out verdict));
        }

        [Fact]
        public void Nova5Layout()
        {
            SteelSeriesBatteryProvider.Verdict verdict;
            Assert.Equal(88, Parse(0x2232, new byte[] { 0, 0xB0, 0x01, 0, 88, 0, 0, 0 }, out verdict));
            Assert.Null(Parse(0x2232, new byte[] { 0, 0xB0, 0x02, 0, 88, 0, 0, 0 }, out verdict));
            Assert.Equal(SteelSeriesBatteryProvider.Verdict.Offline, verdict);
        }

        [Fact]
        public void UnknownProductHasNoModel()
        {
            Assert.Null(SteelSeriesBatteryProvider.ModelFor(0x0001));
        }
    }

    public class RazerTests
    {
        [Theory]
        [InlineData(255, 100)]
        [InlineData(128, 50)]
        [InlineData(1, 1)]         //rounds to 0, kept above flat
        [InlineData(0, null)]      //mouse off answers 0
        public void ToPercentage(int raw, int? expected)
        {
            Assert.Equal(expected, RazerBatteryProvider.ToPercentage((byte)raw, "test"));
        }

        [Fact]
        public void RequestFrame()
        {
                //Battery level request: class 0x07, id 0x80, two argument bytes.
            byte[] wire = RazerReport.Build(0x1F, 0x07, 0x80, 0x02);

            Assert.Equal(RazerReport.WIRE_LENGTH, wire.Length);
            Assert.Equal(0x00, wire[0]);    //report id
            Assert.Equal(0x00, wire[1]);    //status: new command
            Assert.Equal(0x1F, wire[2]);    //transaction id
            Assert.Equal(0x02, wire[6]);    //data size
            Assert.Equal(0x07, wire[7]);    //command class
            Assert.Equal(0x80, wire[8]);    //command id
            Assert.Equal(0x02 ^ 0x07 ^ 0x80, wire[89]);
        }

        [Fact]
        public void ChecksumSkipsStatusAndTransactionId()
        {
            byte[] wire = new byte[RazerReport.WIRE_LENGTH];
            wire[1] = 0xFF;
            wire[2] = 0xFF;
            Assert.Equal(0x00, RazerReport.Checksum(wire));
        }
    }

    public class HidppTests
    {
        [Fact]
        public void ShortFrameIsRepublishedLong()
        {
            byte[] shortFrame = { 0x10, 0x01, 0x8F, 0x11, 0x22, 0x33, 0x44 };
            byte[] normalised = HidppTransport.Normalise(shortFrame, shortFrame.Length, 0x01);

            Assert.Equal(HidppTransport.LONG_FRAME_SIZE, normalised.Length);
            Assert.Equal(HidppTransport.REPORT_LONG, normalised[0]);
            Assert.Equal(new byte[] { 0x01, 0x8F, 0x11, 0x22, 0x33, 0x44, 0x00 }, Slice(normalised, 1, 7));
        }

        [Fact]
        public void LongFramePassesThrough()
        {
            byte[] longFrame = new byte[HidppTransport.LONG_FRAME_SIZE];
            longFrame[0] = HidppTransport.REPORT_LONG;
            Assert.Same(longFrame, HidppTransport.Normalise(longFrame, longFrame.Length, 0xFF));
        }

        [Theory]
        [InlineData(4300, 100)]
        [InlineData(4186, 100)]
        [InlineData(3811, 50)]
        [InlineData(3835, 55)]     //interpolated halfway to 60
        [InlineData(3500, 0)]
        [InlineData(3000, 0)]
        public void VoltageCurve(int millivolts, int expected)
        {
            Assert.Equal(expected, LogitechVoltageCurve.ToPercentage(millivolts));
        }

        private static byte[] Slice(byte[] source, int start, int length)
        {
            byte[] slice = new byte[length];
            System.Array.Copy(source, start, slice, 0, length);
            return slice;
        }
    }

    public class BluetoothTests
    {
        [Fact]
        public void PropertyBagValueAsPercentage()
        {
            Assert.Equal(57, BluetoothBatteryProvider.AsPercentage((byte)57));
            Assert.Equal(80, BluetoothBatteryProvider.AsPercentage("80"));
            Assert.Equal(0, BluetoothBatteryProvider.AsPercentage(0));
            Assert.Null(BluetoothBatteryProvider.AsPercentage(101));
            Assert.Null(BluetoothBatteryProvider.AsPercentage(-1));
            Assert.Null(BluetoothBatteryProvider.AsPercentage("abc"));
            Assert.Null(BluetoothBatteryProvider.AsPercentage(null));
        }
    }
}
