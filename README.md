# CamScope — Cross-Platform Camera Discovery & Management Platform

> **Discover. Connect. Monitor.**

CamScope is a cross-platform desktop application designed for security operators, system administrators, and network engineers. Built with C# and **.NET 10 LTS** using **Avalonia UI**, CamScope provides local network surveillance discovery, inventory management, secure credential handling, multi-camera live monitoring, recording timeline playback, and activity audit logging.

---

## Technical Limitation & Scope Disclosure

> [!IMPORTANT]
> **Network discovery cannot find every physical camera nearby.**
> Network discovery via ONVIF WS-Discovery relies on UDP multicast (`239.255.255.250:3702`). Discovery is strictly bounded by:
> - The selected local network interface and subnet boundaries.
> - Network switches and router multicast routing configurations (IGMP snooping).
> - Host and local network firewall rules.
> - Device protocol support (ONVIF WS-Discovery enabled).
> - Access permissions and network segmentation (VLANs).
>
> **CamScope is not a universal covert surveillance detector.** It is a network-connected camera management platform for authorized environments.

---

## Key Features

- **Local Network Discovery (ONVIF WS-Discovery)**
  - Enumerates physical and wireless network interfaces with loopback filtering by default (plus optional diagnostic toggle).
  - Constrains multicast outbound traffic strictly to the authorized interface via `SocketOptionName.MulticastInterface`.
  - Automatically calculates subnet directed broadcast fallback when multicast group joins are restricted by local networks or switches.
  - Requires explicit user authorization confirmation before initiating discovery.
  - Broadcasts standard ONVIF WS-Discovery Probe messages (`dn:NetworkVideoTransmitter`).
  - Differentiates bind, transmission, and response errors—fatal socket transmission exceptions are reported accurately rather than swallowed.
  - Strict XML security parser protecting against XML External Entity (XXE) and XML bomb attacks.
  - Real-time response parsing with duplicate device merging.
  - Instant discovery cancellation.

- **Explicit Authentication & Secure Credential Handling**
  - Discovered cameras are clearly distinguished from Authenticated cameras.
  - Explicit user-entered connection flow.
  - Zero hardcoded passwords, zero default-password brute-forcing, and zero automatic logins.
  - Integrated with **macOS Keychain** (`security` service) with encrypted in-memory fallback.
  - SQLite database persists device metadata, capabilities, and tags—**passwords are strictly excluded**.
  - Structured log output automatically redacts passwords, tokens, and RTSP secrets (`***REDACTED***`).

- **Camera Inventory Management**
  - Interactive table and card views with sorting and real-time filtering.
  - Filter by search query, manufacturer, or connection/authentication status.
  - Manual device entry for cameras with known IP and custom ONVIF service paths.
  - Actions column with direct `Auth` and `Del` commands.
  - Export inventory reports to CSV or JSON with sensitive credentials omitted.

- **Real RTSP Multi-Camera Live Monitoring**
  - **Real Device Mode**: Uses cross-platform **FFmpeg media pipeline integration** (`-rtsp_transport tcp -i <uri> -f image2pipe -vcodec bmp -`) to demux and decode live H.264/H.265/MJPEG RTSP video feeds from cameras directly into continuous frame streams.
  - **Secure Credentials**: Credentials resolved automatically from the secure credential store (`ICredentialStore` / macOS Keychain) and injected into the RTSP session without exposing secrets in logs.
  - **Zero Silent Fallback**: Real devices never display synthetic frames; if an RTSP URI is missing, FFmpeg is absent, or connection fails, the cell enters `StreamState.Error` with descriptive diagnostics.
  - **Disciplined Lifecycle & Concurrency**: Session concurrency synchronization (`SemaphoreSlim`), linked cancellation tokens, non-recursive iterative reconnection loops with bounded backoff (1s–30s), and guaranteed task completion before resource disposal.
  - **UI Frame Coalescing**: Throttles and coalesces incoming frames to prevent Avalonia UI dispatcher queue backlog under high-framerate streams.
  - **Responsive Matrix**: Scales to 1x1 Single, 2x2 Quad, and 3x3 Matrix grid viewport.

- **Recordings & Playback Timeline**
  - Search historical recording segments through camera ONVIF recording search APIs.
  - Interactive playback timeline bar with playhead scrubber, play, pause, and time readout.
  - **Honest capability handling**: clearly informs the user when a camera does not expose the ONVIF Recording Search service.

- **Evaluation & Demonstration Mode**
  - Toggleable Demo Mode for evaluating the full UI workflow without physical cameras.
  - Generates realistic synthetic devices (Axis, Hanwha, Hikvision, Bosch, Dahua) clearly marked with `[DEMO]`.
  - Produces pure C# synthetic test frames strictly when `device.IsDemoDevice == true`.
  - Demo streaming is strictly isolated from real-device execution.

