using System.Runtime.InteropServices;
using System.Text;
using HikvisionIsupServer.Interop;
using HikvisionIsupServer.Models;
using Microsoft.Extensions.Options;
using static QuickNV.HikvisionISUPSDK.Defines;
using static QuickNV.HikvisionISUPSDK.Methods;

namespace HikvisionIsupServer.Services;

/// <summary>
/// ISUP 5.0 CMS registration listener.
/// </summary>
/// <remarks>
/// Registration is also where the platform tells the device where to send events, so the
/// attendance pipeline depends on this callback filling in the alarm server address. A
/// device that registers successfully but is handed no alarm server stays online and
/// silent.
/// </remarks>
public sealed class IsupCmsService : IDisposable
{
    private readonly ILogger<IsupCmsService> _logger;
    private readonly DeviceRegistry _registry;
    private readonly IsupAlarmService _alarm;
    private readonly AttendanceBackfillService _backfill;
    private readonly IsupOptions _options;

    private DEVICE_REGISTER_CB? _registerCallback;
    private int _listenHandle = -1;
    private bool _initialized;

    public IsupCmsService(
        ILogger<IsupCmsService> logger,
        DeviceRegistry registry,
        IsupAlarmService alarm,
        AttendanceBackfillService backfill,
        IOptions<IsupOptions> options)
    {
        _logger = logger;
        _registry = registry;
        _alarm = alarm;
        _backfill = backfill;
        _options = options.Value;
    }

    public void Start()
    {
        if (_initialized)
            return;

        // Loads the native HCISUP SDK files from the package/native search path.
        // For the official SDK, place HCISUPCMS.dll and its dependencies in the
        // application's native directory and/or configure the SDK's native paths.

        if (!NET_ECMS_Init())
        {
            throw new InvalidOperationException(
                $"Hikvision HCISUPCMS initialization failed. SDK error: {NET_ECMS_GetLastError()}");
        }

        _initialized = true;
        _logger.LogInformation("NET_ECMS_Init completed.");

        _registerCallback = RegisterCallback;

        var listen = new NET_EHOME_CMS_LISTEN_PARAM();
        listen.struAddress.Init();

        IsupMarshal.WriteAddress(
            ref listen.struAddress, _options.ListenIp, (ushort)_options.ListenPort);

        listen.fnCB = _registerCallback;
        listen.byRes = new byte[32];

        _listenHandle = NET_ECMS_StartListen(ref listen);

        if (_listenHandle < 0)
        {
            throw new InvalidOperationException(
                $"NET_ECMS_StartListen failed for {_options.ListenIp}:{_options.ListenPort}. " +
                $"SDK error: {NET_ECMS_GetLastError()}");
        }

        _logger.LogInformation(
            "Hikvision ISUP CMS listener started on {Ip}:{Port}.",
            _options.ListenIp,
            _options.ListenPort);
    }

