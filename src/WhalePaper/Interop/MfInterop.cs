using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WhalePaper.Interop
{
    /// <summary>
    /// Minimal, hand-written Media Foundation interop (no NuGet dependency).
    /// Only the pieces needed by the wallpaper renderer are implemented:
    /// MFStartup/MFShutdown, the Source Reader, and RGB32 video frames.
    /// </summary>
    internal static class MfInterop
    {
        public const int MF_VERSION = 0x00020070;
        public const int MFSTARTUP_FULL = 0;
        public const int MFSTARTUP_LITE = 1;

        public const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = unchecked((int)0xFFFFFFFC);
        public const int MF_SOURCE_READER_ALL_STREAMS = unchecked((int)0xFFFFFFFE);

        public const int MF_SOURCE_READERF_ERROR = 0x00000001;
        public const int MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
        public const int MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000020;
        public const int MF_SOURCE_READERF_STREAMTICK = 0x00000100;

        public const int S_OK = 0;
        public const int S_FALSE = 1;

        public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6);
        public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
        public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
        public const int MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212);
        public const int E_FAIL = unchecked((int)0x80004005);

        public const ushort VT_EMPTY = 0;
        public const ushort VT_I8 = 20;

        public const int MFVideoInterlace_Progressive = 2;

        // ---- Attributes / media types ----

        public static readonly Guid MF_MT_MAJOR_TYPE =
            new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");

        public static readonly Guid MF_MT_SUBTYPE =
            new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");

        public static readonly Guid MF_MT_FRAME_SIZE =
            new Guid("1652c33d-d6b2-4012-b834-72030849a37d");

        public static readonly Guid MF_MT_FRAME_RATE =
            new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");

        public static readonly Guid MF_MT_DEFAULT_STRIDE =
            new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

        public static readonly Guid MF_MT_INTERLACE_MODE =
            new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

        public static readonly Guid MFMediaType_Video =
            new Guid("73646976-0000-0010-8000-00aa00389b71");

        public static readonly Guid MFVideoFormat_RGB32 =
            new Guid("00000016-0000-0010-8000-00aa00389b71");

        public static readonly Guid MFVideoFormat_RGB24 =
            new Guid("00000014-0000-0010-8000-00aa00389b71");

        public static readonly Guid MFVideoFormat_NV12 =
            new Guid("3231564e-0000-0010-8000-00aa00389b71");

        public static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING =
            new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");

        public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING =
            new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

        public static bool Succeeded(int hr)
        {
            return hr >= 0;
        }

        public static bool Failed(int hr)
        {
            return hr < 0;
        }

        public static string HResultText(int hr)
        {
            return "0x" + ((uint)hr).ToString("X8");
        }

        public static string HResultText(int hr, string context)
        {
            return context + " " + HResultText(hr);
        }

        // ---- P/Invoke ----

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int dwFlags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, int cInitialSize);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample ppIMFSample);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFGetStrideForBitmapInfoHeader(int format, int width, out int stride);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int MFCreateSourceReaderFromURL(
            string pwszURL,
            IMFAttributes pAttributes,
            out IMFSourceReader ppSourceReader);

        // ---- COM interfaces ----

        [ComImport]
        [Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFAttributes
        {
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem(
                ref Guid guidKey,
                IntPtr value,
                [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int Compare(
                IMFAttributes pTheirs,
                int matchType,
                [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out int punValue);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out long punValue);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out int pcchLength);
            [PreserveSig] int GetString(
                ref Guid guidKey,
                [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue,
                int cchBufSize,
                out int pcchLength);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbSize);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] int DeleteItem(ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, int unValue);
            [PreserveSig] int SetUINT64(ref Guid guidKey, long unValue);
            [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
            [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize);
            [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out int pcItems);
            [PreserveSig] int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        }

        [ComImport]
        [Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaType : IMFAttributes
        {
            // ComImport does not inherit native vtable slots. Keep all 30 IMFAttributes
            // methods here, in SDK order, before this interface's own methods.
            [PreserveSig] new int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] new int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] new int CompareItem(ref Guid guidKey, IntPtr value,
                [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] new int Compare(IMFAttributes pTheirs, int matchType,
                [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] new int GetUINT32(ref Guid guidKey, out int punValue);
            [PreserveSig] new int GetUINT64(ref Guid guidKey, out long punValue);
            [PreserveSig] new int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] new int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] new int GetStringLength(ref Guid guidKey, out int pcchLength);
            [PreserveSig] new int GetString(ref Guid guidKey,
                [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue,
                int cchBufSize, out int pcchLength);
            [PreserveSig] new int GetAllocatedString(ref Guid guidKey,
                out IntPtr ppwszValue, out int pcchLength);
            [PreserveSig] new int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
            [PreserveSig] new int GetBlob(ref Guid guidKey, IntPtr pBuf,
                int cbBufSize, out int pcbBlobSize);
            [PreserveSig] new int GetAllocatedBlob(ref Guid guidKey,
                out IntPtr ppBuf, out int pcbSize);
            [PreserveSig] new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] new int SetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] new int DeleteItem(ref Guid guidKey);
            [PreserveSig] new int DeleteAllItems();
            [PreserveSig] new int SetUINT32(ref Guid guidKey, int unValue);
            [PreserveSig] new int SetUINT64(ref Guid guidKey, long unValue);
            [PreserveSig] new int SetDouble(ref Guid guidKey, double fValue);
            [PreserveSig] new int SetGUID(ref Guid guidKey, ref Guid guidValue);
            [PreserveSig] new int SetString(ref Guid guidKey,
                [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] new int SetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize);
            [PreserveSig] new int SetUnknown(ref Guid guidKey,
                [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] new int LockStore();
            [PreserveSig] new int UnlockStore();
            [PreserveSig] new int GetCount(out int pcItems);
            [PreserveSig] new int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] new int CopyAllItems(IMFAttributes pDest);

            [PreserveSig] int GetMajorType(out Guid pguidMajorType);
            [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
            [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
            [PreserveSig] int GetRepresentation([In] Guid guidRepresentation, out IntPtr ppvRepresentation);
            [PreserveSig] int FreeRepresentation([In] Guid guidRepresentation, [In] IntPtr pvRepresentation);
        }

        [ComImport]
        [Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
            [PreserveSig] int SetCurrentLength(int cbCurrentLength);
            [PreserveSig] int GetMaxLength(out int pcbMaxLength);
        }

        [ComImport]
        [Guid("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMF2DBuffer
        {
            [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
            [PreserveSig] int Unlock2D();
            [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
            [PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool contiguous);
            [PreserveSig] int GetContiguousLength(out int length);
            [PreserveSig] int ContiguousCopyTo(IntPtr destination, int length);
            [PreserveSig] int ContiguousCopyFrom(IntPtr source, int length);
        }

        [ComImport]
        [Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSample : IMFAttributes
        {
            // Omitting these slots makes ConvertToContiguousBuffer call the native
            // GetStringLength method instead, returning MF_E_ATTRIBUTENOTFOUND.
            [PreserveSig] new int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] new int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] new int CompareItem(ref Guid guidKey, IntPtr value,
                [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] new int Compare(IMFAttributes pTheirs, int matchType,
                [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] new int GetUINT32(ref Guid guidKey, out int punValue);
            [PreserveSig] new int GetUINT64(ref Guid guidKey, out long punValue);
            [PreserveSig] new int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] new int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] new int GetStringLength(ref Guid guidKey, out int pcchLength);
            [PreserveSig] new int GetString(ref Guid guidKey,
                [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue,
                int cchBufSize, out int pcchLength);
            [PreserveSig] new int GetAllocatedString(ref Guid guidKey,
                out IntPtr ppwszValue, out int pcchLength);
            [PreserveSig] new int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
            [PreserveSig] new int GetBlob(ref Guid guidKey, IntPtr pBuf,
                int cbBufSize, out int pcbBlobSize);
            [PreserveSig] new int GetAllocatedBlob(ref Guid guidKey,
                out IntPtr ppBuf, out int pcbSize);
            [PreserveSig] new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] new int SetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] new int DeleteItem(ref Guid guidKey);
            [PreserveSig] new int DeleteAllItems();
            [PreserveSig] new int SetUINT32(ref Guid guidKey, int unValue);
            [PreserveSig] new int SetUINT64(ref Guid guidKey, long unValue);
            [PreserveSig] new int SetDouble(ref Guid guidKey, double fValue);
            [PreserveSig] new int SetGUID(ref Guid guidKey, ref Guid guidValue);
            [PreserveSig] new int SetString(ref Guid guidKey,
                [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] new int SetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize);
            [PreserveSig] new int SetUnknown(ref Guid guidKey,
                [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] new int LockStore();
            [PreserveSig] new int UnlockStore();
            [PreserveSig] new int GetCount(out int pcItems);
            [PreserveSig] new int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] new int CopyAllItems(IMFAttributes pDest);

            [PreserveSig] int GetSampleFlags(out int pdwSampleFlags);
            [PreserveSig] int SetSampleFlags(int dwSampleFlags);
            [PreserveSig] int GetSampleTime(out long phnsSampleTime);
            [PreserveSig] int SetSampleTime(long hnsSampleTime);
            [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
            [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
            [PreserveSig] int GetBufferCount(out int pdwBufferCount);
            [PreserveSig] int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
            [PreserveSig] int RemoveBufferByIndex(int dwIndex);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out int pcbTotalLength);
            [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
        }

        [ComImport]
        [Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(
                int dwStreamIndex,
                [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
            [PreserveSig] int SetStreamSelection(
                int dwStreamIndex,
                [MarshalAs(UnmanagedType.Bool)] bool fSelected);
            [PreserveSig] int GetNativeMediaType(
                int dwStreamIndex,
                int dwMediaTypeIndex,
                out IMFMediaType ppMediaType);
            [PreserveSig] int GetCurrentMediaType(int dwStreamIndex, out IMFMediaType ppMediaType);
            [PreserveSig] int SetCurrentMediaType(
                int dwStreamIndex,
                IntPtr pdwReserved,
                IMFMediaType pMediaType);
            [PreserveSig] int SetCurrentPosition(ref Guid guidTimeFormat, ref PROPVARIANT pvarPosition);
            [PreserveSig] int ReadSample(
                int dwStreamIndex,
                int dwControlFlags,
                out int pdwActualStreamIndex,
                out int pdwStreamFlags,
                out long pllTimestamp,
                out IMFSample ppSample);
            [PreserveSig] int Flush(int dwStreamIndex);
            [PreserveSig] int GetServiceForStream(
                int dwStreamIndex,
                ref Guid guidService,
                ref Guid riid,
                out IntPtr ppvObject);
            [PreserveSig] int GetPresentationAttribute(
                int dwStreamIndex,
                ref Guid guidAttribute,
                out PROPVARIANT pvarAttribute);
        }

        /// <summary>
        /// PROPVARIANT is 24 bytes on x64. The app is x64-only (PlatformTarget x64).
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(2)] public ushort wReserved1;
            [FieldOffset(4)] public ushort wReserved2;
            [FieldOffset(6)] public ushort wReserved3;
            [FieldOffset(8)] public long hVal;
            [FieldOffset(8)] public ulong uhVal;
            [FieldOffset(8)] public double dblVal;
            [FieldOffset(8)] public IntPtr pointerValue;
        }
    }
}
