# Hikvision DS-K1T321EX — ISUP 5.0 Attendance Server

An ASP.NET Core/.NET 9 host that registers Hikvision ISUP 5.0 access-control terminals,
receives their access events, normalises them into attendance records, and exposes them over
a REST API.

## Device configuration

Configure the DS-K1T321EX approximately as:

- ISUP: Enabled
- Protocol Version: ISUP 5.0
- Server IP Address: `202.4.117.56`
- Port: `7660`
- Device ID: `1`
- Encryption Key: must match `HikvisionIsup:IsupKey` on the server

The server must be reachable from the terminal on the CMS port **and** on the alarm port,
because the device dials both.

## Architecture

```text
DS-K1T321EX
   |
   |-- ISUP 5.0 registration ---> TCP 7660  CMS listener   (HCISUPCMS)
   |                                          |
   |                                          +-- answers the ISUP key challenge
   |                                          +-- installs the session key on both modules
   |                                          +-- tells the device where the alarm server is
   |
   +-- access events ----------> TCP 7663  alarm listener (HCISUPAlarm)
                                              |
                                              v
                                     ingest queue (bounded)
                                              |
                                              v
                                   parser -> store -> HRMS webhook
                                              |
                                              v
                                   ASP.NET Core API :5080
```

Two separate listeners, two separate ports. **Registration alone produces no attendance
data**: the alarm channel is what carries events, and the device only uses it if the
registration response gave it an alarm server address.

## Requirements

1. Windows Server or Windows 10/11 x64 (Linux x64 also ships in the SDK package).
2. .NET 9 SDK/runtime.
3. The `QuickNV.HikvisionISUPSDK` package, which carries the HCISUP native binaries under
   `runtimes/{rid}/native`, or your licensed official SDK files in `native/`.
4. Inbound TCP 7660 and 7663 through the firewall/NAT.

The package wraps HCISUPCMS and HCISUPStream. The alarm module is not wrapped, so
[`Interop/HikvisionAlarmNative.cs`](Interop/HikvisionAlarmNative.cs) declares the
`NET_EALARM_*` entry points and structs directly against `HCISUPAlarm.dll`, following
`HCISUPAlarm.h` from the vendor SDK.

## Configuration

`appsettings.json`, overridable per environment or by environment variable.

### `HikvisionIsup`

| Key | Default | Notes |
| --- | --- | --- |
| `ListenIp` / `ListenPort` | `0.0.0.0` / `7660` | CMS registration listener. |
| `IsupKey` | *(empty)* | ISUP 5.0 key. Must match the device. **Credential — do not commit.** |
| `ExpectedDeviceId` | `1` | Logs a warning when another device registers. |
| `KeepAliveSeconds`, `TimeoutCount`, `NtpIntervalSeconds` | `15`, `6`, `3600` | Returned to the device at registration. |

### `HikvisionIsup:Alarm`

| Key | Default | Notes |
| --- | --- | --- |
| `Enabled` | `true` | Disabling it stops all event capture. |
| `ListenIp` / `ListenPort` | `0.0.0.0` / `7663` | Local bind for the alarm listener. |
| `UdpPort` | `7662` | Advertised to ISUP 4.0 devices. |
| `Protocol` | `Mqtt` | `Mqtt` for ISUP 5.0, `Udp`/`Tcp` for ISUP 4.0. MQTT reuses the TCP port. |
| `AdvertisedIp` | *(empty)* | **Set this behind NAT.** The address the device dials; empty falls back to `ListenIp`. |
| `AdvertisedPort` / `AdvertisedUdpPort` | `0` | `0` uses the listen ports. |
| `KeepAliveSeconds`, `TimeoutCount` | `30`, `3` | Alarm channel heartbeat. |
| `NativeLogLevel` | `0` | `1`-`3` writes the vendor SDK log to `NativeLogDirectory`. |

`AdvertisedIp` is the setting that most often explains "device is online but no events
arrive": the device is told to send events to `0.0.0.0` and has nowhere to go.

### `HikvisionIsup:PictureServer`