    private bool RegisterCallback(
        int iUserID,
        int dwDataType,
        IntPtr pOutBuffer,
        int dwOutLen,
        IntPtr pInBuffer,
        int dwInLen,
        IntPtr pUser)
    {
        try
        {
            _logger.LogDebug(
                "ISUP callback: LoginId={LoginId}, DataType={DataType}, OutLen={OutLen}, InLen={InLen}",
                iUserID, dwDataType, dwOutLen, dwInLen);

            // Offline notifications carry no registration payload, only the login handle.
            if (dwDataType == ENUM_DEV_OFF)
                return HandleOffline(iUserID);

            if (pOutBuffer == IntPtr.Zero)
            {
                _logger.LogWarning("ISUP callback {DataType} arrived without a registration payload.", dwDataType);
                return true;
            }

            var info = Marshal.PtrToStructure<NET_EHOME_DEV_REG_INFO_V12>(pOutBuffer);
            var deviceId = IsupMarshal.ReadFixedString(info.struRegInfo.byDeviceID);
            var serial = IsupMarshal.ReadFixedString(info.struRegInfo.sDeviceSerial);

            return dwDataType switch
            {
                ENUM_DEV_ON => HandleOnline(iUserID, info, deviceId, serial, pInBuffer),
                ENUM_DEV_AUTH => HandleAuth(deviceId, pInBuffer, dwInLen),
                ENUM_DEV_SESSIONKEY => HandleSessionKey(info, deviceId),
                ENUM_DEV_ADDRESS_CHANGED => HandleAddressChanged(iUserID, info, deviceId, serial),
                _ => true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Hikvision ISUP registration callback.");
            return false;
        }
    }

    private bool HandleOnline(
        int loginId,
        NET_EHOME_DEV_REG_INFO_V12 info,
        string deviceId,
        string serial,
        IntPtr pInBuffer)
    {
        var protocolVersion = GetProtocolVersion(info);

        _logger.LogInformation(
            "ISUP device registration: DeviceId={DeviceId}, Serial={Serial}, Protocol={Protocol}, LoginId={LoginId}",
            deviceId, serial, protocolVersion, loginId);

        if (!string.IsNullOrWhiteSpace(_options.ExpectedDeviceId) &&
            !string.Equals(deviceId, _options.ExpectedDeviceId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Unexpected device ID {DeviceId}; expected {ExpectedDeviceId}.",
                deviceId, _options.ExpectedDeviceId);
        }

        if (pInBuffer == IntPtr.Zero)
        {
            _logger.LogError(
                "Device {DeviceId} registered without a server-info buffer; it will not receive an alarm server address.",
                deviceId);
            return false;
        }

        // The SDK pre-fills defaults here, so read, adjust and write back rather than
        // building a fresh struct.
        var serverInfo = Marshal.PtrToStructure<NET_EHOME_SERVER_INFO_V50>(pInBuffer);

        serverInfo.dwKeepAliveSec = _options.KeepAliveSeconds;
        serverInfo.dwTimeOutCount = _options.TimeoutCount;
        serverInfo.dwNTPInterval = _options.NtpIntervalSeconds;

        ApplyAlarmServer(ref serverInfo);
        ApplyPictureServer(ref serverInfo);

        Marshal.StructureToPtr(serverInfo, pInBuffer, false);

        _registry.Online(new HikvisionDevice
        {
            DeviceId = deviceId,
            SerialNumber = serial,
            LoginId = loginId,
            ProtocolVersion = protocolVersion,
            ConnectedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            Online = true
        });

        _logger.LogInformation("Hikvision device ONLINE: {DeviceId}", deviceId);

        _backfill.RunOnDeviceOnline(deviceId);

        return true;
    }

    /// <summary>
    /// Tells the device where to upload events. Advertise a publicly reachable address here:
    /// the device dials this, so a private bind address behind NAT leaves it with nowhere to
    /// send punches.
    /// </summary>
    private void ApplyAlarmServer(ref NET_EHOME_SERVER_INFO_V50 serverInfo)
    {
        var alarm = _options.Alarm;

        if (!alarm.Enabled)
        {
            _logger.LogWarning(
                "Alarm channel disabled; the device is not being given an alarm server address.");
            return;
        }

        var ip = string.IsNullOrWhiteSpace(alarm.AdvertisedIp) ? alarm.ListenIp : alarm.AdvertisedIp;

        if (ip is "0.0.0.0" or "::")
        {
            _logger.LogWarning(
                "Alarm server address advertised as {Ip}. Set HikvisionIsup:Alarm:AdvertisedIp to the address the device can reach.",
                ip);
        }

        var tcpPort = alarm.AdvertisedPort > 0 ? alarm.AdvertisedPort : alarm.ListenPort;
        var udpPort = alarm.AdvertisedUdpPort > 0 ? alarm.AdvertisedUdpPort : alarm.UdpPort;

        IsupMarshal.WriteAddress(ref serverInfo.struTCPAlarmSever, ip, (ushort)tcpPort);
        IsupMarshal.WriteAddress(ref serverInfo.struUDPAlarmSever, ip, (ushort)udpPort);
        serverInfo.dwAlarmServerType = (int)alarm.Protocol;

        _logger.LogInformation(
            "Advertising alarm server {Ip} (tcp/mqtt {TcpPort}, udp {UdpPort}) as type {Type}.",
            ip, tcpPort, udpPort, alarm.Protocol);
    }

    private void ApplyPictureServer(ref NET_EHOME_SERVER_INFO_V50 serverInfo)
    {
        var picture = _options.PictureServer;

        if (!picture.Enabled || string.IsNullOrWhiteSpace(picture.Ip))
            return;

        IsupMarshal.WriteAddress(ref serverInfo.struPictureSever, picture.Ip, (ushort)picture.Port);
        serverInfo.dwPicServerType = picture.Type;

        _logger.LogInformation(
            "Advertising picture server {Ip}:{Port} as type {Type}.",
            picture.Ip, picture.Port, picture.Type);
    }

    /// <summary>
    /// ISUP 5.0 registration challenge: the device expects the shared key back in the input
    /// buffer. An empty or wrong key leaves the terminal stuck at "registering".
    /// </summary>
    private bool HandleAuth(string deviceId, IntPtr pInBuffer, int dwInLen)
    {
        if (string.IsNullOrWhiteSpace(_options.IsupKey))
        {
            _logger.LogError(
                "Device {DeviceId} requested ISUP 5.0 authentication but no HikvisionIsup:IsupKey is configured.",
                deviceId);
            return false;
        }

        if (pInBuffer == IntPtr.Zero)
            return false;

        var key = Encoding.ASCII.GetBytes(_options.IsupKey);
        var capacity = dwInLen > 0 ? dwInLen : key.Length + 1;

        if (key.Length >= capacity)
        {
            _logger.LogError(
                "Configured ISUP key is {Length} bytes but the SDK buffer holds {Capacity}.",
                key.Length, capacity);
            return false;
        }

        Marshal.Copy(key, 0, pInBuffer, key.Length);
        Marshal.WriteByte(pInBuffer, key.Length, 0);

        _logger.LogDebug("Answered the ISUP 5.0 authentication challenge for {DeviceId}.", deviceId);
        return true;
    }

    /// <summary>
    /// The session key has to reach both SDK modules. The CMS module alone is enough to keep
    /// the device registered, but the alarm module needs it to decrypt uploaded events.
    /// </summary>
    private bool HandleSessionKey(NET_EHOME_DEV_REG_INFO_V12 info, string deviceId)
    {
        var sessionKey = new NET_EHOME_DEV_SESSIONKEY
        {
            sDeviceID = new byte[NET_EHOME_DEVICEID_LEN],
            sSessionKey = new byte[MAX_MASTER_KEY_LEN]
        };

        info.struRegInfo.byDeviceID.CopyTo(sessionKey.sDeviceID, 0);
        info.struRegInfo.bySessionKey.CopyTo(sessionKey.sSessionKey, 0);

        var cmsAccepted = NET_ECMS_SetDeviceSessionKey(ref sessionKey);
        var alarmAccepted = _alarm.ApplySessionKey(ref sessionKey);

        if (!cmsAccepted)
        {
            _logger.LogWarning(
                "NET_ECMS_SetDeviceSessionKey rejected the key for {DeviceId}. SDK error: {Error}",
                deviceId, NET_ECMS_GetLastError());
        }

        if (!alarmAccepted)
        {
            _logger.LogWarning(
                "The alarm module did not accept the session key for {DeviceId}; encrypted events may not decode.",
                deviceId);
        }

        _logger.LogDebug("ISUP session key applied for {DeviceId}.", deviceId);
        return true;
    }

    private bool HandleAddressChanged(
        int loginId,
        NET_EHOME_DEV_REG_INFO_V12 info,
        string deviceId,
        string serial)
    {
        _logger.LogInformation("Device {DeviceId} changed address; refreshing its registration.", deviceId);

        _registry.Online(new HikvisionDevice
        {
            DeviceId = deviceId,
            SerialNumber = serial,
            LoginId = loginId,
            ProtocolVersion = GetProtocolVersion(info),
            ConnectedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            Online = true
        });

        return true;
    }

    private bool HandleOffline(int loginId)
    {
        var device = _registry.FindByLoginId(loginId);

        if (device is null)
        {
            _logger.LogInformation("Device with login {LoginId} went offline before it was registered.", loginId);
            return true;
        }

        _registry.Offline(device.DeviceId);
        _logger.LogWarning("Hikvision device OFFLINE: {DeviceId}", device.DeviceId);
        return true;
    }

    private static int GetProtocolVersion(NET_EHOME_DEV_REG_INFO_V12 info)
    {
        var version = info.struRegInfo.byDevProtocolVersion;

        if (version.Length > 0)
        {
            if (version[0] == '2' || version[0] == 2) return 2;
            if (version[0] == '4' || version[0] == 4) return 4;
            if (version[0] == '5' || version[0] == 5) return 5;
        }

        return 5;
    }

    public void Dispose()
    {
        if (_listenHandle >= 0)
        {
            try
            {
                NET_ECMS_StopListen(_listenHandle);
            }
            catch
            {
                // Native SDK may already have been unloaded.
            }

            _listenHandle = -1;
        }

        if (_initialized)
        {
            try
            {
                NET_ECMS_Fini();
            }
            catch
            {
            }

            _initialized = false;
        }

        GC.KeepAlive(_registerCallback);
    }
}
