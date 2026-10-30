using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using HikvisionIsupServer.Interop;
using HikvisionIsupServer.Models;
using Microsoft.Extensions.Options;
using static HikvisionIsupServer.Interop.HikvisionAlarmNative;
using static QuickNV.HikvisionISUPSDK.Defines;

namespace HikvisionIsupServer.Services;

/// <summary>
/// Hosts the ISUP alarm listener. This is the channel that carries attendance punches;
/// the CMS listener only handles registration.
/// </summary>
public sealed class IsupAlarmService : IDisposable
{
    private const int MaxPayloadBytes = 8 * 1024 * 1024;
    private const int MaxPictureBytes = 16 * 1024 * 1024;
    private const int MaxPicturesPerEvent = 8;

    private readonly AttendanceIngestService _ingest;
    private readonly AlarmOptions _options;
    private readonly ILogger<IsupAlarmService> _logger;
    private readonly IHostEnvironment _environment;

    // Held for the lifetime of the listener: the SDK keeps the raw pointer, so letting the
    // delegate be collected would crash the process on the next event.
    private EHomeMsgCallBack? _callback;

    private int _listenHandle = -1;
    private bool _initialised;

    public IsupAlarmService(
        AttendanceIngestService ingest,
        IOptions<IsupOptions> options,
        IHostEnvironment environment,
        ILogger<IsupAlarmService> logger)
    {
        _ingest = ingest;
        _options = options.Value.Alarm;
        _environment = environment;
        _logger = logger;
    }

    public bool IsRunning => _listenHandle >= 0;

    public string Status { get; private set; } = "not started";

    /// <summary>
    /// Starts the listener. A failure here is logged rather than thrown: registration and the
    /// device API stay useful even when the event channel cannot bind.
    /// </summary>
    public void Start()
    {
        if (!_options.Enabled)
        {
            Status = "disabled";
            _logger.LogWarning(
                "ISUP alarm listener is disabled. No attendance events will be received.");
            return;
        }

        if (_initialised)
            return;

        try
        {
            ConfigureNativePaths();

            if (!Init())
            {
                Status = $"NET_EALARM_Init failed (SDK error {GetLastError()})";
                _logger.LogError("NET_EALARM_Init failed. SDK error: {Error}", GetLastError());
                return;
            }

            _initialised = true;
            ConfigureNativeLogging();
            ConfigureAccessSecurity();

            _callback = OnAlarm;

            var param = new NET_EHOME_ALARM_LISTEN_PARAM();
            param.Init();

            IsupMarshal.WriteAddress(ref param.struAddress, _options.ListenIp, (ushort)_options.ListenPort);
            param.fnMsgCb = _callback;
            param.pUserData = IntPtr.Zero;
            param.byProtocolType = (byte)_options.Protocol;
            param.byUseCmsPort = (byte)(_options.UseCmsPort ? 1 : 0);
            param.byUseThreadPool = (byte)(_options.UseThreadPool ? 0 : 1);
            param.dwKeepAliveSec = _options.KeepAliveSeconds;
            param.dwTimeOutCount = _options.TimeoutCount;

            _listenHandle = StartListen(ref param);

            if (_listenHandle < 0)
            {
                var error = GetLastError();
                Status = $"NET_EALARM_StartListen failed (SDK error {error})";
                _logger.LogError(
                    "NET_EALARM_StartListen failed for {Ip}:{Port} ({Protocol}). SDK error: {Error}",
                    _options.ListenIp, _options.ListenPort, _options.Protocol, error);
                return;
            }

            Status = $"listening on {_options.ListenIp}:{_options.ListenPort} ({_options.Protocol})";
            _logger.LogInformation(
                "ISUP alarm listener started on {Ip}:{Port} using {Protocol}.",
                _options.ListenIp, _options.ListenPort, _options.Protocol);
        }
        catch (DllNotFoundException ex)
        {
            Status = "HCISUPAlarm native library not found";
            _logger.LogError(ex,
                "Could not load the HCISUPAlarm native library. Attendance events are unavailable.");
        }
        catch (Exception ex)
        {
            Status = $"start failed: {ex.Message}";
            _logger.LogError(ex, "Failed to start the ISUP alarm listener.");
        }
    }

