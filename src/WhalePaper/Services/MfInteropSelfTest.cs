using System;
using System.Runtime.InteropServices;
using WhalePaper.Interop;

namespace WhalePaper.Services
{
    // Uses real Windows MF objects rather than reading back our own GUID constants.
    internal static class MfInteropSelfTest
    {
        public static string Run()
        {
            MfInterop.IMFMediaType type = null;
            MfInterop.IMFSample sample = null;
            MfInterop.IMFMediaBuffer buffer = null;
            MfInterop.IMFMediaBuffer contiguous = null;
            var locked = false;
            try
            {
                Check(MfInterop.MFCreateMediaType(out type), "MFCreateMediaType");
                var majorKey = MfInterop.MF_MT_MAJOR_TYPE;
                var video = MfInterop.MFMediaType_Video;
                Check(type.SetGUID(ref majorKey, ref video), "SetGUID");
                Check(type.GetMajorType(out Guid actual), "GetMajorType");
                if (actual != video) { throw new InvalidOperationException("GetMajorType mismatch"); }

                Check(MfInterop.MFCreateSample(out sample), "MFCreateSample");
                Check(sample.SetSampleTime(1234567), "SetSampleTime");
                Check(sample.GetSampleTime(out long time), "GetSampleTime");
                if (time != 1234567) { throw new InvalidOperationException("sample time mismatch"); }
                Check(MfInterop.MFCreateMemoryBuffer(64, out buffer), "MFCreateMemoryBuffer");
                Check(buffer.SetCurrentLength(64), "SetCurrentLength");
                Check(sample.AddBuffer(buffer), "AddBuffer");
                Check(sample.GetBufferCount(out int count), "GetBufferCount");
                if (count != 1) { throw new InvalidOperationException("buffer count mismatch"); }
                Check(sample.ConvertToContiguousBuffer(out contiguous), "ConvertToContiguousBuffer");
                Check(contiguous.Lock(out IntPtr bits, out int capacity, out int length), "Lock");
                locked = true;
                if (bits == IntPtr.Zero || capacity < 64 || length != 64)
                {
                    throw new InvalidOperationException("buffer length mismatch");
                }
                Marshal.WriteByte(bits, 0x5A);
                if (Marshal.ReadByte(bits) != 0x5A)
                {
                    throw new InvalidOperationException("buffer round trip mismatch");
                }
                return "MF 原生接口（GetMajorType/SampleTime/AddBuffer/Convert/Lock）：[OK]";
            }
            catch (Exception ex)
            {
                return "MF 原生接口：[FAIL] " + ex.Message;
            }
            finally
            {
                if (locked) { contiguous.Unlock(); }
                MfSourceReader.ReleaseCom(contiguous);
                MfSourceReader.ReleaseCom(buffer);
                MfSourceReader.ReleaseCom(sample);
                MfSourceReader.ReleaseCom(type);
            }
        }

        private static void Check(int hr, string step)
        {
            if (MfInterop.Failed(hr))
            {
                throw new InvalidOperationException(step + " " + MfInterop.HResultText(hr));
            }
        }
    }
}
