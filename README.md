# MFD Customizer

Display custom videos, images, and live streams on the Multi-Function Displays (MFDs) of your Nuclear Option cockpit. Every aircraft has its MFD regions pre-mapped, and each physical MFD screen can hold a different media source.

![MFD Customizer banner](docs/banner.png)

## Features

- **Per-aircraft slot layouts** — Ifrit, Brawler, Revoker, Vortex, Compass, Cricket, Darkreach, Medusa, Tarantula, Chicane, Ibis, and Alkyon all have individual MFD regions mapped
- **Multiple slots per cockpit** — each physical MFD shows its own media source independently
- **Media formats** — videos (`.mp4`, `.webm`, `.avi`, `.mov`) and images (`.png`, `.jpg`, `.jpeg`)
- **URL playback** — paste a YouTube / Twitch URL, the plugin resolves via bundled yt-dlp and plays it back
- **Live streams** — Twitch live streams captured in 30-second segments via bundled ffmpeg
- **In-game edit mode** — tweak slot position/size live with arrow keys, values persist to config
- **Rotation support** — rotate content 0/90/180/270 for MFDs whose UV mapping renders sideways

## Installation

1. Install [BepInEx 5 (x64)](https://github.com/BepInEx/BepInEx/releases) into your Nuclear Option folder
2. Run the game once so BepInEx creates its folders
3. Drop the release `MFDCustomizer/` folder into `BepInEx/plugins/`
4. Place your media files (videos, images) into `BepInEx/plugins/MFDCustomizer/`
5. Launch the game

Your plugins folder should look like:

```
BepInEx/plugins/MFDCustomizer/
├── MFDCustomizer.dll
├── yt-dlp.exe          (optional, for URL playback)
├── ffmpeg.exe          (optional, for live streams)
├── my_video.mp4
├── strategic_map.png
└── ...
```

## Controls

| Key | Action |
|-----|--------|
| **F10** | Open/close slot menu — always works regardless of current state |
| **1-9** | (In slot menu) Pick source for slot N |
| **Shift + 1-9** | (In slot menu) Stop slot N |
| **S** | (In slot menu) Stop all active slots |
| **0** | (In source picker) Enter URL |
| **Esc** | Cancel current action |
| **F12** / **Keypad \*** | Toggle edit mode for selected slot |
| **Tab** | (In edit mode) Cycle between active slots |
| **Arrows** | (In edit mode) Move slot |
| **+/-** | (In edit mode) Resize width |
| **PgUp/PgDn** | (In edit mode) Resize height |

## Usage

### Basic video playback

1. Enter cockpit in any supported aircraft
2. Press **F10** → slot list appears with the aircraft's MFD slots
3. Press **1** to choose a source for slot 1 → media list appears
4. Press **1-9** to pick a media file → it plays on the first physical MFD

### URL / YouTube / Twitch playback

1. Open slot menu (F10) → pick a slot → press **0** (URL input)
2. Paste URL, press **Enter**
3. Wait for yt-dlp to resolve (~5-15s first time due to Windows Defender scan)
4. For live streams: ffmpeg captures 30s segments and loops them

### Fine-tuning a slot position

1. Start a slot so it's active
2. Press **F12** (or Keypad `*`) → edit mode
3. Arrow keys move, `+`/`-` resize width, PageUp/Down resize height
4. Values save to `BepInEx/config/com.noms.mfdcustomizer.cfg`

## Measuring a new aircraft

Every aircraft's MFD regions map to different pixel areas on a single 1024×512 canvas. Use the bundled measurement tool:

```
python measure_gui.py
```

or double-click `measure.bat`. Drag rectangles over each physical MFD in your canvas dump (take with F9 was… now you can measure from an external dump), name them, and copy the generated C# dictionary / config snippet into the plugin.

See `measure_gui.py` for the tool source.

## Supported aircraft

| Aircraft | Slots |
|----------|-------|
| KR-67 Ifrit | Screen1_Main, Screen2_Engines, Screen3_Menu, Screen4_Attitude |
| A-19 Brawler | main, panel, AoA |
| FS-12 Revoker | main, AoA, panel |
| FS-20 Vortex | main, AoA, panel |
| TA-30 Compass | main, panel, AoA |
| CI-22 Cricket | main, engine, AoA, engine_2 |
| SFB-81 Darkreach | main, engine_1, pylon, panel, AoA, engine2 |
| EW-25 Medusa | main, engine, panel, engine2, AoA, engine_L, engine_R |
| VL-49 Tarantula | main, engine, engine1, panel |
| SAH-46 Chicane | main, panel, engine, AoA |
| UH-90 Ibis | main, AoA, panel, engine |
| Alkyon AB-4 | Screen1 (placeholder — to be measured) |

## Building from source

Requires the .NET 4.7.2 SDK.

```
dotnet build -c Release
```

Outputs `bin/Release/net472/MFDCustomizer.dll`. Copy it to `BepInEx/plugins/MFDCustomizer/`.

The `.csproj` hint paths assume Nuclear Option is installed at the default Steam path — edit them if yours differs.

## Credits

- Requested by the Nuclear Option community (Matthew, PALA, shimp and others on Discord)
- Built with [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyLib](https://github.com/pardeike/Harmony)
- Uses [yt-dlp](https://github.com/yt-dlp/yt-dlp) and [ffmpeg](https://ffmpeg.org/) for URL/live playback

## License

MIT.