    /// <summary>
    /// Hands the ISUP 5.0 session key negotiated during registration to the alarm module.
    /// Without it the device uploads events the alarm channel cannot decrypt.
    /// </summary>
    public bool ApplySessionKey(ref NET_EHOME_DEV_SESSIONKEY key)
    {
        if (!_initialised)
            return false;

        try
        {
            return SetDeviceSessionKey(ref key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NET_EALARM_SetDeviceSessionKey failed.");
            return false;
        }
    }

    // ---- Native callback ------------------------------------------------------------------

    private bool OnAlarm(int handle, IntPtr pAlarmMsg, IntPtr pUser)
    {
        try
        {
            if (pAlarmMsg == IntPtr.Zero)
                return false;

            var msg = Marshal.PtrToStructure<NET_EHOME_ALARM_MSG>(pAlarmMsg);
            var alarmType = (int)msg.dwAlarmType;
            _logger.LogInformation(
                "Alarm callback handle={Handle} type={Type} infoLen={InfoLen} xmlLen={XmlLen} httpLen={HttpLen}",
                handle, alarmType, msg.dwAlarmInfoLen, msg.dwXmlBufLen, msg.dwHttpUrlLen);

            // ISUP 4.0 delivers ISAPI events with the picture data still attached, and marks
            // them only by supplying an HTTP URL. The vendor demo re-tags them the same way.
            if (msg.pHttpUrl != IntPtr.Zero && msg.dwHttpUrlLen > 0)
                alarmType = EHOME_ISAPI_ALARM;

            string? payload = null;
            var format = RawAlarmFormat.Unknown;
            IReadOnlyList<RawAlarmPicture> pictures = Array.Empty<RawAlarmPicture>();

            switch (alarmType)
            {
                case EHOME_ISAPI_ALARM:
                    (payload, format, pictures) = ReadIsapiAlarm(msg);
                    break;

                case EHOME_ALARM_ACS:
                    payload = ReadText(msg.pAlarmInfo, msg.dwAlarmInfoLen);
                    format = RawAlarmFormat.Unknown;
                    break;

                default:
                    payload = ReadText(msg.pXmlBuf, msg.dwXmlBufLen);
                    format = RawAlarmFormat.Xml;
                    break;
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                _logger.LogDebug(
                    "Alarm type {AlarmType} arrived with no readable payload.", alarmType);
                return true;
            }

            _ingest.Submit(new RawAlarmMessage
            {
                AlarmType = alarmType,
                ReceivedAt = DateTimeOffset.UtcNow,
                DeviceSerial = ReadFixedAscii(msg.sSerialNumber),
                Payload = payload,
                Format = format,
                Pictures = pictures
            });

            return true;
        }
        catch (Exception ex)
        {
            // Never let an exception cross back into native code.
            _logger.LogError(ex, "Error in the ISUP alarm callback.");
            return false;
        }
    }

    private (string? Payload, RawAlarmFormat Format, IReadOnlyList<RawAlarmPicture> Pictures)
        ReadIsapiAlarm(NET_EHOME_ALARM_MSG msg)
    {
        if (msg.pAlarmInfo == IntPtr.Zero)
            return (null, RawAlarmFormat.Unknown, Array.Empty<RawAlarmPicture>());

        var info = Marshal.PtrToStructure<NET_EHOME_ALARM_ISAPI_INFO>(msg.pAlarmInfo);

        var format = info.byDataType switch
        {
            ISAPI_DATA_JSON => RawAlarmFormat.Json,
            ISAPI_DATA_XML => RawAlarmFormat.Xml,
            _ => RawAlarmFormat.Unknown
        };

        var payload = format == RawAlarmFormat.Unknown
            ? null
            : ReadText(info.pAlarmData, info.dwAlarmDataLen);

        return (payload, format, ReadPictures(info));
    }

    private IReadOnlyList<RawAlarmPicture> ReadPictures(NET_EHOME_ALARM_ISAPI_INFO info)
    {
        if (info.pPicPackData == IntPtr.Zero || info.byPicturesNumber == 0)
            return Array.Empty<RawAlarmPicture>();

        var count = Math.Min((int)info.byPicturesNumber, MaxPicturesPerEvent);

        if (count < info.byPicturesNumber)
        {
            _logger.LogWarning(
                "Event carried {Total} pictures; keeping the first {Kept}.",
                info.byPicturesNumber, count);
        }

        var stride = Marshal.SizeOf<NET_EHOME_ALARM_ISAPI_PICDATA>();
        var pictures = new List<RawAlarmPicture>(count);

        for (var i = 0; i < count; i++)
        {
            var pic = Marshal.PtrToStructure<NET_EHOME_ALARM_ISAPI_PICDATA>(
                info.pPicPackData + (i * stride));

            if (pic.pPicData == IntPtr.Zero || pic.dwPicLen == 0 || pic.dwPicLen > MaxPictureBytes)
                continue;

            var data = new byte[pic.dwPicLen];
            Marshal.Copy(pic.pPicData, data, 0, data.Length);

            pictures.Add(new RawAlarmPicture(
                ReadFixedAscii(pic.szFilename) ?? $"picture_{i}.jpg", data));
        }

        return pictures;
    }

    private string? ReadText(IntPtr pointer, uint length)
    {
        if (pointer == IntPtr.Zero || length == 0)
            return null;

        if (length > MaxPayloadBytes)
        {
            _logger.LogWarning(
                "Alarm payload of {Length} bytes exceeds the {Max} byte limit; ignoring.",
                length, MaxPayloadBytes);
            return null;
        }

        var buffer = new byte[length];
        Marshal.Copy(pointer, buffer, 0, buffer.Length);

        var end = buffer.Length;
        while (end > 0 && (buffer[end - 1] == 0 || buffer[end - 1] <= 0x20))
            end--;

        return end == 0 ? null : Encoding.UTF8.GetString(buffer, 0, end);
    }

    private static string? ReadFixedAscii(byte[]? bytes)
    {
        var text = IsupMarshal.ReadFixedString(bytes);
        return text.Length == 0 ? null : text;
    }

    // ---- Native configuration ---------------------------------------------------------------

    private void ConfigureNativePaths()
    {
        var nativeDirectory = ResolveNativeDirectory();

        if (nativeDirectory is null)
        {
            _logger.LogWarning(
                "Could not locate the ISUP native directory; relying on the default SDK search path.");
            return;
        }

        _logger.LogDebug("ISUP native directory: {Directory}", nativeDirectory);

        // Prefer OpenSSL 1.1 (exports OpenSSL_version). SDK 1.0 libs cause EHOME50_ERROR on MQTT.
        var crypto = IsWindows ? "libeay32.dll" : "libcrypto.so";
        var ssl = IsWindows ? "ssleay32.dll" : "libssl.so";
        if (!IsWindows && !File.Exists(Path.Combine(nativeDirectory, crypto)))
            crypto = "libcrypto.so";
        if (!IsWindows && !File.Exists(Path.Combine(nativeDirectory, ssl)))
            ssl = "libssl.so";

        SetPathConfig(
            "libeay/libcrypto", INIT_CFG_LIBEAY_PATH, Path.Combine(nativeDirectory, crypto), SetSDKInitCfg);

        SetPathConfig(
            "ssleay/libssl", INIT_CFG_SSLEAY_PATH, Path.Combine(nativeDirectory, ssl), SetSDKInitCfg);

        var components = Path.Combine(nativeDirectory, "HCAapSDKCom");

        if (Directory.Exists(components))
        {
            SetPathConfig(
                "HCAapSDKCom", LOCAL_CFG_AAP_SDK_COM_PATH, components, SetSDKLocalCfg);
        }
    }

    private void SetPathConfig(
        string description,
        int selector,
        string path,
        Func<int, IntPtr, bool> apply)
    {
        var buffer = Marshal.AllocHGlobal(256);

        try
        {
            var bytes = Encoding.ASCII.GetBytes(path);

            for (var i = 0; i < 256; i++)
                Marshal.WriteByte(buffer, i, 0);

            Marshal.Copy(bytes, 0, buffer, Math.Min(bytes.Length, 255));

            if (!apply(selector, buffer))
            {
                _logger.LogDebug(
                    "Alarm SDK rejected the {Description} path {Path}.", description, path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not set the {Description} path.", description);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }


    private void ConfigureAccessSecurity()
    {
        try
        {
            // HCISUPAlarm 2.5.0.35: ACTIVE_ACCESS_SECURITY = 0, struct dwSize = 132
            // byAccessSecurity = 0 allows MQTT PUBLISH (punches); non-zero rejects them.
            const int ActiveAccessSecurity = 0;
            const int DwSize = 132;
            var ptr = Marshal.AllocHGlobal(DwSize);
            try
            {
                for (var i = 0; i < DwSize; i++)
                    Marshal.WriteByte(ptr, i, 0);
                Marshal.WriteInt32(ptr, 0, DwSize);
                Marshal.WriteByte(ptr, 4, 0);
                if (SetSDKLocalCfg(ActiveAccessSecurity, ptr))
                {
                    _logger.LogInformation(
                        "ISUP alarm AccessSecurity=0 (enum {Enum}, dwSize={Size}).",
                        ActiveAccessSecurity, DwSize);
                    return;
                }

                _logger.LogWarning(
                    "ACTIVE_ACCESS_SECURITY SetSDKLocalCfg returned false (err={Err}).",
                    GetLastError());
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConfigureAccessSecurity failed; continuing.");
        }
    }

    private void ConfigureNativeLogging()
    {
        if (_options.NativeLogLevel <= 0)
            return;

        var directory = Path.IsPathRooted(_options.NativeLogDirectory)
            ? _options.NativeLogDirectory
            : Path.Combine(_environment.ContentRootPath, _options.NativeLogDirectory);

        try
        {
            Directory.CreateDirectory(directory);
            SetLogToFile(_options.NativeLogLevel, directory, false);
            _logger.LogInformation("ISUP alarm SDK logging to {Directory}.", directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enable ISUP alarm SDK file logging.");
        }
    }

    private static string? ResolveNativeDirectory()
    {
        var rid = IsWindows ? "win-x64" : "linux-x64";
        var marker = IsWindows ? "HCISUPAlarm.dll" : "libHCISUPAlarm.so";

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win7-x64", "native"),
            Path.Combine(AppContext.BaseDirectory, "native"),
            AppContext.BaseDirectory
        };

        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, marker)));
    }

    public void Dispose()
    {
        if (_listenHandle >= 0)
        {
            try
            {
                StopListen(_listenHandle);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NET_EALARM_StopListen failed during shutdown.");
            }

            _listenHandle = -1;
        }

        if (_initialised)
        {
            try
            {
                Fini();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NET_EALARM_Fini failed during shutdown.");
            }

            _initialised = false;
        }

        Status = "stopped";
        GC.KeepAlive(_callback);
        _callback = null;
    }
}