- **Network Explorer & App-Scoped Interface Binding**
  - **Primary Internet Protection**: CamScope binds discovery and streaming strictly at the application socket level (`Socket.Bind` to interface IP and `SocketOptionName.MulticastInterface`). Your Mac's default gateway, system routes, DNS, and primary internet connectivity remain 100% untouched and unmodified.
  - **Multi-Adapter Inspection**: Identifies physical hardware ports ("Wi-Fi", "Thunderbolt 1", "USB Ethernet Adapter") via macOS system utilities and maps them to BSD device names (`en0`, `en3`, etc.).
  - **Nearby Wi-Fi Telemetry**: Reads real Wi-Fi environment telemetry (SSID, RSSI dBm, signal percentage, channel, band, security protocol, PHY mode) via supported macOS diagnostic facilities.
  - **OS-Level Wi-Fi Disclosure**: Explicitly discloses that switching Wi-Fi SSIDs is an operating system action affecting the entire computer. CamScope never silently alters system Wi-Fi connections and provides a modal with quick access to macOS System Settings.
  - **Directed Subnet Scanner (CIDR Sweep)**: Sweeps IPv4 subnets (e.g. `192.168.1.0/24`, up to 512 hosts) via bounded concurrency unicast UDP probes on port 3702, enabling camera discovery across routed subnets, VLANs, and environments where multicast WS-Discovery is blocked.
  - **Multi-Point Reachability Diagnostics**: Analyzes selected interface link state, loopback status, multicast readiness, and gateway availability with actionable operator recommendations.
  - **Routing Invariant Verification**: Captures before-and-after routing snapshots to cryptographically and structurally verify that no system routes or gateways were altered during scans.

- **Passive Wi-Fi Vulnerability Assessment**
  - **Zero-Disruption Passive Beacon Capture**: Scans and evaluates nearby 802.11 wireless networks strictly from broadcast beacon Information Elements without connecting, sending association/deauthentication frames, or injecting packets.
  - **Full Observable Telemetry**: Captures SSID, BSSID (MAC), RSSI dBm, signal quality %, channel, frequency band (2.4 GHz, 5 GHz, 6 GHz), PHY mode (802.11a/b/g/n/ac/ax/be), and security standards.
  - **Verifiable Weakness Detection**: Passive findings engine flags unencrypted Open networks (High severity), deprecated WEP (Critical severity), legacy WPA1/TKIP (High severity), and WPS advertisement (Medium severity).
  - **Duplicate SSID & Rogue AP Analysis**: Discovers multiple access points sharing identical SSIDs and analyzes security parity; legitimate multi-AP mesh roaming is distinguished from conflicting security configurations (potential rogue AP / evil twin indicator) without asserting definitive malice.
  - **Honest Observability & PMF Classification**: Accurately distinguishes observable WPA3 mandatory PMF (Protected Management Frames / 802.11w) from unobservable WPA2 PMF settings, honestly marking ambiguous parameters as `Unknown / Requires Router Verification`.
  - **Authorized Audit Checklist Workflow**: Operator-assisted defensive audit mode with explicit authorization confirmation, guiding verification of router admin credentials, WPS deactivation, PMF enforcement, VLAN network segmentation, and firmware patch currency.
  - **Zero-Retention Password Strength Tool**: Evaluates candidate Wi-Fi passphrases in volatile memory with information entropy calculation, pattern penalties, and offline GPU/CPU crack time estimation. Memory is immediately purged upon modal dismissal without persisting or logging passwords.
  - **Sanitized Export & Scan History**: Privacy-conscious JSON and CSV export formats, persistent SQLite scan history, and spectrum congestion analytics for 2.4 GHz non-standard channel overlap.

- **Security & Activity Audit Logging**
  - Immutable audit trail recording discovery sessions, authentication attempts, manual additions, Wi-Fi security assessments, audit checklist saves, and exports.
  - Export audit logs to CSV or clear audit history.

---

## Architecture Overview

CamScope follows Clean Architecture principles with dependency injection and modular boundaries:

```
┌────────────────────────────────────────────────────────┐
│                   CamScope.App                         │
│   Avalonia 11 UI, MVVM, Themes, Views & ViewModels     │
└──────────────┬──────────────────────────┬──────────────┘
               │                          │
┌──────────────▼─────────────┐ ┌──────────▼──────────────┐
│    CamScope.Application    │ │     CamScope.Streaming   │
│ UseCases, Services, DTOs,  │ │ RTSP Client, Stream FSM,│
│ Validation, Audit Service  │ │ Reconnect Backoff, Canvas│
└──────┬──────────────┬──────┘ └──────────┬──────────────┘
       │              │                   │
┌──────▼──────┐ ┌─────▼───────────────────▼──────────────┐
│CamScope.Core│ │         CamScope.Discovery             │
│Domain Models│ │ ONVIF WS-Discovery (UDP Multicast 3702)│
│Interfaces   │ │ Interface Binding, Safe XML, Dedup     │
│Enums, Events│ └─────┬───────────────────┬──────────────┘
       │              │                   │
┌──────┴─────┐  ┌─────▼───────────────┐ ┌─▼──────────────┐
│  Infrastructure│Onvif SOAP Services,│ │CamScope.Persist│
│OS Keychain,│  │Capabilities, Auth   │ │SQLite Storage, │
│Net Provider│  └─────────────────────┘ │Audit, Settings │
└────────────┘                          └────────────────┘
```

