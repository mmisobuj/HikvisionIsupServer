namespace HikvisionIsupServer.Services;

public sealed class IsupOptions
{
    public string ListenIp { get; set; } = "0.0.0.0";
    public short ListenPort { get; set; } = 7660;
    public string? ExpectedDeviceId { get; set; }
    public int ApiPort { get; set; } = 5080;

    public int KeepAliveSeconds { get; set; } = 15;
    public int TimeoutCount { get; set; } = 6;
    public int NtpIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// ISUP 5.0 registration key. Must match the encryption key configured on the terminal.
    /// Treat as a credential: supply it through an environment variable or user-secrets
    /// (<c>HikvisionIsup__IsupKey</c>), never through a committed appsettings file.
    /// </summary>
    public string? IsupKey { get; set; }

    /// <summary>ISUP alarm/event channel. This is what carries attendance punches.</summary>
    public AlarmOptions Alarm { get; set; } = new();

    /// <summary>Picture storage server advertised to the device during registration.</summary>
    public PictureServerOptions PictureServer { get; set; } = new();
}

public enum AlarmProtocol
{
    /// <summary>ISUP 4.0 TCP upload.</summary>
    Tcp = 0,

    /// <summary>ISUP 4.0 UDP upload.</summary>
    Udp = 1,

    /// <summary>ISUP 5.0 upload. Reuses the TCP port.</summary>
    Mqtt = 2
}

public sealed class AlarmOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Local bind address for the alarm listener.</summary>
    public string ListenIp { get; set; } = "0.0.0.0";

    /// <summary>Local bind port. For ISUP 5.0 (MQTT) this is also the port the device dials.</summary>
    public short ListenPort { get; set; } = 7663;

    /// <summary>UDP port advertised for ISUP 4.0 devices.</summary>
    public short UdpPort { get; set; } = 7662;

    public AlarmProtocol Protocol { get; set; } = AlarmProtocol.Mqtt;

    /// <summary>
    /// Address the device is told to send events to. Leave empty to advertise
    /// <see cref="ListenIp"/>; behind NAT this must be the public address.
    /// </summary>
    public string? AdvertisedIp { get; set; }

    /// <summary>Advertised TCP/MQTT port. 0 uses <see cref="ListenPort"/>.</summary>
    public short AdvertisedPort { get; set; }

    /// <summary>Advertised UDP port. 0 uses <see cref="UdpPort"/>.</summary>
    public short AdvertisedUdpPort { get; set; }

    public int KeepAliveSeconds { get; set; } = 30;
    public int TimeoutCount { get; set; } = 3;

    /// <summary>Reuse the CMS port instead of binding a second listener.</summary>
    public bool UseCmsPort { get; set; }

    /// <summary>Dispatch alarm callbacks on the SDK thread pool.</summary>
    public bool UseThreadPool { get; set; } = true;

    /// <summary>Write the native SDK log. 0 disables, 1-3 increase verbosity.</summary>
    public int NativeLogLevel { get; set; }

    public string NativeLogDirectory { get; set; } = "logs/isup-sdk";
}

public sealed class PictureServerOptions
{
    /// <summary>
    /// Off by default. The picture storage server is a separate HCISUPSS listener that this
    /// project does not host, so advertising an address that nothing serves only makes the
    /// device retry uploads. Event photos still arrive inline on the ISAPI alarm channel.
    /// </summary>
    public bool Enabled { get; set; }

    public string? Ip { get; set; }
    public short Port { get; set; } = 6011;

    /// <summary>0-Tomcat, 1-VRB, 2-cloud storage, 3-KMS, 4-ISUP 5.0.</summary>
    public int Type { get; set; } = 2;
}
