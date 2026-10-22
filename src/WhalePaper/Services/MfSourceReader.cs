using System;
using System.IO;
using System.Runtime.InteropServices;
using WhalePaper.Interop;

namespace WhalePaper.Services
{
    internal enum MfReadStatus
    {
        Frame = 0,
        EndOfStream = 1,
        StreamTick = 2,
        Error = 3
    }

    /// <summary>
    /// One locked frame (RGB32 or RGB24). The pixel pointer is only valid until Dispose().
    /// </summary>
    internal sealed class FrameSample : IDisposable
    {
        private MfInterop.IMFMediaBuffer _buffer;
        private MfInterop.IMFSample _sample;
        private MfInterop.IMF2DBuffer _surface;

        internal FrameSample(
            IntPtr bits,
            int width,
            int height,
            int stride,
            bool bottomUp,
            int bitCount,
            long timestamp,
            MfInterop.IMFMediaBuffer buffer,
            MfInterop.IMFSample sample,
            MfInterop.IMF2DBuffer surface = null)
        {
            Bits = bits;
            Width = width;
            Height = height;
            Stride = stride;
            BottomUp = bottomUp;
            BitCount = bitCount;
            Timestamp = timestamp;
            _buffer = buffer;
            _sample = sample;
            _surface = surface;
        }