| Project | Responsibility |
| :--- | :--- |
| `CamScope.Core` | Domain entities (`CameraDevice`), enums (`DeviceConnectionState`, `AuthenticationState`), and interfaces. |
| `CamScope.Application` | Use cases, device management, credential redaction, demo data generator. |
| `CamScope.Discovery` | ONVIF WS-Discovery UDP socket engine, safe XML parser, duplicate device deduplicator. |
| `CamScope.CameraProtocols` | ONVIF SOAP client (`GetDeviceInformation`, `GetCapabilities`, `GetStreamUri`, `FindRecordings`), WS-Security UsernameToken digest. |
| `CamScope.Streaming` | Stream lifecycle state machine, bounded backoff reconnection, pure C# synthetic test frame generator. |
| `CamScope.Infrastructure` | Network interface enumeration, macOS Keychain credential store, redacting logger. |
| `CamScope.Persistence` | SQLite repository, schema migrations, audit log service, settings service. |
| `CamScope.App` | Avalonia 11 desktop application, dark charcoal/electric cyan theme, 10 view navigation shell. |
| `CamScope.UnitTests` | Unit tests for XML parsing, XXE rejection, deduplication, redaction, security headers, backoff. |
| `CamScope.IntegrationTests` | Integration tests for SQLite persistence, Demo mode isolation, and streaming session lifecycle. |

---

## Supported Platforms

| Operating System | Architecture | Status |
| :--- | :--- | :--- |
| **macOS** | Apple Silicon (`osx-arm64`) | Supported & Verified |
| **macOS** | Intel (`osx-x64`) | Supported |
| **Windows** | Windows 10/11 (`win-x64`) | Supported |
| **Linux** | Ubuntu 22.04+ / Debian (`linux-x64`) | Supported |

---

## Prerequisites

- [.NET 10 LTS SDK](https://dotnet.microsoft.com/download) (or .NET 8 / 9 supported SDK).

---

## How to Build & Run on macOS

### 1. Clone & Build
```bash
git clone https://github.com/shatru123/CamScope.git
cd CamScope

# Build the entire solution
dotnet build CamScope.slnx
```

### 2. Run All Automated Tests
```bash
dotnet test CamScope.slnx --verbosity normal
```

### 3. Launch the Application
```bash
dotnet run --project src/CamScope.App/CamScope.App.csproj
```

---

## How to Use CamScope

### Evaluating in Demo Mode
1. In the top toolbar, check the **Demo Mode** box.
2. Navigate to **Discover Devices**, select an interface, check authorization, and click **Start WS-Discovery**.
3. Watch the simulated camera discovery progress. Click **Add Discovered to Inventory**.
4. Navigate to **Live Monitoring**, click **Start All Streams**, and view simulated 1080p feeds with live timestamps and motion target indicators.
5. Navigate to **Recordings**, select a camera, pick a date, and inspect the recorded segments timeline.

### Real Network Discovery
1. In the top toolbar, ensure **Demo Mode** is unchecked.
2. Navigate to **Discover Devices**.
3. Select your local network adapter (e.g., `en0` Wi-Fi or Ethernet).
4. Inspect the adapter IP and subnet.
5. Check the required authorization box:
   `"I confirm that I am explicitly authorized to discover surveillance devices on this network."`
6. Click **Start WS-Discovery**.
7. CamScope will send UDP multicast probes to `239.255.255.250:3702`, safely parse incoming ProbeMatch responses, deduplicate endpoints, and display the discovered devices in real time.
8. Click **Add Discovered to Inventory** to save the discovered devices to your local SQLite database.

### Authenticating a Camera
1. Navigate to **Camera Inventory**.
2. Select a device and click **Auth**.
3. Enter the device username and password.
4. Click **Connect & Authenticate**.
5. CamScope contacts the ONVIF Device Management service, verifies credentials using WS-Security UsernameToken password digest, queries camera capabilities, and securely stores credentials in the macOS Keychain.

---

## Security & Privacy Model

- **No Plaintext Passwords**: Passwords are saved only to the operating system's secure credential store (macOS Keychain) and are never stored in the SQLite database or configuration files.
- **Redaction of Secrets**: All logging infrastructure passes through `SecretRedactor`, masking passwords, authentication headers, and RTSP credentials before output.
- **XXE & XML Bomb Protection**: The XML parser prohibits DTD processing (`DtdProcessing.Prohibit`) and enforces a 1MB character cap to prevent denial-of-service via expansion attacks.
- **Strict Endpoint Validation**: Only `http://` and `https://` schemes are accepted for camera service endpoints, preventing SSRF and scheme hijacking.
- **Local Privacy**: Discovery results and telemetry are stored exclusively on the user's local machine. No cloud telemetry or feed uploading occurs.

---

## License

This project is licensed under the MIT License.
