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

    // Last ISUP5 session key bytes, re-applied under serial when DAS provides it.
    private byte[]? _lastSessionKeyBytes;
    private string? _lastSessionKeyDeviceId;

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

        // ISUP 5.0 CMS needs the same OpenSSL libs as the alarm module. Without
        // this, AUTH/SESSIONKEY may succeed while ENUM_DEV_ON (real LoginId) never
        // arrives — and ISAPI passthrough then fails with SDK error 17.
        ConfigureCmsNativePaths();

        if (!NET_ECMS_Init())
        {
            throw new InvalidOperationException(
                $"Hikvision HCISUPCMS initialization failed. SDK error: {NET_ECMS_GetLastError()}");
        }

        _initialized = true;
        _logger.LogInformation("NET_ECMS_Init completed.");
        TryEnableCmsSdkLog();

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

        // ISUP 5.0 dials CMS MQTT for EHOME50_MASTER_KEY_SEARCH before SESSIONKEY arrives.
        // Without a pre-seeded master key FindDevAuthInfo fails and ENUM_DEV_ON never comes.
        PreseedMasterKeys();
    }


    private void PreseedMasterKeys()
    {
        if (string.IsNullOrWhiteSpace(_options.IsupKey))
        {
            _logger.LogWarning("No IsupKey configured; cannot pre-seed ISUP master keys.");
            return;
        }

        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.ExpectedDeviceId))
            ids.Add(_options.ExpectedDeviceId.Trim());
        foreach (var s in _options.KnownDeviceSerials ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(s))
                ids.Add(s.Trim());
        }

        if (ids.Count == 0)
        {
            _logger.LogWarning("No ExpectedDeviceId/KnownDeviceSerials to pre-seed.");
            return;
        }

        var keyBytes = Encoding.ASCII.GetBytes(_options.IsupKey);
        _lastSessionKeyBytes = keyBytes; // until real SESSIONKEY overwrites

        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var sessionKey = new NET_EHOME_DEV_SESSIONKEY
            {
                sDeviceID = new byte[NET_EHOME_DEVICEID_LEN],
                sSessionKey = new byte[MAX_MASTER_KEY_LEN]
            };
            var idBytes = Encoding.ASCII.GetBytes(id);
            Array.Copy(idBytes, sessionKey.sDeviceID, Math.Min(idBytes.Length, sessionKey.sDeviceID.Length - 1));
            Array.Copy(keyBytes, sessionKey.sSessionKey, Math.Min(keyBytes.Length, sessionKey.sSessionKey.Length));

            var cms = NET_ECMS_SetDeviceSessionKey(ref sessionKey);
            var alarm = _alarm.ApplySessionKey(ref sessionKey);
            _logger.LogInformation(
                "Pre-seeded ISUP master key for {Id}: CMS={Cms} Alarm={Alarm} err={Error}",
                id, cms, alarm, NET_ECMS_GetLastError());
        }
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
            _logger.LogInformation(
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
                // Some firmwares send SESSIONKEY_REQ (6) instead of / in addition to SESSIONKEY (4).
                ENUM_DEV_SESSIONKEY_REQ => HandleSessionKey(info, deviceId),
                ENUM_DEV_ADDRESS_CHANGED => HandleAddressChanged(iUserID, info, deviceId, serial),
                ENUM_DEV_DAS_REQ => HandleDasRequest(iUserID, info, deviceId, serial, pInBuffer, dwInLen),
                ENUM_DEV_DAS_REREGISTER => HandleOnline(iUserID, info, deviceId, serial, pInBuffer),
                _ => LogUnknownCallback(iUserID, dwDataType, deviceId)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Hikvision ISUP registration callback.");
            return false;
        }
    }

    private bool LogUnknownCallback(int loginId, int dataType, string deviceId)
    {
        _logger.LogWarning(
            "Unhandled ISUP callback type {DataType} for device {DeviceId} login {LoginId}.",
            dataType, deviceId, loginId);
        return true;
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

        _logger.LogInformation("Hikvision device ONLINE: {DeviceId}, LoginId={LoginId}", deviceId, loginId);

        // Alarm MQTT members appear shortly after DEV_ON; keep rebinding until GetMember works.
        RebindSessionKeySoon(deviceId, serial);
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 6; i++)
                {
                    await Task.Delay(2000);
                    if (_registry.Find(deviceId) is not { Online: true })
                        return;
                    if (_lastSessionKeyBytes is null || _lastSessionKeyBytes.Length == 0)
                        return;
                    var info = new NET_EHOME_DEV_REG_INFO_V12();
                    ApplySessionKeyForIds(info, deviceId, serial);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-DEV_ON session key rebind loop failed for {DeviceId}.", deviceId);
            }
        });

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

        // DEV_ON alarm address: prefer EventPort so DAS can keep CMS:7660 while events go to AMS:7663.
        var tcpPort = alarm.EventPort > 0
            ? alarm.EventPort
            : (alarm.AdvertisedPort > 0 ? alarm.AdvertisedPort : alarm.ListenPort);
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

    /// <summary>
    /// ISUP 5.0 DAS request: tell the device which public IP/port to use for events (alarm).
    /// Without answering this, Register Status stays Offline.
    /// </summary>
    private bool HandleDasRequest(
        int loginId,
        NET_EHOME_DEV_REG_INFO_V12 info,
        string deviceId,
        string serial,
        IntPtr pInBuffer,
        int dwInLen)
    {
        if (pInBuffer == IntPtr.Zero || dwInLen <= 0)
        {
            _logger.LogError("DAS request for {DeviceId} had no input buffer.", deviceId);
            return false;
        }

        var alarm = _options.Alarm;
        var ip = string.IsNullOrWhiteSpace(alarm.AdvertisedIp) ? alarm.ListenIp : alarm.AdvertisedIp;
        if (string.IsNullOrWhiteSpace(ip) || ip is "0.0.0.0" or "::")
        {
            _logger.LogError(
                "DAS request for {DeviceId}: set HikvisionIsup:Alarm:AdvertisedIp to a public IP the device can reach.",
                deviceId);
            return false;
        }

        // ISUP 5.0 vendor demos put the CMS listen port in DAS (device keeps MQTT on CMS).
        // Separate alarm port leaves CMS before ENUM_DEV_ON and yields keepalive-only sessions.
        var tcpPort = alarm.AdvertisedPort > 0
            ? alarm.AdvertisedPort
            : (short)(alarm.UseCmsPort ? _options.ListenPort : alarm.ListenPort);
        var udpPort = alarm.AdvertisedUdpPort > 0 ? alarm.AdvertisedUdpPort : alarm.UdpPort;
        if (tcpPort <= 0) tcpPort = (short)(_options.ListenPort > 0 ? _options.ListenPort : 7660);
        if (udpPort <= 0) udpPort = (short)7662;

        var protocolVersion = GetProtocolVersion(info);
        var dasJson =
            "{\"Type\":\"DAS\",\"DasInfo\":{\"Address\":\"" + ip +
            "\",\"Domain\":\"" + ip +
            "\",\"ServerID\":\"das_" + ip + "_" + tcpPort +
            "\",\"Port\":" + tcpPort +
            ",\"UdpPort\":" + udpPort + "}}";

        var bytes = Encoding.ASCII.GetBytes(dasJson);
        if (bytes.Length + 1 > dwInLen)
        {
            _logger.LogError(
                "DAS JSON length {Len} exceeds buffer {Cap} for {DeviceId}.",
                bytes.Length, dwInLen, deviceId);
            return false;
        }

        Marshal.Copy(bytes, 0, pInBuffer, bytes.Length);
        Marshal.WriteByte(pInBuffer, bytes.Length, 0);

        _logger.LogInformation(
            "Answered ISUP DAS request for {DeviceId}: alarm {Ip}:{Port} (udp {Udp}).",
            deviceId, ip, tcpPort, udpPort);

        // MQTT connects with serial as UserName — bind the cached session key under serial too.
        ApplySessionKeyForIds(info, deviceId, serial);


        // DAS proves the terminal can reach us and gets Online on the LCD, but the
        // CMS LoginId is still -1 until ENUM_DEV_ON. Do NOT invent a fake LoginId —
        // NET_ECMS_* ISAPI passthrough then fails with SDK error 17 and Workwise
        // looks like everything is "ignored".
        var existing = _registry.Find(deviceId);
        var keepLogin = loginId >= 0
            ? loginId
            : (existing is { LoginId: >= 0 } ? existing.LoginId : -1);

        _registry.Online(new HikvisionDevice
        {
            DeviceId = deviceId,
            SerialNumber = serial,
            LoginId = keepLogin,
            ProtocolVersion = protocolVersion,
            ConnectedAt = existing?.ConnectedAt ?? DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            Online = true
        });
        _logger.LogInformation(
            "Hikvision device ONLINE (via DAS): {DeviceId}, LoginId={LoginId} (ISAPI ready only when LoginId>=0 from DEV_ON)",
            deviceId, keepLogin);

        if (keepLogin < 0)
            _logger.LogWarning(
                "Device {DeviceId} has no CMS LoginId yet — punches may still arrive on MQTT; user/fingerprint ISAPI is blocked until ENUM_DEV_ON.",
                deviceId);
        // Do not ISAPI-backfill from DAS: a PassThrough on a fresh MQTT session
        // tears the link down (SDK error 8 / GetMember Failed).

        // MQTT connects a moment after DAS. Re-bind session key so Alarm FindDevAuthInfo
        // succeeds (otherwise the terminal stays on keepalive and never PUBLISHes punches).
        RebindSessionKeySoon(deviceId, serial);
        return true;
    }


    private void RebindSessionKeySoon(string deviceId, string serial)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);
                if (_lastSessionKeyBytes is null || _lastSessionKeyBytes.Length == 0)
                    return;
                // Fake reg info carrier — ApplySessionKeyForIds only needs session bytes + ids.
                var info = new NET_EHOME_DEV_REG_INFO_V12();
                ApplySessionKeyForIds(info, deviceId, serial);
                await Task.Delay(2000);
                ApplySessionKeyForIds(info, deviceId, serial);
                _logger.LogInformation("Re-bound ISUP session key after DAS for {DeviceId}/{Serial}.", deviceId, serial);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-DAS session key rebind failed for {DeviceId}.", deviceId);
            }
        });
    }

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

    private void ApplySessionKeyForIds(NET_EHOME_DEV_REG_INFO_V12 info, params string[] ids)
    {
        var keyBytes = info.struRegInfo.bySessionKey;
        // Prefer cached key from SESSIONKEY callback when DAS re-applies.
        if (_lastSessionKeyBytes is { Length: > 0 })
            keyBytes = _lastSessionKeyBytes;

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var sessionKey = new NET_EHOME_DEV_SESSIONKEY
            {
                sDeviceID = new byte[NET_EHOME_DEVICEID_LEN],
                sSessionKey = new byte[MAX_MASTER_KEY_LEN]
            };
            var idBytes = Encoding.ASCII.GetBytes(id.Trim());
            Array.Copy(idBytes, sessionKey.sDeviceID, Math.Min(idBytes.Length, sessionKey.sDeviceID.Length - 1));
            keyBytes.CopyTo(sessionKey.sSessionKey, 0);

            var cms = NET_ECMS_SetDeviceSessionKey(ref sessionKey);
            var alarm = _alarm.ApplySessionKey(ref sessionKey);
            _logger.LogInformation(
                "ISUP session key bound to {Id}: CMS={Cms} Alarm={Alarm} err={Error}",
                id, cms, alarm, NET_ECMS_GetLastError());
        }
    }

    private bool HandleSessionKey(NET_EHOME_DEV_REG_INFO_V12 info, string deviceId)
    {
        _lastSessionKeyBytes = (byte[])info.struRegInfo.bySessionKey.Clone();
        _lastSessionKeyDeviceId = deviceId;

        var nonZero = _lastSessionKeyBytes.Count(b => b != 0);
        _logger.LogInformation(
            "ISUP SESSIONKEY for {DeviceId}: len={Len}, nonZero={NonZero}, hex={Hex}",
            deviceId,
            _lastSessionKeyBytes.Length,
            nonZero,
            Convert.ToHexString(_lastSessionKeyBytes.Take(16).ToArray()));

        var serial = IsupMarshal.ReadFixedString(info.struRegInfo.sDeviceSerial);
        ApplySessionKeyForIds(info, deviceId, serial);

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



    private void TryEnableCmsSdkLog()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs", "isup-sdk");
            Directory.CreateDirectory(dir);
            var ok = NET_ECMS_SetLogToFile(3, dir.ToCharArray(), false);
            _logger.LogInformation("CMS SDK logging to {Dir} ok={Ok}", dir, ok);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enable CMS SDK file log.");
        }
    }

    private void ConfigureCmsNativePaths()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "runtimes", "linux-x64", "native"),
                Path.Combine(baseDir, "native"),
                baseDir
            };
            string? native = null;
            foreach (var c in candidates)
            {
                if (File.Exists(Path.Combine(c, "libcrypto.so")) ||
                    File.Exists(Path.Combine(c, "libHCISUPCMS.so")))
                {
                    native = c;
                    break;
                }
            }
            if (native is null)
            {
                _logger.LogWarning("CMS native directory not found; OpenSSL path not set.");
                return;
            }

            // Same enum values as NET_EHOME_CMS_INIT_CFG_LIBEAY_PATH / SSLEAY_PATH (0 / 1).
            SetCmsLibPath(0, Path.Combine(native, "libcrypto.so"));
            SetCmsLibPath(1, Path.Combine(native, "libssl.so"));
            _logger.LogInformation("CMS OpenSSL paths set under {Native}.", native);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not configure CMS OpenSSL paths.");
        }
    }

    private static void SetCmsLibPath(int enumType, string path)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (var i = 0; i < 256; i++)
                Marshal.WriteByte(buffer, i, 0);
            var bytes = Encoding.ASCII.GetBytes(path);
            Marshal.Copy(bytes, 0, buffer, Math.Min(bytes.Length, 255));
            NET_ECMS_SetSDKInitCfg(enumType, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
