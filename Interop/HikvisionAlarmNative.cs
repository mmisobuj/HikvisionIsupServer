using System.Runtime.InteropServices;
using static QuickNV.HikvisionISUPSDK.Defines;

namespace HikvisionIsupServer.Interop;

/// <summary>
/// P/Invoke surface for the Hikvision HCISUPAlarm module (the ISUP alarm/event channel).
/// </summary>
/// <remarks>
/// The QuickNV.HikvisionISUPSDK package wraps HCISUPCMS and HCISUPStream only, but it does
/// ship the HCISUPAlarm native binaries under <c>runtimes/{rid}/native</c>, so they resolve
/// through the normal native probing paths. Struct layouts follow HCISUPAlarm.h from the
/// vendor ISUP SDK; keep them byte-for-byte identical or the SDK will read garbage.
/// </remarks>
public static class HikvisionAlarmNative
{
    private const string WindowsDll = "HCISUPAlarm.dll";
    private const string LinuxSo = "libHCISUPAlarm.so";

    public static bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // ---- Alarm types (NET_EHOME_ALARM_MSG.dwAlarmType) ----------------------------------

    public const int EHOME_ALARM_UNKNOWN = 0;
    public const int EHOME_ALARM = 1;
    public const int EHOME_ALARM_HEATMAP_REPORT = 2;
    public const int EHOME_ALARM_FACESNAP_REPORT = 3;
    public const int EHOME_ALARM_GPS = 4;
    public const int EHOME_ALARM_CID_REPORT = 5;
    public const int EHOME_ALARM_NOTICE_PICURL = 6;
    public const int EHOME_ALARM_NOTIFY_FAIL = 7;
    public const int EHOME_ALARM_SELFDEFINE = 9;
    public const int EHOME_ALARM_DEVICE_NETSWITCH_REPORT = 10;

    /// <summary>Access-control event upload. Payload is XML (or JSON) text in pAlarmInfo.</summary>
    public const int EHOME_ALARM_ACS = 11;

    public const int EHOME_ALARM_WIRELESS_INFO = 12;

    /// <summary>ISAPI event upload. pAlarmInfo is a NET_EHOME_ALARM_ISAPI_INFO.</summary>
    public const int EHOME_ISAPI_ALARM = 13;

    public const int EHOME_ALARM_MPDCDATA = 15;
    public const int EHOME_ALARM_QRCODE = 20;
    public const int EHOME_ALARM_FACETEMP = 21;

    // ---- Config selectors ---------------------------------------------------------------

    /// <summary>NET_EALARM_SetSDKInitCfg: path to libeay32.dll / libcrypto.so.</summary>
    public const int INIT_CFG_LIBEAY_PATH = 0;

    /// <summary>NET_EALARM_SetSDKInitCfg: path to ssleay32.dll / libssl.so.</summary>
    public const int INIT_CFG_SSLEAY_PATH = 1;

    /// <summary>NET_EALARM_SetSDKLocalCfg: directory holding the HCAapSDKCom components.</summary>
    public const int LOCAL_CFG_AAP_SDK_COM_PATH = 5;


    /// <summary>NET_EHOME_ALARM_LISTEN_PARAM.byProtocolType.</summary>
    public const byte PROTOCOL_TCP = 0;
    public const byte PROTOCOL_UDP = 1;
    public const byte PROTOCOL_MQTT = 2;

    /// <summary>NET_EHOME_ALARM_ISAPI_INFO.byDataType.</summary>
    public const byte ISAPI_DATA_INVALID = 0;
    public const byte ISAPI_DATA_XML = 1;
    public const byte ISAPI_DATA_JSON = 2;

    private const int MaxFilePathLen = 256;
    private const int SerialLen = 12;

    // ---- Structures ---------------------------------------------------------------------


    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool EHomeMsgCallBack(int iHandle, IntPtr pAlarmMsg, IntPtr pUser);

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_EHOME_ALARM_LISTEN_PARAM
    {
        public NET_EHOME_IPADDRESS struAddress;
        public EHomeMsgCallBack fnMsgCb;
        public IntPtr pUserData;

        /// <summary>0-TCP, 1-UDP, 2-MQTT (ISUP 5.0).</summary>
        public byte byProtocolType;

        /// <summary>Non-zero reuses the CMS port; struAddress must then be the loopback address.</summary>
        public byte byUseCmsPort;

        /// <summary>0-dispatch callbacks on the SDK thread pool, 1-do not use the pool.</summary>
        public byte byUseThreadPool;

        public byte byRes1;
        public int dwKeepAliveSec;
        public int dwTimeOutCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] byRes;

        public void Init()
        {
            struAddress.Init();
            byRes = new byte[20];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_EHOME_ALARM_MSG
    {
        public uint dwAlarmType;
        public IntPtr pAlarmInfo;
        public uint dwAlarmInfoLen;
        public IntPtr pXmlBuf;
        public uint dwXmlBufLen;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SerialLen)]
        public byte[] sSerialNumber;

