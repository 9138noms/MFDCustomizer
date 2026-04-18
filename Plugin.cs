using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

namespace MFDCustomizer
{
    [BepInPlugin("com.noms.mfdcustomizer", "MFD Customizer", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        private static ManualLogSource Log;

        // ---- Global config ----
        private ConfigEntry<KeyCode> menuKey;
        private ConfigEntry<KeyCode> editModeKey;
        private ConfigEntry<KeyCode> editModeKeyAlt;
        private ConfigEntry<KeyCode> slotCycleKey;
        private ConfigEntry<float> volume;
        private ConfigEntry<bool> loopVideo;
        private ConfigEntry<string> ytdlpPath;

        // ---- Per-aircraft / per-slot layout ----
        private class SlotLayout
        {
            public string slotName;
            public ConfigEntry<float> PosX, PosY, Width, Height;
            public ConfigEntry<string> Source;
            public ConfigEntry<bool> Enabled;
            public ConfigEntry<float> Rotation; // 0/90/180/270 — for MFDs whose UV rotates the canvas
        }
        // currentAircraftSection → dict of slotName → SlotLayout
        private Dictionary<string, Dictionary<string, SlotLayout>> layoutCache
            = new Dictionary<string, Dictionary<string, SlotLayout>>();
        private string currentAircraftSection = "Default";

        // Per-(aircraft, slot) rotation defaults. Missing entries default to 0.
        // Add entries here for MFDs whose UV mapping shows content rotated.
        private static readonly Dictionary<string, Dictionary<string, float>> BuiltinRotations =
            new Dictionary<string, Dictionary<string, float>>
        {
            // example: ["Aircraft"] = new Dictionary<string,float> { ["SlotName"] = 90f }
        };

        private static float GetBuiltinRotation(string aircraftSection, string slotName)
        {
            if (BuiltinRotations.TryGetValue(aircraftSection, out var rotMap) &&
                rotMap.TryGetValue(slotName, out var r)) return r;
            return 0f;
        }

        // Built-in defaults per (aircraft, slot). Values measured from F9 MFD dumps.
        // Canvas = 1024x512 for all aircraft. canvas_x = px_center - 512, canvas_y = 256 - py_center.
        // Format: aircraft → (slot → (posX, posY, w, h))
        private static readonly Dictionary<string, Dictionary<string, (float, float, float, float)>> BuiltinLayouts =
            new Dictionary<string, Dictionary<string, (float, float, float, float)>>
        {
            // Fallback when aircraft unknown — single slot over radar area
            ["Default"] = new Dictionary<string, (float, float, float, float)>
            {
                ["Screen1"] = (-287f, 71f, 290f, 370f),
            },

            // Ifrit — 4 physical MFDs, measured from canvas dump by user (px boxes → canvas coords)
            // Slot 4 (1,1~765,380) = Main | Slot 2 (771,4~1020,120) = Engines
            // Slot 1 (793,150~990,265) = Menu | Slot 3 (781,298~1017,494) = Attitude
            ["KR-67_Ifrit"] = new Dictionary<string, (float, float, float, float)>
            {
                ["Screen1_Main"]     = (-129f,   66f, 764f, 459f),  // radar + fuel/throttle/HEAT + weapons
                ["Screen2_Engines"]  = ( 384f,  194f, 249f, 171f),  // engine status (-10 TB)
                ["Screen3_Menu"]     = ( 380f,   48f, 197f, 170f),  // menu buttons (-10 TB)
                ["Screen4_Attitude"] = ( 387f, -140f, 236f, 256f),  // AOA/VSI/horizon ball (-10 TB)
            },

            // A-19 Brawler — measured via measure_gui.py
            ["A-19_Brawler"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]  = (  0f,   76f, 1020f, 360f),
                ["panel"] = (214f, -180f,  253f, 184f),  // expanded +40 LR, +20 TB
                ["AoA"]   = (407f, -181f,  178f, 142f),
            },

            // FS-12 Revoker
            ["FS-12_Revoker"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]  = (  1f,   74f, 1022f, 357f),
                ["AoA"]   = (378f, -182f,  211f, 147f),
                ["panel"] = (  9f, -183f,  118f, 146f),
            },

            // FS-20 Vortex
            ["FS-20_Vortex"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]  = (  0f,   74f, 1020f, 359f),
                ["AoA"]   = (380f, -180f,  184f, 141f),
                ["panel"] = ( 10f, -182f,  115f, 144f),
            },