        public IntPtr Bits { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride { get; private set; }
        public bool BottomUp { get; private set; }
        public int BitCount { get; private set; }
        public long Timestamp { get; private set; }

        public int BytesPerPixel
        {
            get { return BitCount / 8; }
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            if (buffer != null)
            {
                try
                {
                    if (_surface != null) { _surface.Unlock2D(); }
                    else { buffer.Unlock(); }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("MF buffer unlock failed: " + ex.Message);
                }
                MfSourceReader.ReleaseCom(buffer);
            }
            // Both interfaces refer to the same RCW; release it only once.
            _surface = null;

            var sample = _sample;
            _sample = null;
            MfSourceReader.ReleaseCom(sample);
            Bits = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Media Foundation Source Reader wrapper. Advanced video processing (the video
    /// processor MFT) converts YUV to RGB32; if the decoder refuses RGB32 we also try
    /// RGB24. 1.0.6 fixes the MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING GUID:
    /// 1.0.5 used a wrong GUID, so the video processor was never inserted and
    /// SetCurrentMediaType(RGB32) failed with MF_E_INVALIDMEDIATYPE (0xC00D36B4).
    /// </summary>
    internal sealed class MfSourceReader : IDisposable
    {
        private readonly string _path;
        private MfInterop.IMFSourceReader _reader;
        private MfInterop.IMFMediaType _currentType;
        private bool _mfStarted;
        private bool _disposed;

        private ulong _nativeFrameSize;
        private ulong _nativeFrameRate;
        private int _nativeInterlace;
        private long _readCount;
        private int _contiguousStride;
        private bool _contiguousBottomUp;

        private MfSourceReader(string path)
        {
            _path = path;
        }

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride { get; private set; }
        public bool BottomUp { get; private set; }
        public int BitCount { get; private set; }
        public double Fps { get; private set; }
        public double SourceFps { get; private set; }
        public string SubtypeName { get; private set; }
        public int LastError { get; private set; }
        public string LastErrorStage { get; private set; }

        public int BytesPerPixel
        {
            get { return BitCount / 8; }
        }

        public bool RowsArePacked
        {
            get { return Stride == Width * BytesPerPixel; }
        }

        public string Description
        {
            get
            {
                return SubtypeName + " " + Width + "x" + Height +
                       " bpp=" + BitCount +
                       " stride=" + Stride +
                       (BottomUp ? " bottom-up" : " top-down") +
                       " fps=" + Fps.ToString("0.###");
            }
        }

        public static MfSourceReader Open(string path, out int errorHr, out string error)
        {
            errorHr = 0;
            error = null;
            var session = new MfSourceReader(path);
            if (session.OpenCore(out errorHr, out error))
            {
                return session;
            }

            session.Dispose();
            return null;
        }

        private bool OpenCore(out int errorHr, out string error)
        {
            errorHr = 0;
            error = null;

            var hr = MfInterop.MFStartup(MfInterop.MF_VERSION, MfInterop.MFSTARTUP_FULL);
            if (MfInterop.Failed(hr))
            {
                errorHr = hr;
                error = "MFStartup 失败 " + MfInterop.HResultText(hr);
                return false;
            }
            _mfStarted = true;

            MfInterop.IMFAttributes attributes = null;
            try
            {
                hr = MfInterop.MFCreateAttributes(out attributes, 2);
                if (MfInterop.Failed(hr))
                {
                    errorHr = hr;
                    error = "MFCreateAttributes 失败 " + MfInterop.HResultText(hr);
                    return false;
                }

                var advancedProcessingKey =
                    MfInterop.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING;
                hr = attributes.SetUINT32(ref advancedProcessingKey, 1);
                if (MfInterop.Failed(hr))
                {
                    AppLog.Warn("MF advanced video processing attribute failed: " +
                                MfInterop.HResultText(hr));
                }

                hr = MfInterop.MFCreateSourceReaderFromURL(_path, attributes, out _reader);
                if (MfInterop.Failed(hr) || _reader == null)
                {
                    errorHr = hr;
                    error = "MF_OPEN_FAILED 打开视频失败 " + MfInterop.HResultText(hr);
                    return false;
                }
            }
            finally
            {
                ReleaseCom(attributes);
            }

            hr = _reader.SetStreamSelection(MfInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            if (MfInterop.Failed(hr))
            {
                AppLog.Warn("MF disable all streams failed: " + MfInterop.HResultText(hr));
            }

            hr = _reader.SetStreamSelection(MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);
            if (MfInterop.Failed(hr))
            {
                errorHr = hr;
                error = "选择视频流失败 " + MfInterop.HResultText(hr);
                return false;
            }

            ReadNativeInfo();

            var configured = TrySetOutputType(false, true, out int lastHr)
                || TrySetOutputType(false, false, out lastHr)
                || TrySetOutputType(true, true, out lastHr)
                || TrySetOutputType(true, false, out lastHr);

            if (!configured)
            {
                errorHr = lastHr;
                error = "MF_NO_RGB32 无法协商 RGB32/RGB24 输出 " + MfInterop.HResultText(lastHr);
                return false;
            }

            if (!ReadOutputFormat(out errorHr, out error))
            {
                return false;
            }

            AppLog.Info("MF opened: " + _path);
            return true;
        }

        private bool ReadOutputFormat(out int errorHr, out string error)
        {
            errorHr = 0;
            error = null;
            ReleaseCom(_currentType);
            _currentType = null;
            var hr = _reader.GetCurrentMediaType(
                MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out _currentType);
            if (MfInterop.Failed(hr) || _currentType == null)
            {
                errorHr = hr;
                error = "读取输出格式失败 " + MfInterop.HResultText(hr);
                return false;
            }

            BitCount = 32;
            SubtypeName = "RGB32";
            var subtypeKey = MfInterop.MF_MT_SUBTYPE;
            if (MfInterop.Succeeded(_currentType.GetGUID(ref subtypeKey, out Guid subtype)))
            {
                if (subtype == MfInterop.MFVideoFormat_RGB24)
                {
                    BitCount = 24;
                    SubtypeName = "RGB24";
                }
                else if (subtype == MfInterop.MFVideoFormat_RGB32)
                {
                    BitCount = 32;
                    SubtypeName = "RGB32";
                }
                else
                {
                    errorHr = MfInterop.MF_E_INVALIDMEDIATYPE;
                    error = "MF_UNSUPPORTED_OUTPUT 不支持的输出格式 " + subtype;
                    return false;
                }
            }

            Fps = ReadFrameRate(_currentType, SourceFps);

            var frameSizeKey = MfInterop.MF_MT_FRAME_SIZE;
            hr = _currentType.GetUINT64(ref frameSizeKey, out long packedSize);
            var frameSize = MfInterop.Succeeded(hr) ? (ulong)packedSize : _nativeFrameSize;

            if (frameSize == 0)
            {
                errorHr = MfInterop.MF_E_ATTRIBUTENOTFOUND;
                error = "MF_NO_FRAME_SIZE 输出格式没有帧尺寸";
                return false;
            }

            Width = (int)(frameSize >> 32);
            Height = (int)(frameSize & 0xFFFFFFFF);
            if (Width <= 0 || Height <= 0)
            {
                errorHr = MfInterop.MF_E_INVALIDMEDIATYPE;
                error = "MF_INVALID_FRAME_SIZE 无效的帧尺寸 " + Width + "x" + Height;
                return false;
            }

            var bytesPerPixel = BitCount / 8;
            var strideKey = MfInterop.MF_MT_DEFAULT_STRIDE;
            var strideResult = _currentType.GetUINT32(ref strideKey, out int signedStride);
            if (MfInterop.Succeeded(strideResult))
            {
                if (signedStride == int.MinValue)
                {
                    errorHr = MfInterop.MF_E_INVALIDMEDIATYPE;
                    error = "MF_INVALID_STRIDE 无效的行跨度";
                    return false;
                }
                Stride = Math.Abs(signedStride);
                BottomUp = signedStride < 0;
            }
            else
            {
                // Ask MF for the subtype's contiguous layout; RGB32 output from the
                // video processor must not be guessed to be a bottom-up DIB.
                hr = MfInterop.MFGetStrideForBitmapInfoHeader(BitCount == 24 ? 20 : 22,
                    Width, out signedStride);
                if (MfInterop.Failed(hr) || signedStride == 0 || signedStride == int.MinValue)
                {
                    errorHr = MfInterop.Failed(hr) ? hr : MfInterop.MF_E_INVALIDMEDIATYPE;
                    error = "MF_INVALID_STRIDE 无法取得像素行跨度";
                    return false;
                }
                Stride = Math.Abs(signedStride);
                BottomUp = signedStride < 0;
                AppLog.Info("MF default stride calculated: " + signedStride);
            }

            if (Stride < Width * bytesPerPixel)
            {
                Stride = Width * bytesPerPixel;
            }
            _contiguousStride = Stride;
            _contiguousBottomUp = BottomUp;

            AppLog.Info("MF output: " + Description +
                        " sourceFps=" + SourceFps.ToString("0.###") +
                        " nativeSize=0x" + _nativeFrameSize.ToString("X") +
                        " nativeRate=0x" + _nativeFrameRate.ToString("X") +
                        " interlace=" + _nativeInterlace);
            return true;
        }

        private void ReadNativeInfo()
        {
            MfInterop.IMFMediaType native = null;
            try
            {
                var hr = _reader.GetNativeMediaType(
                    MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out native);
                if (MfInterop.Failed(hr) || native == null)
                {
                    SourceFps = 30;
                    return;
                }

                SourceFps = ReadFrameRate(native, 0);

                var frameSizeKey = MfInterop.MF_MT_FRAME_SIZE;
                if (MfInterop.Succeeded(native.GetUINT64(ref frameSizeKey, out long packedSize)))
                {
                    _nativeFrameSize = (ulong)packedSize;
                }

                var frameRateKey = MfInterop.MF_MT_FRAME_RATE;
                if (MfInterop.Succeeded(native.GetUINT64(ref frameRateKey, out long packedRate)))
                {
                    _nativeFrameRate = (ulong)packedRate;
                }

                var interlaceKey = MfInterop.MF_MT_INTERLACE_MODE;
                if (MfInterop.Succeeded(native.GetUINT32(ref interlaceKey, out int interlace)))
                {
                    _nativeInterlace = interlace;
                }
            }
            finally
            {
                ReleaseCom(native);
            }
        }

        /// <summary>
        /// Builds an output type. Some decoders/video processors require the frame size
        /// and rate to be present; if the full type is rejected we retry minimal, then
        /// RGB24. This keeps a useful fallback if RGB32 is not available.
        /// </summary>
        private bool TrySetOutputType(bool rgb24, bool includeFrameInfo, out int hr)
        {
            hr = 0;
            MfInterop.IMFMediaType outputType = null;
            try
            {
                hr = MfInterop.MFCreateMediaType(out outputType);
                if (MfInterop.Failed(hr) || outputType == null)
                {
                    return false;
                }

                var majorTypeKey = MfInterop.MF_MT_MAJOR_TYPE;
                var videoTypeValue = MfInterop.MFMediaType_Video;
                hr = outputType.SetGUID(ref majorTypeKey, ref videoTypeValue);
                if (MfInterop.Failed(hr))
                {
                    return false;
                }

                var subtypeKey = MfInterop.MF_MT_SUBTYPE;
                var subtypeValue = rgb24
                    ? MfInterop.MFVideoFormat_RGB24
                    : MfInterop.MFVideoFormat_RGB32;
                hr = outputType.SetGUID(ref subtypeKey, ref subtypeValue);
                if (MfInterop.Failed(hr))
                {
                    return false;
                }

                if (includeFrameInfo && _nativeFrameSize != 0)
                {
                    var frameSizeKey = MfInterop.MF_MT_FRAME_SIZE;
                    hr = outputType.SetUINT64(ref frameSizeKey, (long)_nativeFrameSize);
                    if (MfInterop.Failed(hr))
                    {
                        return false;
                    }

                    if (_nativeFrameRate != 0)
                    {
                        var frameRateKey = MfInterop.MF_MT_FRAME_RATE;
                        hr = outputType.SetUINT64(ref frameRateKey, (long)_nativeFrameRate);
                        if (MfInterop.Failed(hr))
                        {
                            return false;
                        }
                    }

                    var interlaceKey = MfInterop.MF_MT_INTERLACE_MODE;
                    hr = outputType.SetUINT32(
                        ref interlaceKey, MfInterop.MFVideoInterlace_Progressive);
                    if (MfInterop.Failed(hr))
                    {
                        return false;
                    }
                }

                hr = _reader.SetCurrentMediaType(
                    MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, outputType);
                return MfInterop.Succeeded(hr);
            }
            finally
            {
                ReleaseCom(outputType);
            }
        }

        private static double ReadFrameRate(MfInterop.IMFMediaType type, double fallback)
        {
            var frameRateKey = MfInterop.MF_MT_FRAME_RATE;
            var hr = type.GetUINT64(ref frameRateKey, out long packed);
            if (MfInterop.Failed(hr))
            {
                return fallback > 0 ? fallback : 30;
            }

            var numerator = (uint)(packed >> 32);
            var denominator = (uint)(packed & 0xFFFFFFFF);
            if (numerator == 0 || denominator == 0)
            {
                return fallback > 0 ? fallback : 30;
            }

            var fps = numerator / (double)denominator;
            if (fps < 1 || fps > 240)
            {
                return fallback > 0 ? fallback : 30;
            }
            return fps;
        }

        public MfReadStatus ReadSample(out FrameSample frame)
        {
            frame = null;
            if (_reader == null || _disposed)
            {
                return MfReadStatus.Error;
            }

            var hr = _reader.ReadSample(
                MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                0,
                out int streamIndex,
                out int flags,
                out long timestamp,
                out MfInterop.IMFSample sample);

            _readCount++;
            if (_readCount <= 3 || MfInterop.Failed(hr) || flags != 0)
            {
                AppLog.Info("MF ReadSample: read=" + _readCount +
                            " hr=" + MfInterop.HResultText(hr) + " stream=" + streamIndex +
                            " flags=0x" + flags.ToString("X") + " timestamp=" + timestamp +
                            " sample=" + (sample == null ? "null" : "present"));
            }

            if (MfInterop.Failed(hr))
            {
                LastError = hr;
                LastErrorStage = "ReadSample";
                ReleaseCom(sample);
                return MfReadStatus.Error;
            }

            if ((flags & MfInterop.MF_SOURCE_READERF_ERROR) != 0)
            {
                LastError = MfInterop.Failed(hr) ? hr : MfInterop.E_FAIL;
                LastErrorStage = "ReadSample flags=ERROR";
                ReleaseCom(sample);
                return MfReadStatus.Error;
            }

            if ((flags & MfInterop.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
            {
                if (!ReadOutputFormat(out int formatHr, out string formatError))
                {
                    LastError = formatHr;
                    LastErrorStage = "GetCurrentMediaType";
                    AppLog.Warn("MF output format change failed: " + formatError);
                    ReleaseCom(sample);
                    return MfReadStatus.Error;
                }
            }

            if (sample == null)
            {
                return (flags & MfInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0
                    ? MfReadStatus.EndOfStream
                    : MfReadStatus.StreamTick;
            }

            if ((flags & MfInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                ReleaseCom(sample);
                return MfReadStatus.EndOfStream;
            }

            hr = sample.ConvertToContiguousBuffer(out MfInterop.IMFMediaBuffer buffer);
            if (_readCount <= 3 || MfInterop.Failed(hr))
            {
                AppLog.Info("MF ConvertToContiguousBuffer: hr=" + MfInterop.HResultText(hr) +
                            " buffer=" + (buffer == null ? "null" : "present"));
            }
            if (MfInterop.Failed(hr) || buffer == null)
            {
                LastError = MfInterop.Failed(hr) ? hr : MfInterop.E_FAIL;
                LastErrorStage = "ConvertToContiguousBuffer";
                ReleaseCom(buffer);
                ReleaseCom(sample);
                return MfReadStatus.Error;
            }

            // Prefer the actual surface pitch. Lock2D returns the visual top row;
            // normalize a negative pitch to our lowest-address + BottomUp contract.
            var surface = buffer as MfInterop.IMF2DBuffer;
            if (surface != null)
            {
                hr = surface.Lock2D(out IntPtr scanline0, out int pitch);
                if (MfInterop.Succeeded(hr))
                {
                    if (scanline0 == IntPtr.Zero || pitch == int.MinValue ||
                        Math.Abs((long)pitch) < (long)Width * BytesPerPixel)
                    {
                        surface.Unlock2D();
                        LastError = MfInterop.MF_E_INVALIDMEDIATYPE;
                        LastErrorStage = "Lock2D pitch";
                        ReleaseCom(buffer);
                        ReleaseCom(sample);
                        return MfReadStatus.Error;
                    }
                    Stride = Math.Abs(pitch);
                    BottomUp = pitch < 0;
                    var lowestAddress = BottomUp
                        ? new IntPtr(scanline0.ToInt64() + (long)(Height - 1) * pitch)
                        : scanline0;
                    if (_readCount <= 3)
                    {
                        AppLog.Info("MF Lock2D: hr=" + MfInterop.HResultText(hr) + " pitch=" + pitch);
                    }
                    frame = new FrameSample(lowestAddress, Width, Height, Stride,
                        BottomUp, BitCount, timestamp, buffer, sample, surface);
                    return MfReadStatus.Frame;
                }
                if (_readCount <= 3)
                {
                    AppLog.Warn("MF Lock2D unavailable; trying contiguous Lock: " +
                                MfInterop.HResultText(hr));
                }
            }

            Stride = _contiguousStride;
            BottomUp = _contiguousBottomUp;
            hr = buffer.Lock(out IntPtr bits, out int maxLength, out int currentLength);
            if (_readCount <= 3 || MfInterop.Failed(hr))
            {
                AppLog.Info("MF Lock: hr=" + MfInterop.HResultText(hr) +
                            " maxLength=" + maxLength + " currentLength=" + currentLength);
            }
            if (MfInterop.Failed(hr) || bits == IntPtr.Zero)
            {
                LastError = MfInterop.Failed(hr) ? hr : MfInterop.E_FAIL;
                LastErrorStage = "Lock";
                if (MfInterop.Succeeded(hr)) { buffer.Unlock(); }
                ReleaseCom(buffer);
                ReleaseCom(sample);
                return MfReadStatus.Error;
            }

            var requiredLength = (long)Stride * (Height - 1) + (long)Width * BytesPerPixel;
            if (requiredLength > currentLength || currentLength > maxLength)
            {
                LastError = MfInterop.MF_E_INVALIDMEDIATYPE;
                LastErrorStage = "BufferLength";
                AppLog.Warn("MF frame buffer too short: required=" + requiredLength +
                            " current=" + currentLength + " max=" + maxLength);
                buffer.Unlock();
                ReleaseCom(buffer);
                ReleaseCom(sample);
                return MfReadStatus.Error;
            }

            frame = new FrameSample(
                bits, Width, Height, Stride, BottomUp, BitCount, timestamp, buffer, sample);
            return MfReadStatus.Frame;
        }

        public bool SeekToStart()
        {
            if (_reader == null || _disposed)
            {
                return false;
            }

            var timeFormat = Guid.Empty;
            var position = new MfInterop.PROPVARIANT
            {
                vt = MfInterop.VT_I8,
                hVal = 0
            };

            var hr = _reader.SetCurrentPosition(ref timeFormat, ref position);
            if (MfInterop.Failed(hr))
            {
                LastError = hr;
                AppLog.Warn("MF seek to start failed: " + MfInterop.HResultText(hr));
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            ReleaseCom(_currentType);
            _currentType = null;
            ReleaseCom(_reader);
            _reader = null;

            if (_mfStarted)
            {
                _mfStarted = false;
                try
                {
                    MfInterop.MFShutdown();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("MFShutdown failed: " + ex.Message);
                }
            }
        }

        internal static void ReleaseCom(object comObject)
        {
            if (comObject == null)
            {
                return;
            }

            try
            {
                if (Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Release COM object failed: " + ex.Message);
            }
        }
    }
}