Disabled by default. Event photos over this route need an HCISUPSS storage server, which this
project does not host — see [Limitations](#limitations).

### `Attendance`

| Key | Default | Notes |
| --- | --- | --- |
| `DataDirectory` | `data/attendance` | Append-only JSONL journal, one file per local date. |
| `PhotoDirectory` | `data/attendance/photos` | Inline event photos, one folder per local date. |
| `TimeZone` | *(host zone)* | Buckets local days and interprets device timestamps that arrive without an offset. |
| `MaxInMemoryEvents` | `20000` | Query window held in memory. |
| `LoadDays` | `7` | Journal days reloaded at startup. |
| `RetainRawPayload` | `true` | Keeps the verbatim device message on each record. |
| `EventCatalogPath` | `config/acs-event-types.json` | ISAPI major/minor code table. |
| `Hrms:WebhookUrl` | *(empty)* | POST target for each accepted event. Empty disables forwarding. |
| `Hrms:AuthorizationHeader` | *(empty)* | **Credential — do not commit.** |
| `Backfill:OnDeviceOnline` | `false` | Pull buffered events whenever a device registers. |
| `Backfill:LookbackHours` | `24` | Window for an automatic backfill. |

Supply credentials through the environment rather than a committed file:

```powershell
$env:HikvisionIsup__IsupKey = "<device encryption key>"
$env:Attendance__Hrms__WebhookUrl = "https://hrms.internal/api/attendance"
$env:Attendance__Hrms__AuthorizationHeader = "Bearer <token>"
```

## Attendance record

Each event is normalised to:

```text
Id                 deterministic, derived from DeviceId + device event serial
DeviceId           DeviceSerial
EmployeeId         from employeeNoString / employeeNo
PersonName         CardNo
EventTime          device time, normalised to an offset
DeviceReportedTime verbatim device string
ReceivedAt
MajorEventType     SubEventType     EventName    (e.g. faceVerifyPass)
Outcome            Granted | Denied | Info
AttendanceStatus   checkIn / checkOut / breakIn / breakOut / overtimeIn / overtimeOut
Direction          In | Out | Unknown
VerificationMode   DoorNo   ReaderNo
DeviceEventSerial  PictureUrl   Photos[]
Source             IsupIsapiAlarm | IsupAcsAlarm | IsapiBackfill
RawPayload
```

`Id` is a hash of the device id and the device event serial, so the *same* punch arriving
live and again through a backfill collapses onto one record. That is what makes backfills
safe to re-run.

### Event names

`config/acs-event-types.json` maps ISAPI `major`/`minor` codes to a name and an outcome
(`granted` / `denied` / `info`). Unknown codes are not dropped — they surface as
`major/minor` with outcome `info`, and the raw payload is retained. Add entries from the
Hikvision ISAPI documentation as you encounter them; the file is read at startup, no rebuild
required.

## API

Base URL `http://<host>:5080`. Swagger UI at `/swagger`.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/` | Service, CMS and alarm listener status. |
| `GET` | `/api/hikvision/devices` | Registered devices and online state. |
| `GET` | `/api/attendance/events` | Query events. |
| `GET` | `/api/attendance/events/{id}` | One event, including its raw payload. |
| `GET` | `/api/attendance/events/{id}/photo?index=0` | Inline event photo, when one was captured. |
| `GET` | `/api/attendance/summary/daily` | First/last punch per person per local day. |
| `GET` | `/api/attendance/stats` | Ingest counters and alarm listener health. |
| `POST` | `/api/attendance/backfill` | Replay events the device buffered. |

`GET /api/attendance/events` accepts `deviceId`, `employeeId`, `from`, `to`, `outcome`,
`attendanceStatus`, `skip`, `take`.

`GET /api/attendance/summary/daily` accepts `from`, `to` (dates), `employeeId`.

```bash
curl "http://localhost:5080/api/attendance/events?employeeId=1042&outcome=Granted&take=50"
curl "http://localhost:5080/api/attendance/summary/daily?from=2026-08-01&to=2026-08-19"
curl -X POST http://localhost:5080/api/attendance/backfill \
  -H "Content-Type: application/json" \
  -d '{"deviceId":"1","from":"2026-08-19T00:00:00+06:00","to":"2026-08-19T23:59:59+06:00"}'
```

### Backfill

The alarm channel is push-only, so events recorded while the server was down are invisible to
it. The terminal keeps its own event log, and backfill replays it over an ISAPI pass-through
tunnelled through the registration the device already holds open. De-duplication is by device
event serial, so re-running a range costs only the requests.

Backfill needs the device to be registered and online.

## Run

```powershell
dotnet restore
dotnet run
```

Expected startup log:

```text
Loaded 458 ISAPI event codes from ...
ISUP alarm listener started on 0.0.0.0:7663 using Mqtt.
NET_ECMS_Init completed.
Hikvision ISUP CMS listener started on 0.0.0.0:7660.
```

Then, when a device registers:

```text
ISUP device registration: DeviceId=1, Serial=..., Protocol=5, LoginId=...
Advertising alarm server 202.4.117.56 (tcp/mqtt 7663, udp 7662) as type Mqtt.
Hikvision device ONLINE: 1
```

And on each punch:

```text
Attendance faceVerifyPass (Granted) device=1 employee=1042 at 2026-08-19T09:12:33.0000000+06:00
```

## Firewall

```powershell
New-NetFirewallRule -DisplayName "Hikvision ISUP CMS 7660"   -Direction Inbound -Protocol TCP -LocalPort 7660 -Action Allow
New-NetFirewallRule -DisplayName "Hikvision ISUP Alarm 7663" -Direction Inbound -Protocol TCP -LocalPort 7663 -Action Allow
```

Behind a router, forward both:

```text
Public TCP 7660 -> Internal 7660
Public TCP 7663 -> Internal 7663
```

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Device stuck at "registering" | `HikvisionIsup:IsupKey` missing or does not match the device. The log says so explicitly. |
| Device online, no events | `Alarm:AdvertisedIp` still `0.0.0.0`, or TCP 7663 not forwarded. |
| Events arrive but do not decrypt | Session key did not reach the alarm module; check for the warning at registration. |
| `alarmListenerStatus` reports a start failure | Port already bound, or `HCISUPAlarm.dll` and its dependencies missing. |
| Events land with names like `5/60000` | Code absent from `config/acs-event-types.json`; add it. |
| `queueDrops` climbing in `/api/attendance/stats` | Downstream is slower than the device; raise `Attendance:QueueCapacity`. |

Set `HikvisionIsup:Alarm:NativeLogLevel` to `3` to get the vendor SDK log alongside the
application log.

## Limitations

- **Event photos.** Photos delivered inline on the ISAPI alarm channel are stored and served.
  Photos uploaded to a *picture storage server* are not: that is an HCISUPSS listener this
  project does not host, and the SDK wrapper in use does not bind `NET_ESS_*`. Records still
  carry the device-reported `PictureUrl`.
- **Storage.** The JSONL journal plus in-memory index is a local record for restart recovery,
  replay and audit — it is not a database and does not scale to years of history. Keep the
  HRMS as the system of record; use the webhook or the query API to feed it.
- **`Direction`** is derived from the terminal's `attendanceStatus`. Terminals not configured
  for attendance mode report no status, and every punch comes through as `Unknown`. The daily
  summary falls back to first/last punch in that case, which is a span, not worked hours.
- **Interop.** The alarm struct layouts are transcribed from the vendor header. They are
  exercised end-to-end here (init, listen, callback), but a future SDK revision that changes
  a struct will require updating `Interop/HikvisionAlarmNative.cs` in step.

## Security

- The device encryption key and any HRMS token are credentials. Keep them out of source
  control; supply them via environment variables or user-secrets. If the key in the original
  device screenshot is a production key, rotate it.
- `data/` and `logs/` hold personal data (employee ids, names, card numbers, face photos).
  They are git-ignored; apply the same retention and access controls as the HRMS.
- Generated code and normalised records are a draft: review, test and reconcile against the
  device before treating attendance output as authoritative.