            // TA-30 Compass
            ["TA-30_Compass"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]  = (  0f,   75f, 1020f, 356f),
                ["panel"] = (214f, -180f,  177f, 148f),
                ["AoA"]   = (406f, -181f,  177f, 142f),
            },

            // CI-22 Cricket
            ["CI-22_Cricket"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]     = ( -76f,    0f, 690f, 508f),  // -56 base, +40 left expansion
                ["engine"]   = ( 411f,  180f, 198f, 151f),
                ["AoA"]      = ( 395f, -164f, 198f, 145f),
                ["engine_2"] = ( 410f,   20f, 201f, 161f),
            },

            // SFB-81 Darkreach
            ["SFB-81_Darkreach"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]     = (-220f,   72f, 585f, 366f),
                ["engine_1"] = ( 188f,   62f, 195f, 345f),
                ["pylon"]    = ( 400f,   86f, 185f, 289f),
                ["panel"]    = ( 443f, -161f, 120f, 158f),
                ["AoA"]      = (-106f, -185f, 224f, 138f),
                ["engine2"]  = (-370f, -186f, 241f, 137f),
            },

            // EW-25 Medusa
            ["EW-25_Medusa"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]     = (-224f,   64f, 569f, 377f),
                ["engine"]   = ( 284f,   62f, 449f, 377f),
                ["panel"]    = (-382f, -192f, 162f, 125f),
                ["engine2"]  = (-139f, -190f, 150f, 123f),
                ["AoA"]      = (   8f, -192f, 132f, 126f),
                ["engine_L"] = ( 207f, -192f, 170f, 123f),
                ["engine_R"] = ( 417f, -192f, 170f, 119f),
            },

            // VL-49 Tarantula
            ["VL-49_Tarantula"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]    = (-256f,   69f,  513f, 372f),
                ["engine"]  = (   1f, -190f, 1020f, 130f),
                ["engine1"] = ( 192f,  113f,  384f, 284f),
                ["panel"]   = ( 450f,  -46f,  121f, 147f),
            },

            // SAH-46 Chicane
            ["SAH-46_Chicane"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]   = (-128f,   66f, 764f, 381f),
                ["panel"]  = ( 133f, -192f, 158f, 123f),
                ["engine"] = ( 384f,  112f, 219f, 285f),
                ["AoA"]    = ( 382f, -142f, 148f, 215f),
            },

            // UH-90 Ibis
            ["UH-90_Ibis"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]   = (-246f,   64f, 533f, 382f),
                ["AoA"]    = ( -66f, -192f, 180f, 125f),
                ["panel"]  = ( 294f, -150f, 433f, 200f),
                ["engine"] = ( 290f,  105f, 431f, 294f),
            },

            // Alkyon AB-4 — measured via measure_gui.py
            // NOTE: main + main1 share the same physical MFD (two canvas regions → one screen)
            // NOTE: engineL + engineR share the same physical engine MFD
            ["Alkyon_AB-4"] = new Dictionary<string, (float, float, float, float)>
            {
                ["main"]     = (-220f,   62f, 584f, 345f),
                ["main1"]    = ( 398f,   91f, 198f, 310f),
                ["engineL"]  = ( 186f, -130f, 207f, 249f),
                ["engineR"]  = ( 187f,  124f, 210f, 251f),
                ["throttle"] = (-369f, -182f, 248f, 137f),
                ["AoA"]      = (-106f, -184f, 231f, 144f),
            },
        };

        // ---- Runtime state ----
        private class ActiveSlot
        {
            public string slotName;
            public string sourcePath;
            public GameObject overlayGO;
            public RawImage rawImg;
            public VideoPlayer videoPlayer;   // non-null if video
            public AudioSource audioSource;
            public RenderTexture videoRT;
            public Texture2D imageTex;         // non-null if image
            public bool isLive;
            public string liveM3u8Url;
        }
        private Dictionary<string, ActiveSlot> activeSlots = new Dictionary<string, ActiveSlot>();

        private bool menuOpen = false;
        private bool editMode = false;
        private string editingSlot = null;
        private bool isSelectingSource = false;
        private string sourceTargetSlot = null;
        private bool isEnteringUrl = false;
        private bool urlFocused = false;
        private string urlInput = "";
        private bool isResolving = false;
        private string resolveStatus = "";

        private List<string> mediaFiles = new List<string>(); // videos + images

        // ---- Reflection ----
        private FieldInfo tacScreenField;
        private FieldInfo canvasField;
        private bool reflectionReady = false;
        private Canvas mfdCanvas;


        // ---- Live stream (one at a time) ----
        private Process ffmpegProcess = null;
        private const int SEGMENT_SECONDS = 30;
        private string[] segmentPaths = new string[2];
        private int playingSegIdx = 0;
        private string liveFfmpegExe = null;
        private volatile string pendingSegmentSwitch = null;
        private volatile bool nextSegmentReady = false;
        private volatile bool isCapturing = false;
        private string liveSlotName = null;

        // Thread → main dispatch for URL resolve
        private class PendingPlay { public string slotName; public string url; public string m3u8; public string title; }
        private volatile PendingPlay pendingUrlPlay;

        // ====================================================================
        void Awake()
        {
            Instance = this;
            Log = Logger;

            menuKey        = Config.Bind("Keys", "Menu",         KeyCode.F10, "Open slot control menu");
            editModeKey    = Config.Bind("Keys", "EditMode",     KeyCode.KeypadMultiply, "Toggle edit mode for selected slot");
            editModeKeyAlt = Config.Bind("Keys", "EditModeAlt",  KeyCode.F12, "Alternate edit-mode key (no numpad)");
            slotCycleKey   = Config.Bind("Keys", "CycleSlot",    KeyCode.Tab, "Cycle which slot edit mode affects");
            volume         = Config.Bind("Playback", "Volume",     0.5f, "Audio volume (0.0-1.0)");
            loopVideo      = Config.Bind("Playback", "Loop",       true, "Loop video playback");
            ytdlpPath      = Config.Bind("Playback", "YtDlpPath",  "",   "yt-dlp.exe path; empty = auto-detect");

            // Pre-bind all known aircraft/slot config so they appear in the .cfg from the start
            foreach (var acKv in BuiltinLayouts)
            {
                EnsureLayoutBinding(acKv.Key);
            }

            ScanMediaFiles();
            SetupReflection();

            SceneManager.sceneLoaded += (s, m) =>
            {
                StopAllSlots(destroyRT: true);
                if (MFDRunner.Instance == null)
                {
                    var go = new GameObject("MFDCustomizerRunner");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.AddComponent<MFDRunner>();
                }
            };

            Log.LogInfo($"MFD Customizer v1.0.0 loaded. {mediaFiles.Count} media file(s). Menu={menuKey.Value}");

            // Warm up yt-dlp / ffmpeg in background so first URL play isn't blocked by Defender scan
            Thread warmup = new Thread(() =>
            {
                try
                {
                    var yt = FindYtDlp();
                    if (yt != null)
                    {
                        var psi = new ProcessStartInfo { FileName = yt, Arguments = "--version", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        var p = Process.Start(psi);
                        string v = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(30000);
                        Log.LogInfo($"yt-dlp warm: v{v}");
                    }
                    else Log.LogWarning("yt-dlp.exe not found in plugin folder — URL playback disabled");

                    var ff = FindFfmpeg();
                    if (ff != null)
                    {
                        var psi = new ProcessStartInfo { FileName = ff, Arguments = "-version", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        var p = Process.Start(psi);
                        string v = p.StandardOutput.ReadLine()?.Trim() ?? "";
                        p.WaitForExit(30000);
                        Log.LogInfo($"ffmpeg warm: {v}");
                    }
                    else Log.LogWarning("ffmpeg.exe not found in plugin folder — live streams disabled");
                }
                catch (Exception e) { Log.LogWarning($"Warmup: {e.Message}"); }
            });
            warmup.IsBackground = true;
            warmup.Start();
        }

        private void SetupReflection()
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                if (asm == null) return;

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var cockpitType = asm.GetType("Cockpit");
                if (cockpitType != null) tacScreenField = cockpitType.GetField("tacScreen", flags);

                var tacScreenType = asm.GetType("TacScreen");
                if (tacScreenType != null)
                {
                    canvasField = tacScreenType.GetField("canvas", flags);
                }

                reflectionReady = tacScreenField != null && canvasField != null;
                Log.LogInfo($"Reflection ready: {reflectionReady}");
            }
            catch (Exception e) { Log.LogError($"Reflection: {e.Message}"); }
        }

        private void ScanMediaFiles()
        {
            mediaFiles.Clear();
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var ext in new[] { "*.mp4", "*.webm", "*.avi", "*.mov", "*.png", "*.jpg", "*.jpeg" })
                mediaFiles.AddRange(Directory.GetFiles(pluginDir, ext).Where(f => !Path.GetFileName(f).StartsWith("mfd_")));
            mediaFiles.Sort();
            foreach (var f in mediaFiles) Log.LogInfo($"  Media: {Path.GetFileName(f)}");
        }

        // ====================================================================
        // Layout management
        // ====================================================================
        private void EnsureLayoutBinding(string aircraftSection)
        {
            if (layoutCache.ContainsKey(aircraftSection)) return;

            var slotDict = new Dictionary<string, SlotLayout>();
            Dictionary<string, (float, float, float, float)> builtin;
            if (!BuiltinLayouts.TryGetValue(aircraftSection, out builtin))
            {
                // Try partial token match
                builtin = null;
                foreach (var kv in BuiltinLayouts)
                {
                    if (kv.Key == "Default") continue;
                    foreach (var p in kv.Key.Split('_'))
                    {
                        if (p.Length >= 3 && aircraftSection.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                        { builtin = kv.Value; break; }
                    }
                    if (builtin != null) break;
                }
                if (builtin == null) builtin = BuiltinLayouts["Default"];
            }

            foreach (var slotKv in builtin)
            {
                var slotName = slotKv.Key;
                var d = slotKv.Value;
                string section = $"Layout.{aircraftSection}.{slotName}";
                var sl = new SlotLayout
                {
                    slotName = slotName,
                    PosX    = Config.Bind(section, "PosX",    d.Item1, "X offset on MFD canvas (0 = center, negative = left)"),
                    PosY    = Config.Bind(section, "PosY",    d.Item2, "Y offset on MFD canvas (0 = center, positive = up)"),
                    Width   = Config.Bind(section, "Width",   d.Item3, "Overlay width"),
                    Height  = Config.Bind(section, "Height",  d.Item4, "Overlay height"),
                    Source  = Config.Bind(section, "Source",  "",      "Media filename (video or image) in plugin folder; empty = no default"),
                    Enabled = Config.Bind(section, "Enabled", true,    "Whether this slot is visible at all"),
                    Rotation = Config.Bind(section, "Rotation", GetBuiltinRotation(aircraftSection, slotName),
                                            "Rotation in degrees (try 0, 90, 180, 270) — use when MFD shows content rotated"),
                };
                slotDict[slotName] = sl;
            }
            layoutCache[aircraftSection] = slotDict;
        }

        private Dictionary<string, SlotLayout> GetSlotsForCurrentAircraft()
        {
            EnsureLayoutBinding(currentAircraftSection);
            return layoutCache[currentAircraftSection];
        }

        // ====================================================================
        // Aircraft detection
        // ====================================================================
        // Find the player's cockpit — only the player's Cockpit has base.enabled == true.
        // Other AI aircraft's Cockpit components exist but are disabled (see Cockpit.Cockpit_OnAircraftInitialize).
        private Component FindPlayerCockpit()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (asm == null) return null;
            var cockpitType = asm.GetType("Cockpit");
            if (cockpitType == null) return null;
            var all = UnityEngine.Object.FindObjectsOfType(cockpitType);
            foreach (var obj in all)
            {
                var beh = obj as Behaviour;
                if (beh != null && beh.isActiveAndEnabled) return beh;
            }
            // Fallback: any (so detection still returns something on very early frames)
            return (all != null && all.Length > 0) ? all[0] as Component : null;
        }

        private string DetectAircraftSection()
        {
            try
            {
                var cockpit = FindPlayerCockpit();
                if (cockpit == null) return "Default";
                var cockpitType = cockpit.GetType();

                string raw = null;
                var aircraftField = cockpitType.GetField("aircraft", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (aircraftField != null)
                {
                    var aircraft = aircraftField.GetValue(cockpit);
                    if (aircraft != null)
                    {
                        var defField = aircraft.GetType().GetField("definition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (defField != null)
                        {
                            var def = defField.GetValue(aircraft);
                            if (def != null)
                            {
                                var codeField = def.GetType().GetField("code", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                var nameField = def.GetType().GetField("unitName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                string code = codeField?.GetValue(def) as string;
                                string name = nameField?.GetValue(def) as string;
                                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(name))
                                {
                                    // If name already contains code prefix, don't double it (e.g. name="CI-22 Cricket", code="CI-22")
                                    if (name.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0) raw = name;
                                    else raw = $"{code}_{name}";
                                }
                                else if (!string.IsNullOrEmpty(code)) raw = code;
                                else if (!string.IsNullOrEmpty(name)) raw = name;
                            }
                        }
                    }
                }

                if (raw == null)
                {
                    var t = cockpit.transform;
                    while (t != null && (raw == null || raw.Equals("cockpit", StringComparison.OrdinalIgnoreCase)))
                    {
                        raw = t.gameObject.name.Replace("(Clone)", "").Trim();
                        t = t.parent;
                    }
                }

                if (string.IsNullOrEmpty(raw)) return "Default";
                var clean = new System.Text.StringBuilder();
                foreach (char c in raw)
                {
                    if (char.IsLetterOrDigit(c) || c == '-' || c == '_') clean.Append(c);
                    else if (c == ' ') clean.Append('_');
                }
                string section = clean.ToString();
                return string.IsNullOrEmpty(section) ? "Default" : section;
            }
            catch (Exception e) { Log.LogWarning($"Aircraft detect: {e.Message}"); return "Default"; }
        }

        private bool AcquireMFDCanvas()
        {
            if (!reflectionReady) return false;
            try
            {
                var cockpit = FindPlayerCockpit();
                if (cockpit == null) return false;
                var tac = tacScreenField.GetValue(cockpit);
                if (tac == null) return false;
                mfdCanvas = canvasField.GetValue(tac) as Canvas;
                return mfdCanvas != null;
            }
            catch (Exception e) { Log.LogError($"AcquireMFDCanvas: {e.Message}"); return false; }
        }

        // ====================================================================
        // Tick: input / edit mode
        // ====================================================================
        internal void Tick()
        {
            if (pendingSegmentSwitch != null) HandleSegmentSwitch();
            if (pendingUrlPlay != null) FlushPendingUrl();

            // MFD dump — any time

            // F10 — always works, cancels any other state
            if (Input.GetKeyDown(menuKey.Value))
            {
                if (menuOpen)
                {
                    // Close menu
                    menuOpen = false;
                }
                else
                {
                    // Cancel any stuck state so F10 always opens a fresh main menu
                    isSelectingSource = false;
                    sourceTargetSlot = null;
                    isEnteringUrl = false;
                    urlInput = "";
                    urlFocused = false;
                    editMode = false;
                    isResolving = false;     // unstick any hung URL resolve
                    resolveStatus = "";
                    menuOpen = true;

                    // Force re-detect aircraft + canvas — prevents stale refs after aircraft change
                    mfdCanvas = null;
                    AcquireMFDCanvas();
                    var newSection = DetectAircraftSection();
                    if (newSection != currentAircraftSection &&
                        !(newSection == "Default" && currentAircraftSection != "Default"))
                    {
                        currentAircraftSection = newSection;
                        Log.LogInfo($"Aircraft section: {currentAircraftSection}");
                    }
                }
            }

            if (menuOpen && !isSelectingSource && !isEnteringUrl)
            {
                HandleMenuInput();
            }
            else if (isSelectingSource)
            {
                HandleSourceMenu();
            }
            else if (isEnteringUrl)
            {
                // Text input captured in OnGUI
            }
            else if (editMode)
            {
                HandleEditMode();
            }

            // Edit mode toggle (requires at least one active slot)
            if (!isSelectingSource && !isEnteringUrl &&
                (Input.GetKeyDown(editModeKey.Value) || Input.GetKeyDown(editModeKeyAlt.Value)))
            {
                if (activeSlots.Count == 0)
                {
                    Log.LogInfo($"EditMode: no active slot. Open menu ({menuKey.Value}) and start a slot first.");
                }
                else
                {
                    editMode = !editMode;
                    menuOpen = false;
                    if (editMode && (editingSlot == null || !activeSlots.ContainsKey(editingSlot)))
                        editingSlot = activeSlots.Keys.First();
                    Log.LogInfo($"EditMode {(editMode ? "ON" : "OFF")} slot={editingSlot}");
                }
            }

            // Slot cycle in edit mode
            if (editMode && Input.GetKeyDown(slotCycleKey.Value) && activeSlots.Count > 0)
            {
                var keys = activeSlots.Keys.ToList();
                int i = keys.IndexOf(editingSlot);
                editingSlot = keys[(i + 1) % keys.Count];
                Log.LogInfo($"Edit slot → {editingSlot}");
            }
        }

        private void HandleMenuInput()
        {
            // Esc = close
            if (Input.GetKeyDown(KeyCode.Escape)) { menuOpen = false; return; }

            // S = stop ALL active slots
            if (Input.GetKeyDown(KeyCode.S))
            {
                int n = activeSlots.Count;
                foreach (var k in activeSlots.Keys.ToList()) StopSlot(k);
                Log.LogInfo($"Stopped all {n} active slot(s)");
                return;
            }

            var slots = GetSlotsForCurrentAircraft();
            var slotKeys = slots.Keys.ToList();

            // Number keys 1-9 → open source picker for slot N (Shift = stop that slot)
            for (int i = 0; i < slotKeys.Count && i < 9; i++)
            {
                var kc = KeyCode.Alpha1 + i;
                if (Input.GetKeyDown(kc))
                {
                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    {
                        StopSlot(slotKeys[i]);
                    }
                    else
                    {
                        sourceTargetSlot = slotKeys[i];
                        isSelectingSource = true;
                        menuOpen = false;
                    }
                    return;
                }
            }
        }

        private void HandleSourceMenu()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                isSelectingSource = false;
                sourceTargetSlot = null;
                menuOpen = true;
                return;
            }
            for (int i = 1; i <= 9 && i <= mediaFiles.Count; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0 + i))
                {
                    string file = mediaFiles[i - 1];
                    PlayMediaOnSlot(sourceTargetSlot, file);
                    isSelectingSource = false;
                    sourceTargetSlot = null;
                    return;
                }
            }
            if (Input.GetKeyDown(KeyCode.Alpha0))
            {
                isSelectingSource = false;
                isEnteringUrl = true;
                urlInput = "";
                urlFocused = false;
            }
        }

        private void HandleEditMode()
        {
            if (editingSlot == null || !activeSlots.ContainsKey(editingSlot)) return;
            var slots = GetSlotsForCurrentAircraft();
            if (!slots.ContainsKey(editingSlot)) return;
            var layout = slots[editingSlot];

            float step = 5f;
            bool changed = false;
            if (Input.GetKey(KeyCode.UpArrow))    { layout.PosY.Value += step; changed = true; }
            if (Input.GetKey(KeyCode.DownArrow))  { layout.PosY.Value -= step; changed = true; }
            if (Input.GetKey(KeyCode.LeftArrow))  { layout.PosX.Value -= step; changed = true; }
            if (Input.GetKey(KeyCode.RightArrow)) { layout.PosX.Value += step; changed = true; }
            if (Input.GetKey(KeyCode.KeypadPlus))  { layout.Width.Value += step; changed = true; }
            if (Input.GetKey(KeyCode.KeypadMinus)) { layout.Width.Value = Mathf.Max(20, layout.Width.Value - step); changed = true; }
            if (Input.GetKey(KeyCode.PageUp))   { layout.Height.Value += step; changed = true; }
            if (Input.GetKey(KeyCode.PageDown)) { layout.Height.Value = Mathf.Max(20, layout.Height.Value - step); changed = true; }

            if (changed && activeSlots.TryGetValue(editingSlot, out var active)) UpdateOverlayRect(active, layout);
        }

        // ====================================================================
        // Playback
        // ====================================================================
        private void PlayMediaOnSlot(string slotName, string mediaPath)
        {
            if (!File.Exists(mediaPath)) { Log.LogError($"File missing: {mediaPath}"); return; }
            if (mfdCanvas == null && !AcquireMFDCanvas()) { Log.LogWarning("MFD canvas not available — sit in cockpit"); return; }

            // Re-detect aircraft, but never downgrade to Default (transient failure guard)
            var sec = DetectAircraftSection();
            if (sec != currentAircraftSection &&
                !(sec == "Default" && currentAircraftSection != "Default"))
            {
                currentAircraftSection = sec;
                var validSlots = GetSlotsForCurrentAircraft().Keys;
                foreach (var aKey in activeSlots.Keys.ToList())
                    if (!validSlots.Contains(aKey)) StopSlot(aKey);
            }

            var slots = GetSlotsForCurrentAircraft();
            if (!slots.ContainsKey(slotName))
            {
                Log.LogError($"Slot '{slotName}' not defined for aircraft '{currentAircraftSection}'");
                return;
            }

            StopSlot(slotName); // clear any existing playback on this slot

            var layout = slots[slotName];
            string ext = Path.GetExtension(mediaPath).ToLowerInvariant();
            bool isImage = ext == ".png" || ext == ".jpg" || ext == ".jpeg";

            var active = new ActiveSlot { slotName = slotName, sourcePath = mediaPath };
            active.overlayGO = new GameObject($"MFDOverlay_{slotName}");
            active.overlayGO.transform.SetParent(mfdCanvas.transform, false);
            active.overlayGO.transform.SetAsLastSibling();
            active.rawImg = active.overlayGO.AddComponent<RawImage>();
            active.rawImg.raycastTarget = false;

            if (isImage)
            {
                var data = File.ReadAllBytes(mediaPath);
                active.imageTex = new Texture2D(2, 2);
                ImageConversion.LoadImage(active.imageTex, data);
                active.rawImg.texture = active.imageTex;
            }
            else
            {
                var go = new GameObject($"MFDVideoHost_{slotName}");
                UnityEngine.Object.DontDestroyOnLoad(go);
                active.videoPlayer = go.AddComponent<VideoPlayer>();
                active.audioSource = go.AddComponent<AudioSource>();

                active.videoRT = new RenderTexture(512, 256, 0);
                active.videoRT.Create();

                active.videoPlayer.source = VideoSource.Url;
                active.videoPlayer.url = "file:///" + mediaPath.Replace('\\', '/');
                active.videoPlayer.isLooping = loopVideo.Value;
                active.videoPlayer.playOnAwake = false;
                active.videoPlayer.renderMode = VideoRenderMode.RenderTexture;
                active.videoPlayer.targetTexture = active.videoRT;
                active.videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
                active.videoPlayer.SetDirectAudioVolume(0, volume.Value);
                active.videoPlayer.errorReceived += (vp, msg) => Log.LogError($"[{slotName}] VideoPlayer: {msg}");
                active.rawImg.texture = active.videoRT;
                active.videoPlayer.Play();
            }

            UpdateOverlayRect(active, layout);
            activeSlots[slotName] = active;
            layout.Source.Value = Path.GetFileName(mediaPath); // remember the last source
            editingSlot = slotName;

            Log.LogInfo($"[{slotName}] playing {Path.GetFileName(mediaPath)} (image={isImage})");
        }

        private void StopSlot(string slotName)
        {
            if (!activeSlots.TryGetValue(slotName, out var active)) return;

            if (liveSlotName == slotName)
            {
                StopFfmpeg();
                CleanupSegmentFiles();
                liveSlotName = null;
            }

            if (active.videoPlayer != null)
            {
                try { active.videoPlayer.Stop(); } catch { }
                UnityEngine.Object.Destroy(active.videoPlayer.gameObject);
            }
            if (active.videoRT != null) { active.videoRT.Release(); UnityEngine.Object.Destroy(active.videoRT); }
            if (active.imageTex != null) UnityEngine.Object.Destroy(active.imageTex);
            if (active.overlayGO != null) UnityEngine.Object.Destroy(active.overlayGO);

            activeSlots.Remove(slotName);
            Log.LogInfo($"[{slotName}] stopped");

            if (editingSlot == slotName) editingSlot = activeSlots.Keys.FirstOrDefault();
        }

        private void StopAllSlots(bool destroyRT)
        {
            foreach (var k in activeSlots.Keys.ToList()) StopSlot(k);
            activeSlots.Clear();
        }

        private void UpdateOverlayRect(ActiveSlot active, SlotLayout layout)
        {
            if (active?.rawImg == null) return;
            var rt = active.rawImg.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(layout.Width.Value, layout.Height.Value);
            rt.anchoredPosition = new Vector2(layout.PosX.Value, layout.PosY.Value);
            rt.localEulerAngles = new Vector3(0f, 0f, layout.Rotation.Value);
            active.overlayGO.SetActive(layout.Enabled.Value);
        }

        // ====================================================================
        // URL flow (live + static)
        // ====================================================================
        internal void StartUrlFlow(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || sourceTargetSlot == null) return;
            string slot = sourceTargetSlot;
            isEnteringUrl = false;

            Thread t = new Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                isResolving = true;
                resolveStatus = "Resolving URL via yt-dlp...";
                Log.LogInfo($"[URL] Resolving: {url}");
                string directUrl = url;
                bool needsStreaming = false;
                try
                {
                    string ytdlpExe = FindYtDlp();
                    if (ytdlpExe != null)
                    {
                        var resolved = ResolveWithYtDlp(ytdlpExe, url);
                        if (resolved != null)
                        {
                            directUrl = resolved;
                            if (directUrl.Contains(".m3u8") || url.Contains("twitch.tv")) needsStreaming = true;
                            Log.LogInfo($"[URL] Resolved in {sw.Elapsed.TotalSeconds:F1}s live={needsStreaming}");
                        }
                        else
                        {
                            Log.LogWarning($"[URL] yt-dlp failed to resolve after {sw.Elapsed.TotalSeconds:F1}s — passing URL directly to VideoPlayer");
                        }
                    }
                    pendingUrlPlay = new PendingPlay
                    {
                        slotName = slot,
                        url = needsStreaming ? null : directUrl,
                        m3u8 = needsStreaming ? directUrl : null,
                        title = url,
                    };
                }
                catch (Exception e)
                {
                    Log.LogError($"[URL] resolve thread: {e.Message}");
                }
                finally
                {
                    isResolving = false;
                    resolveStatus = "";
                }
            });
            t.IsBackground = true; t.Start();
        }

        private void FlushPendingUrl()
        {
            var p = pendingUrlPlay; pendingUrlPlay = null;
            if (p == null) return;

            if (p.m3u8 != null)
            {
                if (liveSlotName != null && liveSlotName != p.slotName)
                {
                    Log.LogWarning("Only one live stream at a time. Stop the other live slot first.");
                    return;
                }
                liveFfmpegExe = FindFfmpeg();
                if (liveFfmpegExe == null) { Log.LogError("ffmpeg not found"); return; }
                liveSlotName = p.slotName;

                Thread t = new Thread(() =>
                {
                    isResolving = true;
                    resolveStatus = "Live stream — capturing 30s segment via ffmpeg...";
                    try
                    {
                        string seg = CaptureSegment(liveFfmpegExe, p.m3u8, 0);
                        if (seg != null)
                            pendingUrlPlay = new PendingPlay { slotName = p.slotName, url = "file:///" + seg.Replace('\\', '/'), title = p.title, m3u8 = null };
                    }
                    catch (Exception e)
                    {
                        Log.LogError($"[URL] live capture thread: {e.Message}");
                    }
                    finally
                    {
                        isResolving = false; resolveStatus = "";
                    }
                });
                t.IsBackground = true; t.Start();
            }
            else if (p.url != null)
            {
                // Play directly as URL — use a temporary target
                PlayUrlOnSlot(p.slotName, p.url, p.title, liveSlotName == p.slotName);
            }
        }

        private void PlayUrlOnSlot(string slotName, string directUrl, string title, bool isLive)
        {
            if (mfdCanvas == null && !AcquireMFDCanvas()) return;
            var slots = GetSlotsForCurrentAircraft();
            if (!slots.ContainsKey(slotName)) { Log.LogError($"Slot '{slotName}' not defined"); return; }

            StopSlot(slotName);
            var layout = slots[slotName];

            var active = new ActiveSlot { slotName = slotName, sourcePath = title, isLive = isLive };
            active.overlayGO = new GameObject($"MFDOverlay_{slotName}");
            active.overlayGO.transform.SetParent(mfdCanvas.transform, false);
            active.overlayGO.transform.SetAsLastSibling();
            active.rawImg = active.overlayGO.AddComponent<RawImage>();
            active.rawImg.raycastTarget = false;

            var go = new GameObject($"MFDVideoHost_{slotName}");
            UnityEngine.Object.DontDestroyOnLoad(go);
            active.videoPlayer = go.AddComponent<VideoPlayer>();
            active.audioSource = go.AddComponent<AudioSource>();
            active.videoRT = new RenderTexture(512, 256, 0);
            active.videoRT.Create();
            active.videoPlayer.source = VideoSource.Url;
            active.videoPlayer.url = directUrl;
            active.videoPlayer.isLooping = loopVideo.Value && !isLive;
            active.videoPlayer.playOnAwake = false;
            active.videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            active.videoPlayer.targetTexture = active.videoRT;
            active.videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
            active.videoPlayer.SetDirectAudioVolume(0, volume.Value);
            active.videoPlayer.errorReceived += (vp, msg) => Log.LogError($"[{slotName}] VP: {msg}");
            active.rawImg.texture = active.videoRT;
            active.videoPlayer.Play();

            if (isLive)
            {
                active.videoPlayer.loopPointReached += OnSegmentEnded;
                PreFetchNextSegment();
            }

            UpdateOverlayRect(active, layout);
            activeSlots[slotName] = active;
            editingSlot = slotName;
            Log.LogInfo($"[{slotName}] URL playing: {title}");
        }

        // ====================================================================
        // yt-dlp / ffmpeg (unchanged logic, condensed)
        // ====================================================================
        private string FindYtDlp()
        {
            if (!string.IsNullOrEmpty(ytdlpPath.Value) && File.Exists(ytdlpPath.Value)) return ytdlpPath.Value;
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var c in new[] { Path.Combine(pluginDir, "yt-dlp.exe"), Path.Combine(Path.GetDirectoryName(pluginDir) ?? "", "yt-dlp.exe") })
                if (File.Exists(c)) return c;
            return WhereCmd("yt-dlp.exe");
        }

        private string FindFfmpeg()
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var c in new[] { Path.Combine(pluginDir, "ffmpeg.exe"), Path.Combine(Path.GetDirectoryName(pluginDir) ?? "", "ffmpeg.exe") })
                if (File.Exists(c)) return c;
            return WhereCmd("ffmpeg.exe");
        }

        private string WhereCmd(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = "where", Arguments = exe, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                var proc = Process.Start(psi);
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    string first = output.Split('\n')[0].Trim();
                    if (File.Exists(first)) return first;
                }
            } catch { }
            return null;
        }

        private string ResolveWithYtDlp(string ytdlpExe, string url)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = ytdlpExe, Arguments = $"--get-url -f \"best[ext=mp4]/best\" \"{url}\"", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                var proc = Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd().Trim();
                string stderr = proc.StandardError.ReadToEnd().Trim();
                proc.WaitForExit(30000);
                if (proc.ExitCode != 0 && (stderr.Contains("is_live") || url.Contains("twitch")))
                {
                    psi.Arguments = $"--get-url -f \"best\" \"{url}\"";
                    proc = Process.Start(psi);
                    stdout = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(30000);
                }
                if (proc.ExitCode != 0) return null;
                return stdout.Split('\n')[0].Trim();
            } catch { return null; }
        }

        private string CaptureSegment(string ffmpegExe, string m3u8Url, int idx)
        {
            try
            {
                StopFfmpeg();
                string tempDir = Path.Combine(Path.GetTempPath(), "MFDCustomizer");
                Directory.CreateDirectory(tempDir);
                string outFile = Path.Combine(tempDir, $"live_seg_{idx}.mp4");
                if (File.Exists(outFile)) try { File.Delete(outFile); } catch { }
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = $"-y -i \"{m3u8Url}\" -t {SEGMENT_SECONDS} -c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -c:a aac -b:a 128k -f mp4 \"{outFile}\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                };
                isCapturing = true;
                ffmpegProcess = Process.Start(psi);
                new Thread(() => { try { ffmpegProcess?.StandardError.ReadToEnd(); } catch { } }) { IsBackground = true }.Start();
                bool exited = ffmpegProcess.WaitForExit((SEGMENT_SECONDS + 20) * 1000);
                isCapturing = false;
                if (!exited) { try { ffmpegProcess.Kill(); } catch { } }
                ffmpegProcess = null;
                if (!File.Exists(outFile) || new FileInfo(outFile).Length < 1024) return null;
                segmentPaths[idx] = outFile;
                return outFile;
            } catch (Exception e) { Log.LogError($"CaptureSegment: {e.Message}"); isCapturing = false; return null; }
        }

        private void OnSegmentEnded(VideoPlayer vp)
        {
            if (liveSlotName == null) return;
            int nextIdx = (playingSegIdx + 1) % 2;
            if (nextSegmentReady && segmentPaths[nextIdx] != null && File.Exists(segmentPaths[nextIdx]))
            {
                playingSegIdx = nextIdx;
                pendingSegmentSwitch = segmentPaths[nextIdx];
                nextSegmentReady = false;
            }
        }

        private void PreFetchNextSegment()
        {
            if (liveSlotName == null || !activeSlots.TryGetValue(liveSlotName, out var active)) return;
            int nextIdx = (playingSegIdx + 1) % 2;
            string url = active.liveM3u8Url;
            Thread t = new Thread(() =>
            {
                string seg = CaptureSegment(liveFfmpegExe, url, nextIdx);
                nextSegmentReady = seg != null;
            });
            t.IsBackground = true; t.Start();
        }

        private void HandleSegmentSwitch()
        {
            string path = pendingSegmentSwitch; pendingSegmentSwitch = null;
            if (liveSlotName == null || !activeSlots.TryGetValue(liveSlotName, out var active)) return;
            try
            {
                active.videoPlayer.Stop();
                active.videoPlayer.url = "file:///" + path.Replace('\\', '/');
                active.videoPlayer.Prepare();
                active.videoPlayer.prepareCompleted += OnLiveSegmentPrepared;
            } catch (Exception e) { Log.LogError($"Segment switch: {e.Message}"); }
        }

        private void OnLiveSegmentPrepared(VideoPlayer vp)
        {
            vp.prepareCompleted -= OnLiveSegmentPrepared;
            vp.Play();
            PreFetchNextSegment();
        }

        private void StopFfmpeg()
        {
            if (ffmpegProcess != null) { try { if (!ffmpegProcess.HasExited) ffmpegProcess.Kill(); } catch { } try { ffmpegProcess.Dispose(); } catch { } ffmpegProcess = null; }
        }

        private void CleanupSegmentFiles()
        {
            for (int i = 0; i < segmentPaths.Length; i++)
            {
                if (segmentPaths[i] != null && File.Exists(segmentPaths[i])) try { File.Delete(segmentPaths[i]); } catch { }
                segmentPaths[i] = null;
            }
        }


        // ====================================================================
        // GUI
        // ====================================================================
        internal void DrawGUI()
        {
            if (isResolving)
            {
                GUI.Label(new Rect(Screen.width/2-120, Screen.height/2-10, 240, 30), resolveStatus);
                return;
            }
            if (isEnteringUrl) { DrawUrlInput(); return; }
            if (isSelectingSource) { DrawSourceMenu(); return; }
            if (menuOpen) { DrawSlotMenu(); }

            // Status bar at bottom
            if (activeSlots.Count > 0 || editMode)
            {
                string info;
                if (editMode && editingSlot != null)
                {
                    var slots = GetSlotsForCurrentAircraft();
                    if (slots.ContainsKey(editingSlot))
                    {
                        var l = slots[editingSlot];
                        info = $"[EDIT {currentAircraftSection} / {editingSlot}] Arrows=Move  +/-=W  PgUp/Dn=H  " +
                               $"{slotCycleKey.Value}=Next slot  {editModeKey.Value}=Exit  |  " +
                               $"X={l.PosX.Value:F0} Y={l.PosY.Value:F0} W={l.Width.Value:F0} H={l.Height.Value:F0}";
                    }
                    else info = $"[EDIT] (slot missing)";
                }
                else
                {
                    info = $"MFD Customizer  [{currentAircraftSection}]  active: {activeSlots.Count}  |  {menuKey.Value}=menu  {editModeKey.Value}=edit";
                }
                GUI.Label(new Rect(10, Screen.height - 24, Screen.width - 20, 22), info);
            }
        }

        private GUIStyle _noWrapStyle;
        private GUIStyle GetNoWrapStyle()
        {
            if (_noWrapStyle == null)
            {
                _noWrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = false, clipping = TextClipping.Clip };
            }
            return _noWrapStyle;
        }

        private void DrawSlotMenu()
        {
            var slots = GetSlotsForCurrentAircraft();
            var keys = slots.Keys.ToList();
            int w = 720, h = 80 + keys.Count * 26;
            int x = Screen.width/2 - w/2, y = Screen.height/2 - h/2;
            GUI.Box(new Rect(x, y, w, h), $"MFD Slots — {currentAircraftSection}");
            int cy = y + 30;
            var style = GetNoWrapStyle();
            for (int i = 0; i < keys.Count && i < 9; i++)
            {
                var k = keys[i];
                string state;
                if (activeSlots.TryGetValue(k, out var a))
                {
                    string fn = Path.GetFileName(a.sourcePath ?? "");
                    if (fn.Length > 32) fn = fn.Substring(0, 29) + "...";
                    state = "> " + fn;
                }
                else state = "- stopped -";
                GUI.Label(new Rect(x + 20, cy, w - 40, 24), $"[{i+1}] {k.PadRight(18)}  {state}", style);
                cy += 26;
            }
            cy += 10;
            GUI.Label(new Rect(x + 20, cy, w - 40, 24), "[1-9] pick source  |  [Shift+1-9] stop one  |  [S] stop all  |  [Esc] close", style);
        }

        private void DrawSourceMenu()
        {
            int w = 560, h = 60 + Mathf.Min(mediaFiles.Count, 9) * 22 + 60;
            int x = Screen.width/2 - w/2, y = Screen.height/2 - h/2;
            GUI.Box(new Rect(x, y, w, h), $"Source for slot: {sourceTargetSlot}");
            int cy = y + 30;
            for (int i = 0; i < mediaFiles.Count && i < 9; i++)
            {
                GUI.Label(new Rect(x + 20, cy, w - 40, 22), $"[{i+1}] {Path.GetFileName(mediaFiles[i])}");
                cy += 22;
            }
            cy += 10;
            GUI.Label(new Rect(x + 20, cy, w - 40, 22), "[0] Enter URL    [Esc] back");
        }

        private void DrawUrlInput()
        {
            int w = 600, h = 140;
            int x = Screen.width/2 - w/2, y = Screen.height/2 - h/2;
            GUI.Box(new Rect(x, y, w, h), $"URL for slot: {sourceTargetSlot}");

            GUI.SetNextControlName("mfdvp_url_input");
            urlInput = GUI.TextField(new Rect(x + 20, y + 35, w - 40, 28), urlInput ?? "");
            if (!urlFocused)
            {
                GUI.FocusControl("mfdvp_url_input");
                urlFocused = true;
            }

            GUI.Label(new Rect(x + 20, y + 75, w - 40, 22), "Enter = play · Esc = cancel");

            if (GUI.Button(new Rect(x + 20, y + h - 35, 100, 26), "Play"))
            {
                if (!string.IsNullOrWhiteSpace(urlInput)) StartUrlFlow(urlInput);
                else { isEnteringUrl = false; urlFocused = false; }
            }
            if (GUI.Button(new Rect(x + 130, y + h - 35, 100, 26), "Cancel"))
            {
                isEnteringUrl = false; urlFocused = false;
            }

            if (Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                {
                    if (!string.IsNullOrWhiteSpace(urlInput)) StartUrlFlow(urlInput);
                    else { isEnteringUrl = false; urlFocused = false; }
                    Event.current.Use();
                }
                else if (Event.current.keyCode == KeyCode.Escape)
                {
                    isEnteringUrl = false; urlFocused = false;
                    Event.current.Use();
                }
            }
        }

        void OnDestroy()
        {
            StopFfmpeg();
            CleanupSegmentFiles();
            StopAllSlots(destroyRT: true);
        }
    }

    public class MFDRunner : MonoBehaviour
    {
        public static MFDRunner Instance;
        void Awake() { Instance = this; }
        void Update() { try { Plugin.Instance?.Tick(); } catch (Exception e) { UnityEngine.Debug.LogError("[MFDC] Tick: " + e); } }
        void OnGUI()  { try { Plugin.Instance?.DrawGUI(); } catch (Exception e) { UnityEngine.Debug.LogError("[MFDC] GUI: " + e); } }
    }
}
