# CamScope CCTV Sample Video Assets Manifest

This directory contains bundled 720p H.264 offline CCTV footage clips used for CamScope's Realistic Demo Mode.

## Specifications
- **Container**: MPEG-4 Part 14 (`.mp4`)
- **Video Codec**: H.264 / MPEG-4 AVC (`libx264`, High Profile 3.1)
- **Resolution**: 1280 x 720 (720p)
- **Framerate**: 25.0 fps progressive
- **Bitrate**: ~1400 - 1500 kbps (VBR/ABR)
- **Pixel Format**: YUV420p
- **FastStart**: `+faststart` (moov atom placed at front for instant streaming/seeking)
- **Audio**: None (silent surveillance feeds)
- **Licensing / Provenance**: Offline synthetic photorealistic CCTV simulations generated locally for CamScope demo mode. Contains no personally identifiable real-world individuals or copyrighted surveillance footage.

## Video Asset Index

| Filename | Endpoint ID | Target Camera Name | Scene / Angle | Key Visual Elements |
| :--- | :--- | :--- | :--- | :--- |
| `cctv_front_entrance.mp4` | `urn:uuid:demo-axis-p3245-v-00408c123456` | [DEMO] Front Entrance Dome | Commercial glass entrance vestibule | Walking pedestrian crossing doorway, ambient daylight, subtle lens noise |
| `cctv_loading_dock.mp4` | `urn:uuid:demo-hanwha-xnp6400rw-00166c654321` | [DEMO] Loading Dock PTZ | Industrial warehouse loading bay | Industrial forklift backing into dock with flashing orange beacon, concrete textures |
| `cctv_server_room.mp4` | `urn:uuid:demo-hikvision-ds2cd2143g2-40b4cd112233` | [DEMO] Server Room Fixed | Datacenter server rack corridor | Raised flooring, high-intensity server LED status blinks (rack green/orange), cooling airflow vibration |
| `cctv_perimeter_fence_thermal.mp4` | `urn:uuid:demo-bosch-mic-9000i-00075f998877` | [DEMO] Perimeter Fence Thermal | High-contrast FLIR thermal perimeter fence | Monochromatic thermal heat signature, wire mesh fence, walking white-hot intruder signature, IR sensor noise |
| `cctv_parking_lot.mp4` | `urn:uuid:demo-dahua-ipc-hfw5842e-3c52a1334455` | [DEMO] Parking Lot 4K Bullet | Outdoor commercial parking facility | Asphalt lines, evening twilight, sedan pulling slowly across parking bay, headlights flare |
| `cctv_reception_lobby.mp4` | `urn:uuid:demo-sony-snc-vb770-001a2b3c4d5e` | [DEMO] Corporate Reception Lobby | Executive headquarters reception lobby | Polished granite flooring, corporate check-in desk, visitor movement |

## Playback Pipeline
In Demo Mode, `DemoVideoPlaybackService` streams these local video files using FFmpeg's piped BMP protocol (`-re -stream_loop -1 -i <file> -f image2pipe -vcodec bmp -`), rendering genuine decoded video frames directly into Avalonia `WriteableBitmap` instances at smooth 25 fps without opening any external network sockets.