        public IntPtr pHttpUrl;
        public uint dwHttpUrlLen;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
        public byte[] byRes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_EHOME_ALARM_ISAPI_INFO
    {
        public IntPtr pAlarmData;
        public uint dwAlarmDataLen;

        /// <summary>0-invalid, 1-XML, 2-JSON.</summary>
        public byte byDataType;

        public byte byPicturesNumber;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] byRes;

        /// <summary>byPicturesNumber consecutive NET_EHOME_ALARM_ISAPI_PICDATA records.</summary>
        public IntPtr pPicPackData;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] byRes1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_EHOME_ALARM_ISAPI_PICDATA
    {
        public uint dwPicLen;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] byRes;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxFilePathLen)]
        public byte[] szFilename;

        public IntPtr pPicData;
    }

    // ---- Entry points -------------------------------------------------------------------

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_Init", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Init_Win();

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_Init", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Init_Linux();

    public static bool Init() => IsWindows ? Init_Win() : Init_Linux();

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_Fini", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Fini_Win();

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_Fini", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Fini_Linux();

    public static bool Fini() => IsWindows ? Fini_Win() : Fini_Linux();

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_GetLastError", CallingConvention = CallingConvention.StdCall)]
    private static extern int GetLastError_Win();

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_GetLastError", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetLastError_Linux();

    public static int GetLastError() => IsWindows ? GetLastError_Win() : GetLastError_Linux();

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_StartListen", CallingConvention = CallingConvention.StdCall)]
    private static extern int StartListen_Win(ref NET_EHOME_ALARM_LISTEN_PARAM param);

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_StartListen", CallingConvention = CallingConvention.Cdecl)]
    private static extern int StartListen_Linux(ref NET_EHOME_ALARM_LISTEN_PARAM param);

    public static int StartListen(ref NET_EHOME_ALARM_LISTEN_PARAM param) =>
        IsWindows ? StartListen_Win(ref param) : StartListen_Linux(ref param);

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_StopListen", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StopListen_Win(int listenHandle);

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_StopListen", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StopListen_Linux(int listenHandle);

    public static bool StopListen(int listenHandle) =>
        IsWindows ? StopListen_Win(listenHandle) : StopListen_Linux(listenHandle);

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_SetSDKInitCfg", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSDKInitCfg_Win(int enumType, IntPtr lpInBuff);

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_SetSDKInitCfg", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSDKInitCfg_Linux(int enumType, IntPtr lpInBuff);

    public static bool SetSDKInitCfg(int enumType, IntPtr lpInBuff) =>
        IsWindows ? SetSDKInitCfg_Win(enumType, lpInBuff) : SetSDKInitCfg_Linux(enumType, lpInBuff);

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_SetSDKLocalCfg", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSDKLocalCfg_Win(int enumType, IntPtr lpInBuff);

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_SetSDKLocalCfg", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSDKLocalCfg_Linux(int enumType, IntPtr lpInBuff);

    public static bool SetSDKLocalCfg(int enumType, IntPtr lpInBuff) =>
        IsWindows ? SetSDKLocalCfg_Win(enumType, lpInBuff) : SetSDKLocalCfg_Linux(enumType, lpInBuff);

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_GetSDKLocalCfg", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSDKLocalCfg_Win(int enumType, IntPtr lpOutBuff);

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_GetSDKLocalCfg", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSDKLocalCfg_Linux(int enumType, IntPtr lpOutBuff);

    public static bool GetSDKLocalCfg(int enumType, IntPtr lpOutBuff) =>
        IsWindows ? GetSDKLocalCfg_Win(enumType, lpOutBuff) : GetSDKLocalCfg_Linux(enumType, lpOutBuff);

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_SetDeviceSessionKey", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDeviceSessionKey_Win(ref NET_EHOME_DEV_SESSIONKEY key);

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_SetDeviceSessionKey", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDeviceSessionKey_Linux(ref NET_EHOME_DEV_SESSIONKEY key);

    /// <summary>
    /// Hands the ISUP 5.0 session key to the alarm module. Without this the alarm channel
    /// cannot decrypt what the device uploads.
    /// </summary>
    public static bool SetDeviceSessionKey(ref NET_EHOME_DEV_SESSIONKEY key) =>
        IsWindows ? SetDeviceSessionKey_Win(ref key) : SetDeviceSessionKey_Linux(ref key);

    [DllImport(WindowsDll, EntryPoint = "NET_EALARM_SetLogToFile", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLogToFile_Win(int logLevel, string logDir, [MarshalAs(UnmanagedType.Bool)] bool autoDelete);

    [DllImport(LinuxSo, EntryPoint = "NET_EALARM_SetLogToFile", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLogToFile_Linux(int logLevel, string logDir, [MarshalAs(UnmanagedType.Bool)] bool autoDelete);

    public static bool SetLogToFile(int logLevel, string logDir, bool autoDelete) =>
        IsWindows ? SetLogToFile_Win(logLevel, logDir, autoDelete) : SetLogToFile_Linux(logLevel, logDir, autoDelete);
}
