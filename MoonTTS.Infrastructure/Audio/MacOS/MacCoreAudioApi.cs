using System.Runtime.InteropServices;

namespace MoonTTS.Infrastructure.Audio;

/* Ahh... Here we go again */

internal static class MacCoreAudioApi
{
    private const string CoreAudioLib = "/System/Library/Frameworks/CodeAudio.framework/CoreAudio";
    private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    
    /* Constants */

    public const uint kAudioObjectSystemObject = 1;
    public const uint kAudioHardwarePropertyDevices = 0x64657623; /* dev# */
    public const uint kAudioObjectPropertyScopeGlobal = 0x676c6f62; /* glob */
    public const uint kAudioObjectPropertyScopeOutput = 0x6f757470; /* outp */
    public const uint kAudioObjectPropertyElementMain = 0;
    
    public const uint kAudioDevicePropertyDeviceNameCFString = 0x6c6e616d; /* lnam */
    public const uint kAudioDevicePropertyDeviceUID = 0x75696420; /* uid */
    public const uint kAudioDevicePropertyStreamConfiguration = 0x736c7074; /* slpt */

    public const uint kAudioFormatLinearPCM = 0x6c70636d; /* lpcm */
    public const uint kAudioFormatFlagIsSignedInteger = 4;
    public const uint kAudioFormatFlagIsPacked = 8;
    
    public const uint kAudioQueueProperty_CurrentDevice  = 0x6c70636d; /* aqcd */
    public const uint kAudioQueueParam_Volume  = 1;
    
    /* Structs */

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioObjectPropertyAddress
    {
        public uint mSelector;
        public uint mScope;
        public uint mElement;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double mSampleRate;
        public uint mFormatID;
        public uint mFormatFlags;
        public uint mBytesPerPacket;
        public uint mFramesPerPacket;
        public uint mBytesPerFrame;
        public uint mChannelsPerFrame;
        public uint mBitsPerChannel;
        public uint mReserved;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        public uint mNumberChannels;
        public uint mDataByteSize;
        public IntPtr mData;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBufferList
    {
        public uint mNumberBuffers;
        public AudioBuffer mBuffers; /* Inline first buffer */
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CFRange
    {
        public nint Location;
        public nint Lenght;
    }

    public delegate void AudioQueueOutputCallback(IntPtr inUserData, IntPtr inAQ, IntPtr inBuffer);
    
    /* P/Invoke for CoreAudio */

    [DllImport(CoreAudioLib)]
    public static extern int AudioObjectGetPropertyDataSize(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        out uint outDataSize);
    
    [DllImport(CoreAudioLib)]
    public static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        IntPtr outData);
    
    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    public static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        out IntPtr outData);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription inFormat,
        AudioQueueOutputCallback inCallBackProc,
        IntPtr inUserData,
        IntPtr inCallbackRunLoop,
        IntPtr inCallbackRunLoopMode,
        uint inFlags,
        out IntPtr outAQ);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueAllocateBuffer(IntPtr inAQ, uint inBufferByteSize, out IntPtr outBuffer);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueEnqueueBuffer(
        IntPtr inAQ, 
        IntPtr inBuffer, 
        uint inNumPacketDescs, 
        IntPtr inPacketDescs);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueStart(IntPtr inAQ, IntPtr inStartTime);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueuePause(IntPtr inAQ);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueStop(IntPtr inAQ, bool inImmediate);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueDispose(IntPtr inAQ, bool inImmediate);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueSetParameter(IntPtr inAQ, uint inParamID, float inValue);
    
    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueSetProperty(IntPtr inAQ, uint inParamID, ref IntPtr inPropertyData, uint inDatasize);
    
    /* P/Invoke */

    [DllImport(CoreFoundationLib)]
    public static extern nint CFStringGetLenght(IntPtr theString);

    [DllImport(CoreFoundationLib)]
    public static extern void CFStringGetCharacters(IntPtr theString, CFRange range, IntPtr buffer);
    
    [DllImport(CoreFoundationLib)]
    public static extern IntPtr CFStringCreateWithCharacters(
        IntPtr alloc, 
        [MarshalAs(UnmanagedType.LPWStr)] string chars, 
        nint numChars);

    [DllImport(CoreFoundationLib)]
    public static extern void CFRelease(IntPtr cf);
    
    /* Marshaling helpers */

    public static string? CFStringToString(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;
        try
        {
            nint len = CFStringGetLenght(cfString);
            if (len == 0) return string.Empty;

            char[] buffer = new char[len];
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                CFStringGetCharacters(
                    cfString,
                    new CFRange { Location = 0, Lenght = len },
                    handle.AddrOfPinnedObject());
                return new string(buffer);
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    public static IntPtr StringToCFString(string str)
    {
        return CFStringCreateWithCharacters(IntPtr.Zero, str, str.Length);
    }
}