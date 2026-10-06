using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppShapes;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(NotepadPlus.NotepadPlusMod), "Notepad Plus", "1.0.0", "4Dfish")]
[assembly: MelonGame("Iron Nest", "Iron Nest Heavy Turret Simulator")]

namespace NotepadPlus
{
    /// <summary>
    /// Two additions to the notepad the player plots on.
    ///
    /// MORE PENS. The game ships three pencils - red, yellow and white - and only yellow both
    /// shows the bearing/distance readout and stays out of the notepad's written notes. This adds
    /// blue, purple and green pencils that behave exactly like yellow.
    ///
    /// Each one draws with the game's own yellow marker prefab, so dragging, the arrow, the
    /// bearing and distance readout and the delete behaviour are the game's own and not a copy of
    /// them. Only the colour differs, and it is put on each stroke as the stroke is drawn and held
    /// there until well after the drag ends.
    ///
    /// LAYERS. Four plotting layers. A stroke joins whichever layer was active when it was drawn,
    /// and only one layer is shown at a time - or, in ghost mode, the others stay on screen at low
    /// opacity so they can be traced over.
    /// </summary>
    public class NotepadPlusMod : MelonMod
    {
        private const int LayerCount = 4;

        /// <summary>
        /// How long a fresh stroke is re-tinted, in frames. The game repaints parts of a stroke
        /// just after it appears; without this the pen's colour survives only on the parts the
        /// game leaves alone.
        /// </summary>
        private const int TintFrames = 90;

        /// <summary>
        /// A hard stop on re-tinting a stroke, in frames, however long the game keeps pointing at
        /// it. Without it a stroke the placer never lets go of would be repainted forever, and the
        /// game's own hover and delete highlights would never show through.
        /// </summary>
        private const int TintCap = 600;

        /// <summary>How long the readout lingers after the drawing view closes, in frames.</summary>
        private const int ZoneHoldFrames = 120;

        /// <summary>How many strokes are named in the log before the mod goes quiet about them.</summary>
        private const int StrokesLogged = 20;

        // =====================================================================================
        // Preferences
        // =====================================================================================

        private MelonPreferences_Entry<bool> _showHud;
        private MelonPreferences_Entry<bool> _addPencils;
        private MelonPreferences_Entry<bool> _verbose;
        private MelonPreferences_Entry<float> _ghostAlpha;
        private MelonPreferences_Entry<float> _penGap;
        private MelonPreferences_Entry<float> _penStartGap;

        private MelonPreferences_Entry<string> _keyLayer1, _keyLayer2, _keyLayer3, _keyLayer4;
        private MelonPreferences_Entry<string> _keyGhost;

        private MelonPreferences_Entry<float> _hueBlue, _hueGreen;

        private Key _kLayer1, _kLayer2, _kLayer3, _kLayer4, _kGhost;

        // =====================================================================================
        // Live game objects
        // =====================================================================================

        private ClipboardToolSelector _selector;
        private MapMarkerPlacer _placer;
        private Transform _mapTools;        // the notepad's tool rack, parent of the pencils

        private GameObject _yellowPencil;
        private ClipboardToolSlot _yellowSlot;
        private GameObject _yellowPrefab;   // the game's own prefab the yellow pencil draws with
        private GameObject _pencilMaster;   // a dormant copy the new pencils are stamped from
        private MarkerNoteLogger _noteLogger;

        private bool _ready;

        private GameObject _hudRoot;

        private TextMeshProUGUI _hudLine1;
        private TextMeshProUGUI _hudLine2;
        private bool _hudTried;
        private bool _hudFallback;

        private bool _atDrawingView = true;
        private int _zoneHold;
        private int _drawHold;
        private string _lastViewDiag;

        /// <summary>A pencil this mod added, and everything it needs to draw and to go home.</summary>
        private sealed class Pen
        {
            public string Label;                 // "Blue"
            public float Hue;                    // 0..1, baked into the pen's marker prefab
            public GameObject Prefab;            // this pen's own copy of the yellow marker prefab
            public GameObject Pencil;            // the pencil model on the notepad
            public ClipboardToolSlot Slot;
            public Transform RestPose;           // where the pencil returns to between picks
        }

        private readonly List<Pen> _pens = new List<Pen>();
        private Pen _activePen;                  // null while a stock pen (red/yellow/white) is picked
        private int _lastSelectedId;

        /// <summary>One plotted stroke, plus the colours to restore when it fades or comes back.</summary>
        private sealed class MarkerEntry
        {
            public GameObject Go;
            public MapMarkerHitTarget Hit;
            public ShapeRenderer[] Shapes;
            public Color[] ShapeBase;
            public Graphic[] Graphics;           // the origin disc, the labels, the tooltip
            public Color[] GraphicBase;
            public float LastAlpha = 1f;
            public float PenHue;
            public int TintFramesLeft;
            public int Age;
            public int DumpIn;
        }

        private readonly List<MarkerEntry>[] _layers = new List<MarkerEntry>[LayerCount];
        private readonly List<MarkerEntry> _freshStrokes = new List<MarkerEntry>();
        private readonly List<MarkerEntry> _dumping = new List<MarkerEntry>();
        private int _dumpsStarted;
        private readonly HashSet<int> _knownMarkers = new HashSet<int>();
        private int _activeLayer;
        private bool _ghost;
        private bool _layerDirty;
        private bool _loggedFailure;

        private int _frame;
        private int _lastFullScan;
        private int _strokesLogged;

        // =====================================================================================
        // Lifecycle
        // =====================================================================================

        public override void OnInitializeMelon()
        {
            for (int i = 0; i < LayerCount; i++)
            {
                _layers[i] = new List<MarkerEntry>();
            }

            var cat = MelonPreferences.CreateCategory("NotepadPlus", "Notepad Plus");

            _showHud = cat.CreateEntry("ShowHud", true, "Show the readout",
                "Show the on-screen layer/pen readout");
            _addPencils = cat.CreateEntry("AddPencils", true, "Put the new pencils on the notepad",
                "Put the blue/purple/green pencils on the notepad. Turn off to change colour " +
                "with the pen key only.");
            _ghostAlpha = cat.CreateEntry("GhostAlpha", 0.15f, "Ghost visibility",
                "How visible the other layers are in ghost mode (0 = invisible, 1 = solid)");
            _penStartGap = cat.CreateEntry("PenStartGap", 2.3f, "Clearance below the compass",
                "Gap between the compass and the first new pencil, as a multiple of the game's own " +
                "pencil spacing. Raise it if the blue pencil overlaps the compass.");
            _penGap = cat.CreateEntry("PenGap", 1.0f, "Gap between the new pencils",
                "Gap between the new pencils, as a multiple of the game's own pencil spacing. " +
                "1 is the same spacing the game gives its own pencils.");
            _verbose = cat.CreateEntry("VerboseLog", false, "Verbose logging",
                "Log every stroke and every layer change in full. Noisy.");

            _keyLayer1 = cat.CreateEntry("KeyLayer1", "1", "Layer 1 key", "Key that selects layer 1");
            _keyLayer2 = cat.CreateEntry("KeyLayer2", "2", "Layer 2 key", "Key that selects layer 2");
            _keyLayer3 = cat.CreateEntry("KeyLayer3", "3", "Layer 3 key", "Key that selects layer 3");
            _keyLayer4 = cat.CreateEntry("KeyLayer4", "4", "Layer 4 key", "Key that selects layer 4");
            _keyGhost = cat.CreateEntry("KeyGhost", "5", "Ghost key", "Key that toggles ghost mode");

            _hueBlue = cat.CreateEntry("HueBlue", 0.60f, "Blue hue", "Blue pen hue, 0..1");
            _hueGreen = cat.CreateEntry("HueGreen", 0.33f, "Green hue", "Green pen hue, 0..1");

            _kLayer1 = ParseKey(_keyLayer1.Value, Key.Digit1);
            _kLayer2 = ParseKey(_keyLayer2.Value, Key.Digit2);
            _kLayer3 = ParseKey(_keyLayer3.Value, Key.Digit3);
            _kLayer4 = ParseKey(_keyLayer4.Value, Key.Digit4);
            _kGhost = ParseKey(_keyGhost.Value, Key.Digit5);

            LoggerInstance.Msg("Notepad Plus v1.0.0 loaded.");
            LoggerInstance.Msg("  layers: " + KeyName(_kLayer1) + " " + KeyName(_kLayer2) + " " +
                               KeyName(_kLayer3) + " " + KeyName(_kLayer4) +
                               "   ghost: " + KeyName(_kGhost));

            // Write the settings out straight away, so there is a section in the config file to
            // edit rather than a set of defaults that only live in the code.
            try
            {
                MelonPreferences.Save();
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not write the settings file: " + e.Message);
            }
        }

        /// <summary>The key as a player would name it, so the log can be checked at a glance.</summary>
        private static string KeyName(Key key)
        {
            string name = key.ToString();
            return name.StartsWith("Digit") ? name.Substring(5) : name;
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Nothing found in one scene is valid in the next: the notepad and every stroke on it
            // belong to the scene that is going away.
            ResetState();
            if (_verbose.Value)
            {
                LoggerInstance.Msg("Scene '" + sceneName + "' loaded; waiting for the notepad.");
            }
        }

        private void ResetState()
        {
            _selector = null;
            _placer = null;
            _mapTools = null;
            _yellowPencil = null;
            _yellowSlot = null;
            _yellowPrefab = null;
            _pencilMaster = null;
            _noteLogger = null;
            _activePen = null;
            _lastSelectedId = 0;
            _activeLayer = 0;
            _layerDirty = false;
            _lastFullScan = 0;
            _strokesLogged = 0;
            _ready = false;
            _pens.Clear();
            ClearGuides();
            _freshStrokes.Clear();
            _dumping.Clear();
            _dumpsStarted = 0;
            _knownMarkers.Clear();
            for (int i = 0; i < LayerCount; i++)
            {
                _layers[i].Clear();
            }
        }

        public override void OnUpdate()
        {
            _frame++;
            try
            {
                if (!_ready)
                {
                    if (!TrySetup())
                    {
                        return;
                    }
                }

                ReadKeys();
                HandleMouseShortcuts();
                PollSelection();
                CatchNewStroke();
                ScanMarkers();
                RefreshFreshStrokes();
                TickPartDumps();
                ApplyVisibility();
                UpdateDrawingView();
                EnsureHud();
                RefreshHud();
            }
            catch (Exception e)
            {
                // A mod that throws every frame floods the log and drags the game down, so the
                // failure is reported once and then the mod goes quiet until the next scene.
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    LoggerInstance.Error("Notepad Plus stopped: " + e);
                }
                _ready = false;
                ResetState();
            }
        }

        // =====================================================================================
        // Setup
        // =====================================================================================

        private bool TrySetup()
        {
            _loggedFailure = false;
            _selector = FindLive<ClipboardToolSelector>();
            _placer = FindLive<MapMarkerPlacer>();
            if (_selector == null || _placer == null)
            {
                return false;
            }

            _mapTools = _selector.transform;

            _yellowPencil = FindChild(_mapTools, "Marker Pencil Button Yellow");
            _yellowSlot = _yellowPencil != null ? _yellowPencil.GetComponent<ClipboardToolSlot>() : null;
            if (_yellowSlot == null || _yellowSlot.markerPrefab == null)
            {
                return false;   // the notepad is not fully built yet; try again next frame
            }
            _yellowPrefab = _yellowSlot.markerPrefab;

            _noteLogger = FindLive<MarkerNoteLogger>();
            LoggerInstance.Msg("Notepad Plus: notepad logger " +
                               (_noteLogger != null ? "found" : "NOT found") +
                               ", note format '" +
                               (_noteLogger != null ? _noteLogger.logEntryFormat : "") + "'.");

            LogToolRack();
            BuildPens();

            _ready = true;
            _layerDirty = true;
            LoggerInstance.Msg("Notepad Plus: ready on the notepad built from '" +
                               _yellowPrefab.name + "'. Yellow pencil goes home to " +
                               ReadRestPose(_yellowSlot) + ".");
            return true;
        }

        private void LogToolRack()
        {
            if (!_verbose.Value)
            {
                return;
            }

            var names = new List<string>();
            for (int i = 0; i < _mapTools.childCount; i++)
            {
                Transform c = _mapTools.GetChild(i);
                names.Add(c != null ? c.name : "<null>");
            }
            LoggerInstance.Msg("  tools on the notepad: " + string.Join(", ", names.ToArray()));

            LoggerInstance.Msg("  strokes are created under '" + Describe(_placer.transform) +
                               "'; the page rect is '" + Describe(_placer.mapRect) + "'.");

            var prefabs = new List<string>();
            Il2CppSystem.Collections.Generic.List<GameObject> pf = _placer.markerPrefabs;
            if (pf != null)
            {
                for (int i = 0; i < pf.Count; i++)
                {
                    prefabs.Add(pf[i] != null ? pf[i].name : "<null>");
                }
            }
            LoggerInstance.Msg("  marker prefabs: " + string.Join(", ", prefabs.ToArray()));
        }

        private static string Describe(Component c)
        {
            if (c == null)
            {
                return "<none>";
            }
            Transform p = c.transform.parent;
            return c.name + (p != null ? " (under " + p.name + ")" : " (root)");
        }

        private void BuildPens()
        {
            _pens.Clear();

            if (!_addPencils.Value)
            {
                LoggerInstance.Msg("Notepad Plus: AddPencils is off; the new colours are on the pen key only.");
                return;
            }

            // The notepad outlives a level: it is not torn down and rebuilt between missions, so
            // the pens put on it last time are still there. Making a second set on top of them is
            // what put two of everything on the rack, so an intact set is taken over as it stands
            // and only an incomplete one is cleared away and rebuilt.
            if (PensAlreadyOnRack())
            {
                AdoptExistingPens();
                return;
            }
            ClearLeftoverPens();

            Transform yellow = _yellowPencil.transform;
            Vector3 yellowPos = yellow.localPosition;
            Quaternion yellowRot = yellow.localRotation;
            Vector3 yellowScale = yellow.localScale;

            // The rack runs down the notepad in a straight line, so the gap between the yellow and
            // compass pencils gives the spacing for the new ones. The compass is a wider tool than
            // a pencil, so the first new pencil is given extra clearance before its own row starts.
            GameObject compassGo = FindChild(_mapTools, "Marker Compas Button");
            Vector3 lastPos = compassGo != null ? compassGo.transform.localPosition : yellowPos;
            Vector3 step = (lastPos - yellowPos) * 0.5f;
            Vector3 cursor = lastPos + step * _penStartGap.Value;

            _pencilMaster = MakePencilMaster();
            if (_pencilMaster == null)
            {
                return;
            }

            AddPen("Marker Pencil Button Blue", "Blue", _hueBlue.Value, cursor, yellowRot, yellowScale);
            cursor += step * _penGap.Value;
            AddPen("Marker Pencil Button Green", "Green", _hueGreen.Value, cursor, yellowRot, yellowScale);

            // The stamp has done its job, and leaving a dormant extra pencil in the game's lists
            // would only invite trouble.
            UnityEngine.Object.Destroy(_pencilMaster);
            _pencilMaster = null;
        }

        /// <summary>
        /// A dormant copy of the yellow pencil, used as the stamp for the new ones.
        /// <para>
        /// A tool works out the spot it returns to while it is waking up, so a copy taken straight
        /// from the yellow pencil would learn the yellow pencil's row. A copy taken from this
        /// sleeping stamp stays asleep while it is put in its own place, and only wakes up once it
        /// is already there - so it learns the right spot, and the yellow pencil is never touched.
        /// </para>
        /// </summary>
        private GameObject MakePencilMaster()
        {
            GameObject master;
            try
            {
                master = UnityEngine.Object.Instantiate(_yellowPencil, _mapTools) as GameObject;
            }
            catch (Exception e)
            {
                LoggerInstance.Error("Could not make a pencil stamp: " + e.Message);
                return null;
            }

            if (master == null)
            {
                LoggerInstance.Error("Making a pencil stamp produced nothing.");
                return null;
            }

            master.name = "NotepadPlus Pencil Stamp";
            master.SetActive(false);
            return master;
        }

        /// <summary>
        /// Rubs out the guide rays this mod laid. The page can carry over between levels just as
        /// the notepad can, and a guide left behind would have no record of which layer it was
        /// drawn on.
        /// </summary>
        private void ClearGuides()
        {
            foreach (Guide guide in _guides)
            {
                if (guide != null && guide.Go != null)
                {
                    UnityEngine.Object.Destroy(guide.Go);
                }
            }
            _guides.Clear();
        }

        /// <summary>
        /// True when there is exactly one of each of this mod's pencils on the rack. Exactly one
        /// matters: a level that ended while the pens were being built, or a game that already
        /// doubled them up, leaves more than one, and taking over "the first one" would leave the
        /// rest standing there as strays.
        /// </summary>
        private bool PensAlreadyOnRack()
        {
            return CountChildren("Marker Pencil Button Blue") == 1 &&
                   CountChildren("Marker Pencil Button Green") == 1;
        }

        private int CountChildren(string name)
        {
            int count = 0;
            for (int i = 0; i < _mapTools.childCount; i++)
            {
                Transform c = _mapTools.GetChild(i);
                if (c != null && c.name == name)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Takes over the pencils left on the rack by the previous level, instead of making new
        /// ones. Their slots are already in the game's list and the game has been using them, so
        /// there is nothing to set up again beyond remembering them.
        /// </summary>
        private void AdoptExistingPens()
        {
            AdoptPen("Marker Pencil Button Blue", "Blue", _hueBlue.Value);
            AdoptPen("Marker Pencil Button Green", "Green", _hueGreen.Value);
            LoggerInstance.Msg("Notepad Plus: the notepad carried over from the last level, so the " +
                               "pens already on it are being used as they are.");
        }

        private void AdoptPen(string objectName, string label, float hue)
        {
            GameObject pencil = FindChild(_mapTools, objectName);
            ClipboardToolSlot slot = pencil != null ? pencil.GetComponent<ClipboardToolSlot>() : null;
            if (slot == null)
            {
                return;
            }

            GameObject rest = FindChild(_mapTools, "NotepadPlus Rest Pose " + label);
            _pens.Add(new Pen
            {
                Label = label,
                Hue = hue,
                Prefab = _yellowPrefab,
                Pencil = pencil,
                Slot = slot,
                RestPose = rest != null ? rest.transform : null,
            });
        }

        /// <summary>
        /// Removes anything this mod left on the rack - a half-built set from a level that ended
        /// mid-setup, or a set from an older version of the mod.
        /// </summary>
        private void ClearLeftoverPens()
        {
            var doomed = new List<GameObject>();
            for (int i = 0; i < _mapTools.childCount; i++)
            {
                Transform c = _mapTools.GetChild(i);
                if (c == null)
                {
                    continue;
                }
                string name = c.name;
                if (name == "Marker Pencil Button Blue" || name == "Marker Pencil Button Green" ||
                    name.StartsWith("NotepadPlus "))
                {
                    doomed.Add(c.gameObject);
                }
            }

            foreach (GameObject go in doomed)
            {
                ClipboardToolSlot slot = go.GetComponent<ClipboardToolSlot>();
                if (slot != null)
                {
                    try
                    {
                        var slots = _selector.slots;
                        if (slots != null)
                        {
                            slots.Remove(slot);
                        }
                    }
                    catch (Exception e)
                    {
                        LoggerInstance.Warning("Could not take a leftover pen out of the tool list: " + e.Message);
                    }
                }
                UnityEngine.Object.Destroy(go);
            }

            if (doomed.Count > 0)
            {
                LoggerInstance.Msg("Notepad Plus: cleared " + doomed.Count +
                                   " leftover object(s) from an earlier level before building the pens.");
            }
        }

        private void AddPen(string objectName, string label, float hue, Vector3 restLocal,
                            Quaternion rot, Vector3 scale)
        {
            // All three new pens draw with the game's own yellow marker prefab.
            //
            // Copying that prefab and keeping the copy was tried twice and was wrong both times: a
            // copy made at run time is a live object, so the game's scripts run over it the moment
            // it exists and the copy is no longer the clean template a prefab is meant to be.
            // Strokes made from such a copy came out with no arrow and could not be dragged. The
            // colour goes onto each stroke instead, and is kept there while it is being drawn.
            GameObject prefab = _yellowPrefab;

            GameObject pencil = CopyPencil(label, restLocal, rot, scale);
            if (pencil == null)
            {
                return;
            }

            ClipboardToolSlot slot = pencil.GetComponent<ClipboardToolSlot>();
            if (slot == null)
            {
                LoggerInstance.Error("The copied " + label + " pencil has no tool slot; dropping it.");
                UnityEngine.Object.Destroy(pencil);
                return;
            }

            // The pencil draws with its own copy of the yellow marker prefab, so the game supplies
            // every stroke already coloured and nothing has to be repainted afterwards.
            slot.markerPrefab = prefab;

            Transform rest = MakeRestPose(label, restLocal, rot, scale);
            slot.restPose = rest;

            // Belt and braces: tell the copy outright where its home is, in case the game reads
            // the values it captured on waking rather than the rest pose object.
            SetCapturedRestPose(slot, restLocal, rot, scale);

            var pen = new Pen
            {
                Label = label,
                Hue = hue,
                Prefab = prefab,
                Pencil = pencil,
                Slot = slot,
                RestPose = rest,
            };
            _pens.Add(pen);

            ApplySlotVisuals(slot);
            RecolourPencil(pencil, hue);

            Il2CppSystem.Collections.Generic.List<ClipboardToolSlot> slots = _selector.slots;
            if (slots != null)
            {
                slots.Add(slot);
            }
            else
            {
                LoggerInstance.Warning("The tool selector has no slot list; the " + label +
                                       " pen will work from the key only.");
            }

            LoggerInstance.Msg("Notepad Plus: added the " + label + " pen at " + Fmt(restLocal) +
                               " (hue " + hue.ToString("0.00") + "); rest pose now " + ReadRestPose(slot) + ".");
        }

        /// <summary>
        /// Stamps out the yellow pencil as a new, independent pencil, standing in its own place on
        /// the rack. It is asleep while it is placed and only wakes up afterwards, which is what
        /// makes it learn its own row as home rather than the yellow pencil's.
        /// </summary>
        private GameObject CopyPencil(string label, Vector3 restLocal, Quaternion rot, Vector3 scale)
        {
            GameObject pencil;
            try
            {
                pencil = UnityEngine.Object.Instantiate(_pencilMaster, _mapTools) as GameObject;
            }
            catch (Exception e)
            {
                LoggerInstance.Error("Could not copy the pencil for the " + label + " pen: " + e.Message);
                return null;
            }

            if (pencil == null)
            {
                LoggerInstance.Error("Copying the pencil for the " + label + " pen produced nothing.");
                return null;
            }

            pencil.name = "Marker Pencil Button " + label;
            Transform t = pencil.transform;
            t.localPosition = restLocal;
            t.localRotation = rot;
            t.localScale = scale;

            pencil.SetActive(true);
            return pencil;
        }

        /// <summary>
        /// A stand-in for the spot the pencil goes home to. A copy keeps the rest pose object the
        /// original pointed at, so each pen gets one of its own; without it every new pencil puts
        /// itself down on top of the yellow one. It sits in the tool rack alongside the pencils,
        /// which is the same space the game moves a pencil around in.
        /// </summary>
        private Transform MakeRestPose(string label, Vector3 restLocal, Quaternion rot, Vector3 scale)
        {
            Transform rest = new GameObject("NotepadPlus Rest Pose " + label).transform;
            rest.SetParent(_mapTools, false);
            rest.localPosition = restLocal;
            rest.localRotation = rot;
            rest.localScale = scale;
            return rest;
        }

        private void SetCapturedRestPose(ClipboardToolSlot slot, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            try
            {
                slot._CapturedRestLocalPosition_k__BackingField = pos;
                slot._CapturedRestLocalRotation_k__BackingField = rot;
                slot._CapturedRestLocalScale_k__BackingField = scale;
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not state the copied pencil's home directly: " + e.Message);
            }
        }

        private string ReadRestPose(ClipboardToolSlot slot)
        {
            try
            {
                slot.GetRestPose(out Vector3 pos, out Quaternion rot, out Vector3 scale);
                return Fmt(pos);
            }
            catch (Exception e)
            {
                return "<unreadable: " + e.Message + ">";
            }
        }

        /// <summary>
        /// Puts the copied pencil back into its resting look. A fresh copy comes up in whatever
        /// state the original was in when it was copied - possibly mid-hover - and the game only
        /// clears that the next time the tool changes.
        /// </summary>
        private void ApplySlotVisuals(ClipboardToolSlot slot)
        {
            try
            {
                slot.ApplyHover(false);
                slot.ApplySelected(false);
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not reset the copied pencil's visuals: " + e.Message);
            }
        }

        /// <summary>
        /// Recolours the pencil model itself. The body is an "Artistic &lt;colour&gt;" material, so
        /// the copy gets its own instance of that material and the wooden tip is left alone.
        /// </summary>
        private void RecolourPencil(GameObject pencil, float hue)
        {
            try
            {
                int painted = 0;
                foreach (MeshRenderer r in pencil.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r == null)
                    {
                        continue;
                    }

                    Il2CppReferenceArray<Material> src = r.sharedMaterials;
                    if (src == null || src.Length == 0)
                    {
                        continue;
                    }

                    bool changed = false;
                    var dst = new Material[src.Length];
                    for (int i = 0; i < src.Length; i++)
                    {
                        Material m = src[i];
                        dst[i] = m;
                        if (m == null || m.name == null || !m.name.StartsWith("Artistic"))
                        {
                            continue;   // the wooden tip and the hover light keep their own look
                        }

                        Color original = ReadMaterialColour(m);
                        float h, s, v;
                        Color.RGBToHSV(original, out h, out s, out v);
                        var tinted = Color.HSVToRGB(hue, Mathf.Max(s, 0.75f), Mathf.Max(v, 0.55f));
                        tinted.a = original.a;
                        dst[i] = TintedMaterial(m, tinted);
                        changed = true;
                        painted++;
                    }

                    if (changed)
                    {
                        r.materials = new Il2CppReferenceArray<Material>(dst);
                    }
                }

                if (painted == 0 && _verbose.Value)
                {
                    LoggerInstance.Msg("No 'Artistic' material on the " + pencil.name +
                                       "; its colour is baked somewhere else.");
                }
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not recolour " + pencil.name + ": " + e.Message);
            }
        }

        private static Color ReadMaterialColour(Material m)
        {
            if (m.HasProperty("_BaseColor"))
            {
                return m.GetColor("_BaseColor");
            }
            if (m.HasProperty("_Color"))
            {
                return m.GetColor("_Color");
            }
            return Color.white;
        }

        private static Material TintedMaterial(Material source, Color colour)
        {
            var copy = new Material(source);
            if (copy.HasProperty("_BaseColor"))
            {
                copy.SetColor("_BaseColor", colour);
            }
            if (copy.HasProperty("_Color"))
            {
                copy.SetColor("_Color", colour);
            }
            return copy;
        }

        // =====================================================================================
        // Pen selection
        // =====================================================================================

        /// <summary>
        /// Watches which tool is picked up. Picking the game's own red, yellow or white pencil
        /// must stop this mod tinting strokes; picking one of the added pencils starts again.
        /// </summary>
        private void PollSelection()
        {
            ClipboardToolSlot current = _selector.CurrentSelected;
            int id = current == null ? 0 : current.GetInstanceID();
            if (id == _lastSelectedId)
            {
                return;
            }

            _lastSelectedId = id;
            _activePen = null;
            for (int i = 0; i < _pens.Count; i++)
            {
                if (_pens[i].Slot != null && _pens[i].Slot.GetInstanceID() == id)
                {
                    _activePen = _pens[i];
                    break;
                }
            }

            if (_activePen != null)
            {
                _placer.SetActiveMarkerPrefab(_activePen.Prefab);
                LoggerInstance.Msg("Notepad Plus: " + _activePen.Label + " pen picked up.");
            }
            else if (current != null && _verbose.Value)
            {
                LoggerInstance.Msg("Notepad Plus: stock tool '" + current.gameObject.name +
                                   "' picked up; strokes keep the game's own colour.");
            }

            DumpToolRack("after picking up " + (current != null ? current.gameObject.name : "<nothing>"));
        }

        /// <summary>
        /// Lists everything standing on the tool rack, with where it is and whether it is switched
        /// on. Picking a pencil up showed a second copy of it, and the only way to tell a stray
        /// copy this mod left behind from the game's own is to look at what is actually there.
        /// </summary>
        private void DumpToolRack(string when)
        {
            try
            {
                LoggerInstance.Msg("Notepad Plus: the rack " + when + ":");
                for (int i = 0; i < _mapTools.childCount; i++)
                {
                    Transform c = _mapTools.GetChild(i);
                    if (c == null)
                    {
                        continue;
                    }
                    ClipboardToolSlot slot = c.GetComponent<ClipboardToolSlot>();
                    LoggerInstance.Msg("    '" + c.name + "'" +
                                       "  active=" + (c.gameObject.activeSelf ? "yes" : "no") +
                                       "  at " + Fmt(c.localPosition) +
                                       (slot != null ? "  [pen tool]" : "  [not a tool]"));
                }
                LoggerInstance.Msg("    (stamp left behind: " + (_pencilMaster != null ? "YES" : "no") +
                                   ", pens this mod made: " + _pens.Count + ")");
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not list the tool rack: " + e.Message);
            }
        }

        // =====================================================================================
        // Strokes: catching them, tinting them, sorting them into layers
        // =====================================================================================

        /// <summary>
        /// The moment a stroke is created.
        /// <para>
        /// The placer points at the stroke it is currently drawing, so a non-null current marker
        /// is the one and only moment a stroke can be said to belong to a layer. Catching it here
        /// rather than by noticing a new child later is what keeps a stroke from being filed under
        /// whichever layer the player happened to switch to while it was being drawn.
        /// </para>
        /// </summary>
        private void CatchNewStroke()
        {
            GameObject current = _placer.currentMarker;
            if (current != null)
            {
                RegisterMarker(current);
            }
        }

        private void ScanMarkers()
        {
            ScanChildren(_placer.transform);
            ScanChildren(_placer.mapRect);

            // Safety net in case strokes turn up somewhere neither of those covers.
            if (_frame - _lastFullScan > 120)
            {
                _lastFullScan = _frame;
                FullScan();
            }
        }

        private void ScanChildren(Transform root)
        {
            if (root == null || !root.gameObject.scene.isLoaded)
            {
                return;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);
                if (c == null)
                {
                    continue;
                }
                if (c.GetComponent<MapMarkerLineUI>() != null)
                {
                    RegisterMarker(c.gameObject);
                }
            }
        }

        private void FullScan()
        {
            foreach (MapMarkerLineUI ui in Resources.FindObjectsOfTypeAll<MapMarkerLineUI>())
            {
                if (ui == null)
                {
                    continue;
                }
                GameObject go = ui.gameObject;
                if (go == null || !go.scene.IsValid() || !go.scene.isLoaded)
                {
                    continue;   // a prefab asset, not a stroke on the page
                }
                RegisterMarker(go);
            }
        }

        private void RegisterMarker(GameObject go)
        {
            int id = go.GetInstanceID();
            if (!_knownMarkers.Add(id))
            {
                return;
            }

            var entry = new MarkerEntry { Go = go };

            // The pen's colour is already on the prefab this stroke came from, so normally there
            // is nothing to do. Tinting again keeps the colour on the parts the game repaints.
            if (_activePen != null)
            {
                entry.PenHue = _activePen.Hue;
                entry.TintFramesLeft = TintFrames;
                ApplyPenTint(go, entry.PenHue);
                _freshStrokes.Add(entry);
            }

            CaptureColours(entry);
            FlushShapes(entry);
            _layers[_activeLayer].Add(entry);
            _layerDirty = true;

            // The first few strokes always report themselves: "nothing happens when I draw" and
            // "the strokes are not being seen at all" look identical on screen otherwise.
            if (_verbose.Value || _strokesLogged < StrokesLogged)
            {
                _strokesLogged++;
                Transform parent = go.transform.parent;
                LoggerInstance.Msg("Notepad Plus: stroke on layer " + (_activeLayer + 1) +
                                   (_activePen != null ? " as " + _activePen.Label : " (game colour)") +
                                   ", under '" + (parent != null ? parent.name : "<root>") +
                                   "', " + entry.Shapes.Length + " shape(s), " +
                                   entry.Graphics.Length + " graphic(s).");
            }

            // The first couple of strokes also have every part of them listed twice - once as the
            // stroke is drawn and once after it has settled - so that "this bit is still the
            // game's colour" can be pinned to a named part instead of guessed at. Only strokes
            // drawn with one of the added pens are worth listing; the strokes already on the page
            // when the mod loads are the game's own and would use the budget up.
            if (_activePen != null && (_verbose.Value || _dumpsStarted < 2))
            {
                _dumpsStarted++;
                entry.DumpIn = 150;
                _dumping.Add(entry);
                DumpParts(entry, "as drawn");
            }
        }

        private void TickPartDumps()
        {
            for (int i = _dumping.Count - 1; i >= 0; i--)
            {
                MarkerEntry e = _dumping[i];
                if (e.Go == null || --e.DumpIn > 0)
                {
                    if (e.Go == null)
                    {
                        _dumping.RemoveAt(i);
                    }
                    continue;
                }

                DumpParts(e, "after settling, with the re-tinting stopped");
                _dumping.RemoveAt(i);
            }
        }

        private void DumpParts(MarkerEntry e, string when)
        {
            LoggerInstance.Msg("Notepad Plus: every part of one stroke, " + when + ":");
            foreach (ShapeRenderer s in CollectShapes(e.Go))
            {
                LoggerInstance.Msg("    shape  '" + s.gameObject.name + "'  rgba(" +
                                   s.color.r.ToString("0.00") + ", " + s.color.g.ToString("0.00") + ", " +
                                   s.color.b.ToString("0.00") + ", " + s.color.a.ToString("0.00") + ")" +
                                   "  blend=" + s.blendMode + (s.enabled ? "" : "  [disabled]"));
            }
            foreach (Graphic g in CollectGraphics(e.Go))
            {
                LoggerInstance.Msg("    figure '" + g.gameObject.name + "'  rgba(" +
                                   g.color.r.ToString("0.00") + ", " + g.color.g.ToString("0.00") + ", " +
                                   g.color.b.ToString("0.00") + ", " + g.color.a.ToString("0.00") + ")" +
                                   (g.enabled ? "" : "  [disabled]"));
            }
        }

        /// <summary>
        /// Keeps re-applying the pen's colour over the first moments of a stroke's life. The game
        /// repaints parts of a stroke shortly after it appears, which is what left the drawn line
        /// yellow while the arrow and labels took the new colour.
        /// </summary>
        private void RefreshFreshStrokes()
        {
            GameObject drawing = _placer.currentMarker;
            int drawingId = drawing != null ? drawing.GetInstanceID() : 0;

            for (int i = _freshStrokes.Count - 1; i >= 0; i--)
            {
                MarkerEntry e = _freshStrokes[i];
                if (e.Go == null)
                {
                    _freshStrokes.RemoveAt(i);
                    continue;
                }

                e.Age++;
                bool beingDrawn = e.Go.GetInstanceID() == drawingId;
                if (e.TintFramesLeft > 0)
                {
                    e.TintFramesLeft--;
                }

                // The colour is held on while the stroke is under the cursor, and for a few
                // seconds after it is let go, because the game repaints parts of a stroke at the
                // end of a drag. That repaint is what used to leave the drawn line itself in the
                // game's own yellow however carefully the rest of the stroke was coloured.
                if ((!beingDrawn && e.TintFramesLeft <= 0) || e.Age > TintCap)
                {
                    _freshStrokes.RemoveAt(i);
                    continue;
                }

                ApplyPenTint(e.Go, e.PenHue);
                ApplyAlpha(e, e.LastAlpha);
            }
        }

        /// <summary>
        /// Every tintable part of a stroke.
        /// <para>
        /// The shapes are gathered through the concrete types as well as the ShapeRenderer base,
        /// and the graphics through the concrete types as well as Graphic. A lookup by a base
        /// class is the kind of thing that can quietly return nothing, and a silent no-op here is
        /// exactly what leaves part of a stroke in the game's own colour.
        /// </para>
        /// </summary>
        private static List<ShapeRenderer> CollectShapes(GameObject go)
        {
            var found = new List<ShapeRenderer>();
            var seen = new HashSet<int>();
            AddAll(go.GetComponentsInChildren<ShapeRenderer>(true), found, seen);
            AddAll(go.GetComponentsInChildren<Line>(true), found, seen);
            AddAll(go.GetComponentsInChildren<Polygon>(true), found, seen);
            AddAll(go.GetComponentsInChildren<Disc>(true), found, seen);
            return found;
        }

        private static void AddAll<T>(Il2CppArrayBase<T> from, List<ShapeRenderer> into,
                                      HashSet<int> seen) where T : ShapeRenderer
        {
            if (from == null)
            {
                return;
            }
            foreach (T s in from)
            {
                if (s != null && seen.Add(s.GetInstanceID()))
                {
                    into.Add(s);
                }
            }
        }

        private static List<Graphic> CollectGraphics(GameObject go)
        {
            var found = new List<Graphic>();
            var seen = new HashSet<int>();
            AddAll(go.GetComponentsInChildren<Graphic>(true), found, seen);
            AddAll(go.GetComponentsInChildren<Image>(true), found, seen);
            AddAll(go.GetComponentsInChildren<TMP_Text>(true), found, seen);
            AddAll(go.GetComponentsInChildren<TextMeshPro>(true), found, seen);
            AddAll(go.GetComponentsInChildren<TextMeshProUGUI>(true), found, seen);
            return found;
        }

        private static void AddAll<T>(Il2CppArrayBase<T> from, List<Graphic> into,
                                      HashSet<int> seen) where T : Graphic
        {
            if (from == null)
            {
                return;
            }
            foreach (T g in from)
            {
                if (g != null && seen.Add(g.GetInstanceID()))
                {
                    into.Add(g);
                }
            }
        }

        /// <summary>
        /// Pushes a stroke's colour towards the pen's hue. Brightly coloured parts - the line, the
        /// arrow, the numbers, the small disc where the line starts, and the "place" highlight
        /// that sits over the line and arrow at rest - move to the new hue and keep their
        /// brightness; the near-white and near-black parts, which is what makes the labels
        /// readable, are left exactly as they are.
        /// <para>
        /// Only the delete highlight is skipped: that is the game warning the player what is about
        /// to be rubbed out, and recolouring it would hide the warning. The place highlight is not
        /// skipped - it is part of the stroke as it sits on the page, and leaving it alone is what
        /// kept a yellow casing over a blue line.
        /// </para>
        /// </summary>
        private static void ApplyPenTint(GameObject go, float hue)
        {
            foreach (ShapeRenderer s in CollectShapes(go))
            {
                if (!IsDeleteHighlight(s))
                {
                    s.color = ShiftHue(s.color, hue);
                }
            }
            foreach (Graphic g in CollectGraphics(go))
            {
                if (!IsDeleteHighlight(g))
                {
                    g.color = ShiftHue(g.color, hue);
                }
            }
        }

        private static bool IsDeleteHighlight(Component c)
        {
            if (c == null || c.gameObject == null)
            {
                return false;
            }
            string name = c.gameObject.name;
            return name.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Color ShiftHue(Color c, float hue)
        {
            float h, s, v;
            Color.RGBToHSV(c, out h, out s, out v);
            if (s < 0.20f)
            {
                return c;   // grey, white or black: not the pen's colour, so not this mod's to move
            }

            Color tinted = Color.HSVToRGB(hue, Mathf.Max(s, 0.65f), Mathf.Max(v, 0.80f));
            tinted.a = c.a;
            return tinted;
        }

        private static void CaptureColours(MarkerEntry e)
        {
            List<ShapeRenderer> shapes = CollectShapes(e.Go);
            e.Shapes = shapes.ToArray();
            e.ShapeBase = new Color[shapes.Count];
            for (int i = 0; i < shapes.Count; i++)
            {
                e.ShapeBase[i] = shapes[i] != null ? shapes[i].color : Color.white;
            }

            List<Graphic> graphics = CollectGraphics(e.Go);
            e.Graphics = graphics.ToArray();
            e.GraphicBase = new Color[graphics.Count];
            for (int i = 0; i < graphics.Count; i++)
            {
                e.GraphicBase[i] = graphics[i] != null ? graphics[i].color : Color.white;
            }

            e.Hit = e.Go.GetComponent<MapMarkerHitTarget>();
        }

        private static void ApplyAlpha(MarkerEntry e, float mul)
        {
            for (int i = 0; i < e.Shapes.Length; i++)
            {
                ShapeRenderer s = e.Shapes[i];
                if (s == null)
                {
                    continue;
                }
                Color c = e.ShapeBase[i];
                c.a = e.ShapeBase[i].a * mul;
                s.color = c;
            }
            for (int i = 0; i < e.Graphics.Length; i++)
            {
                Graphic g = e.Graphics[i];
                if (g == null)
                {
                    continue;
                }
                Color c = e.GraphicBase[i];
                c.a = e.GraphicBase[i].a * mul;
                g.color = c;
            }
        }

        /// <summary>
        /// Pushes the colours just written into the shapes through to the material.
        /// <para>
        /// A shape applies its material properties when it is enabled, which is why a stroke
        /// comes out in the pen's colour: it is tinted before it is ever switched on. Anything
        /// written afterwards - fading a layer, for one - sits in the component and never reaches
        /// the material, so the line and the arrow went on drawing at full strength while the disc
        /// and the labels, which are ordinary UI graphics, faded properly. Asking for the
        /// properties to be re-applied by hand is what closes that gap.
        /// </para>
        /// </summary>
        private static void FlushShapes(MarkerEntry e)
        {
            foreach (ShapeRenderer s in e.Shapes)
            {
                if (s == null)
                {
                    continue;
                }
                try
                {
                    s.UpdateAllMaterialProperties();
                }
                catch
                {
                    // A shape that has already gone away is not worth reporting on.
                }
            }
        }

        // =====================================================================================
        // Layers
        // =====================================================================================

        private void SetLayer(int layer)
        {
            if (layer < 0 || layer >= LayerCount)
            {
                return;
            }
            if (layer == _activeLayer)
            {
                LoggerInstance.Msg("Notepad Plus: already on layer " + (layer + 1) + ".");
                return;
            }

            _activeLayer = layer;
            _layerDirty = true;

            int total = 0;
            for (int i = 0; i < LayerCount; i++)
            {
                total += _layers[i].Count;
            }
            LoggerInstance.Msg("Notepad Plus: layer " + (layer + 1) + " of " + LayerCount + " - " +
                               _layers[layer].Count + " stroke(s) here, " + total + " in all" +
                               (_ghost ? "; ghosting the others" : "") + "; " + SampleStroke() + ".");
        }

        private void ToggleGhost()
        {
            _ghost = !_ghost;
            _layerDirty = true;
            LoggerInstance.Msg("Notepad Plus: ghost mode " + (_ghost ? "on" : "off") + ".");
        }

        /// <summary>
        /// The state of one tracked stroke, for the log.
        /// <para>
        /// Watching this across layer changes is how "the lines get fainter every time I switch"
        /// is told apart from "the mod is fading the other layers on purpose": if the number stays
        /// put while the screen says otherwise, the fading is coming from the game, not from here.
        /// </para>
        /// </summary>
        private string SampleStroke()
        {
            for (int layer = 0; layer < LayerCount; layer++)
            {
                List<MarkerEntry> list = _layers[layer];
                for (int i = 0; i < list.Count; i++)
                {
                    MarkerEntry e = list[i];
                    if (e.Go == null || e.Shapes.Length == 0 || e.Shapes[0] == null)
                    {
                        continue;
                    }
                    return "sample stroke on layer " + (layer + 1) + " alpha=" +
                           e.Shapes[0].color.a.ToString("0.00") + " shown=" + e.Go.activeSelf;
                }
            }
            return "no strokes to sample";
        }

        /// <summary>
        /// Shows the active layer, and hides or fades the rest. Strokes are never reparented:
        /// the page is drawn through a rect mask and moving a stroke out of it would drop it off
        /// the visible area, so a hidden layer is simply switched off where it stands.
        /// </summary>
        private void ApplyVisibility()
        {
            if (!_layerDirty)
            {
                return;
            }

            // The stroke being drawn is never touched: switching it off halfway through would
            // leave a half-built marker behind, and moving it between layers mid-drag would file
            // it under the wrong one.
            GameObject drawing = _placer.currentMarker;
            int drawingId = drawing != null ? drawing.GetInstanceID() : 0;

            bool busy = drawing != null ||
                        (Mouse.current != null && Mouse.current.leftButton.isPressed);
            if (busy)
            {
                return;
            }

            _layerDirty = false;
            for (int layer = 0; layer < LayerCount; layer++)
            {
                bool isActive = layer == _activeLayer;
                bool show = isActive || _ghost;
                float mul = isActive ? 1f : (_ghost ? _ghostAlpha.Value : 1f);

                List<MarkerEntry> list = _layers[layer];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    MarkerEntry e = list[i];
                    if (e.Go == null)
                    {
                        list.RemoveAt(i);
                        continue;
                    }
                    if (e.Go.GetInstanceID() == drawingId)
                    {
                        continue;
                    }

                    try
                    {
                        if (show)
                        {
                            if (!e.Go.activeSelf)
                            {
                                e.Go.SetActive(true);
                            }
                            if (!Mathf.Approximately(mul, e.LastAlpha))
                            {
                                ApplyAlpha(e, mul);
                                FlushShapes(e);
                                e.LastAlpha = mul;
                            }
                            if (e.Hit != null && !e.Hit.enabled)
                            {
                                e.Hit.enabled = true;
                            }
                        }
                        else
                        {
                            if (e.Hit != null && e.Hit.enabled)
                            {
                                e.Hit.enabled = false;   // a hidden stroke must not answer the cursor
                            }
                            if (e.Go.activeSelf)
                            {
                                e.Go.SetActive(false);
                            }
                        }
                    }
                    catch
                    {
                        list.RemoveAt(i);   // the game deleted this stroke out from under us
                    }
                }
            }
        }

        // =====================================================================================
        // Input and readout
        // =====================================================================================

        private void HandleMouseShortcuts()
        {
            Mouse mouse;
            try
            {
                mouse = Mouse.current;
            }
            catch
            {
                return;
            }

            if (mouse == null)
            {
                return;
            }

            if (mouse.middleButton.wasPressedThisFrame)
            {
                NoteHoveredStroke();
            }
            if (mouse.rightButton.wasPressedThisFrame)
            {
                DeleteGuideNearCursor(mouse);
            }
        }

        // =====================================================================================
        // Guide rays
        // =====================================================================================

        /// <summary>A dashed guide ray laid over the page, with the stroke it was drawn from.</summary>
        private sealed class Guide
        {
            public GameObject Go;
            public string From;      // a short description, for the log
        }

        private readonly List<Guide> _guides = new List<Guide>();

        /// <summary>
        /// Pressing L over a stroke lays a dashed guide ray along that stroke's own direction.
        /// <para>
        /// The ray starts at the stroke's starting point and runs forward until it meets the edge
        /// of the page, which is worked out against the page's own rectangle rather than left to
        /// the game's mask - the mask only clips the game's UI pieces, and a shape drawn by hand
        /// would run straight off the table. Its thickness, caps, colour and material are copied
        /// from that stroke's own line, so it looks like something the game drew.
        /// </para>
        /// </summary>
        private void CreateGuideFromHovered()
        {
            if (!_atDrawingView)
            {
                return;
            }

            MapMarkerLineUI stroke;
            try
            {
                MapMarkerHitTarget hit = _placer.hoveredHitTarget;
                if (hit == null)
                {
                    LoggerInstance.Msg("Notepad Plus: point at a stroke first, then press L.");
                    return;
                }
                stroke = hit.gameObject.GetComponent<MapMarkerLineUI>();
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not find the stroke under the cursor: " + e.Message);
                return;
            }

            if (stroke == null || stroke.line == null)
            {
                LoggerInstance.Warning("That stroke has no line to take a direction from.");
                return;
            }

            try
            {
                Transform parent = stroke.transform.parent;
                if (parent == null)
                {
                    parent = stroke.transform;
                }

                // Both ends come from world positions rather than from the stroke's "local"
                // readings: the marker itself stands at the point the stroke was started from -
                // the little disc is on it - and the pointer tip stands at the arrow. Mixing the
                // two local readings together was what skewed the ray, because they do not come
                // from the same space.
                Vector3 originWorld = stroke.transform.position;
                Vector3 tipWorld = stroke.pointerTip != null
                    ? stroke.pointerTip.position
                    : stroke.transform.TransformPoint(stroke.TipLocalPosition);

                RectTransform page = _placer.mapRect;
                if (page == null)
                {
                    LoggerInstance.Warning("No page to measure the guide against.");
                    return;
                }

                Vector3 originOnPage = page.InverseTransformPoint(originWorld);
                Vector3 tipOnPage = page.InverseTransformPoint(tipWorld);

                var from = new Vector2(originOnPage.x, originOnPage.y);
                var toward = new Vector2(tipOnPage.x - originOnPage.x, tipOnPage.y - originOnPage.y);
                if (toward.sqrMagnitude < 1e-10f)
                {
                    LoggerInstance.Msg("Notepad Plus: that stroke is too short to give a direction.");
                    return;
                }
                toward.Normalize();

                Vector2 edge = ClipToPage(page.rect, from, toward);
                Vector3 edgeWorld = page.TransformPoint(new Vector3(edge.x, edge.y, originOnPage.z));

                LoggerInstance.Msg("Notepad Plus: stroke runs " + Fmt(originWorld) + " -> " + Fmt(tipWorld) +
                                   ", on the page heading " + toward.x.ToString("0.000") + "," +
                                   toward.y.ToString("0.000") + ".");

                // The stroke's own line is copied rather than built from scratch: everything that
                // makes it look like something the game drew - its materials, its glow, its
                // thickness - comes along with the copy, and only the two ends and the dashes are
                // changed.
                GameObject go = UnityEngine.Object.Instantiate(stroke.line.gameObject, parent) as GameObject;
                if (go == null)
                {
                    LoggerInstance.Warning("Could not copy the stroke's line for the guide ray.");
                    return;
                }

                go.name = "NotepadPlus Guide";
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;

                Line line = go.GetComponent<Line>();
                if (line == null)
                {
                    LoggerInstance.Warning("The copied line has no shape on it; dropping the guide.");
                    UnityEngine.Object.Destroy(go);
                    return;
                }

                line.dashed = true;
                line.start = parent.InverseTransformPoint(originWorld);
                line.end = parent.InverseTransformPoint(edgeWorld);

                _guides.Add(new Guide { Go = go, From = "a stroke" });
                RegisterGuideAsLayerMember(go);

                LoggerInstance.Msg("Notepad Plus: guide ray laid, length " +
                                   Vector3.Distance(originWorld, edgeWorld).ToString("0.00") +
                                   ", from " + Fmt(originWorld) + " to " + Fmt(edgeWorld) + ".");
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not lay the guide ray: " + e);
            }
        }

        /// <summary>
        /// Where a ray that starts inside the page leaves it. Each side is solved for the distance
        /// along the ray at which it is met, and the nearest of those is the edge.
        /// </summary>
        private static Vector2 ClipToPage(Rect page, Vector2 from, Vector2 towards)
        {
            float nearest = float.MaxValue;

            if (Mathf.Abs(towards.x) > 1e-6f)
            {
                float t = (towards.x > 0f ? page.xMax - from.x : page.xMin - from.x) / towards.x;
                if (t > 0f)
                {
                    nearest = Mathf.Min(nearest, t);
                }
            }
            if (Mathf.Abs(towards.y) > 1e-6f)
            {
                float t = (towards.y > 0f ? page.yMax - from.y : page.yMin - from.y) / towards.y;
                if (t > 0f)
                {
                    nearest = Mathf.Min(nearest, t);
                }
            }

            if (nearest == float.MaxValue)
            {
                return from;   // the page has no size, or the ray starts outside it
            }
            return from + towards * nearest;
        }

        /// <summary>
        /// Files the guide ray under the layer that is open, so it hides and fades with the
        /// strokes drawn on that layer.
        /// </summary>
        private void RegisterGuideAsLayerMember(GameObject go)
        {
            var entry = new MarkerEntry { Go = go };
            CaptureColours(entry);
            FlushShapes(entry);
            _layers[_activeLayer].Add(entry);
            _layerDirty = true;
        }

        /// <summary>
        /// Right-clicking near a guide ray rubs it out. The game's own right-click only knows
        /// about its own strokes, so the test is done here: both ends of the ray are put on the
        /// screen and the cursor's distance to that line is measured in pixels.
        /// </summary>
        private void DeleteGuideNearCursor(Mouse mouse)
        {
            if (!_atDrawingView || _guides.Count == 0)
            {
                return;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            Vector2 cursor = mouse.position.ReadValue();
            const float reach = 16f;   // pixels

            for (int i = _guides.Count - 1; i >= 0; i--)
            {
                Guide guide = _guides[i];
                if (guide.Go == null)
                {
                    _guides.RemoveAt(i);
                    continue;
                }

                Line line = guide.Go.GetComponent<Line>();
                if (line == null)
                {
                    continue;
                }

                Vector3 a = guide.Go.transform.TransformPoint(line.start);
                Vector3 b = guide.Go.transform.TransformPoint(line.end);
                Vector3 sa = cam.WorldToScreenPoint(a);
                Vector3 sb = cam.WorldToScreenPoint(b);
                if (sa.z < 0f || sb.z < 0f)
                {
                    continue;   // behind the camera
                }

                if (PixelDistanceToSegment(cursor, new Vector2(sa.x, sa.y), new Vector2(sb.x, sb.y)) > reach)
                {
                    continue;
                }

                _guides.RemoveAt(i);
                RemoveFromLayers(guide.Go);
                UnityEngine.Object.Destroy(guide.Go);
                LoggerInstance.Msg("Notepad Plus: guide ray removed.");
                return;   // one per click
            }
        }

        private static float PixelDistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            if (lengthSq < 1e-6f)
            {
                return Vector2.Distance(point, a);
            }
            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSq);
            return Vector2.Distance(point, a + ab * t);
        }

        private void RemoveFromLayers(GameObject go)
        {
            int id = go.GetInstanceID();
            for (int layer = 0; layer < LayerCount; layer++)
            {
                List<MarkerEntry> list = _layers[layer];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Go != null && list[i].Go.GetInstanceID() == id)
                    {
                        list.RemoveAt(i);
                    }
                }
            }
            _knownMarkers.Remove(id);
        }

        // =====================================================================================
        // Writing a stroke's reading onto the notepad
        // =====================================================================================

        /// <summary>
        /// Middle-clicking a stroke writes that stroke's bearing and distance onto the notepad.
        /// <para>
        /// Which stroke is under the cursor is the game's own answer - the placer keeps track of
        /// the stroke being hovered - and the two readings are taken from the labels already drawn
        /// on that stroke, so the note says exactly what the page says. The note itself goes in
        /// through the logger the red pencil writes with, so it lands in the same section, in the
        /// same style, with the same typing animation.
        /// </para>
        /// </summary>
        private void NoteHoveredStroke()
        {
            if (!_atDrawingView)
            {
                return;
            }

            MapMarkerHitTarget hit;
            try
            {
                hit = _placer.hoveredHitTarget;
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not ask which stroke is under the cursor: " + e.Message);
                return;
            }

            if (hit == null)
            {
                return;
            }

            MapMarkerLineUI stroke = hit.gameObject.GetComponent<MapMarkerLineUI>();
            if (stroke == null)
            {
                return;
            }

            if (_noteLogger == null)
            {
                LoggerInstance.Warning("No notepad logger to write through; nothing was written.");
                return;
            }

            string text = ReadingText(stroke);
            if (string.IsNullOrEmpty(text))
            {
                LoggerInstance.Warning("That stroke has no bearing or distance to write down.");
                return;
            }

            try
            {
                _noteLogger.LogCustomNote(text);
                LoggerInstance.Msg("Notepad Plus: wrote '" + text.Replace("\n", " ") + "' onto the notepad.");
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not write onto the notepad: " + e.Message);
            }
        }

        /// <summary>
        /// The stroke's reading as text. The labels the game drew are used when they are there,
        /// so the note matches the page; otherwise the numbers behind them are formatted.
        /// </summary>
        private string ReadingText(MapMarkerLineUI stroke)
        {
            string bearing = LabelText(stroke.angleLabel);
            string distance = LabelText(stroke.distanceLabel);

            if (string.IsNullOrEmpty(bearing) || string.IsNullOrEmpty(distance))
            {
                try
                {
                    if (string.IsNullOrEmpty(bearing))
                    {
                        bearing = stroke.AngleValue.ToString("0.0");
                    }
                    if (string.IsNullOrEmpty(distance))
                    {
                        distance = stroke.DistanceValue.ToString("0.00");
                    }
                }
                catch (Exception e)
                {
                    LoggerInstance.Warning("Could not read that stroke's numbers: " + e.Message);
                }
            }

            if (string.IsNullOrEmpty(bearing) && string.IsNullOrEmpty(distance))
            {
                return null;
            }

            // The game's own note format is used when it is there, so a note put on the page with
            // the middle button reads exactly like one the red pencil wrote.
            string format = null;
            try
            {
                format = _noteLogger != null ? _noteLogger.logEntryFormat : null;
            }
            catch
            {
                format = null;
            }

            if (!string.IsNullOrEmpty(format))
            {
                string filled = format
                    .Replace("{angle}", Trim(bearing))
                    .Replace("{distance}", Trim(distance));
                if (filled.IndexOf('{') < 0 && filled.IndexOf('}') < 0)
                {
                    return filled;
                }
            }

            return "方位角 " + Trim(bearing) + "    距离 " + Trim(distance);
        }

        private static string LabelText(TMP_Text label)
        {
            try
            {
                return label != null ? label.text : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Trim(string text)
        {
            return string.IsNullOrEmpty(text) ? "?" : text.Trim();
        }

        private void ReadKeys()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null)
            {
                return;
            }

            if (Pressed(kb, _kLayer1))
            {
                SetLayer(0);
            }
            if (Pressed(kb, _kLayer2))
            {
                SetLayer(1);
            }
            if (Pressed(kb, _kLayer3))
            {
                SetLayer(2);
            }
            if (Pressed(kb, _kLayer4))
            {
                SetLayer(3);
            }
            if (Pressed(kb, _kGhost))
            {
                ToggleGhost();
            }
            if (Pressed(kb, Key.L))
            {
                CreateGuideFromHovered();
            }
        }

        private static bool Pressed(Keyboard kb, Key key)
        {
            switch (key)
            {
                case Key.Digit1: return kb.digit1Key.wasPressedThisFrame;
                case Key.Digit2: return kb.digit2Key.wasPressedThisFrame;
                case Key.Digit3: return kb.digit3Key.wasPressedThisFrame;
                case Key.Digit4: return kb.digit4Key.wasPressedThisFrame;
                case Key.Digit5: return kb.digit5Key.wasPressedThisFrame;
                case Key.Digit6: return kb.digit6Key.wasPressedThisFrame;
                case Key.Digit7: return kb.digit7Key.wasPressedThisFrame;
                case Key.Digit8: return kb.digit8Key.wasPressedThisFrame;
                case Key.Digit9: return kb.digit9Key.wasPressedThisFrame;
                case Key.Digit0: return kb.digit0Key.wasPressedThisFrame;
                case Key.L: return kb.lKey.wasPressedThisFrame;
                case Key.F1: return kb.f1Key.wasPressedThisFrame;
                case Key.F2: return kb.f2Key.wasPressedThisFrame;
                case Key.F3: return kb.f3Key.wasPressedThisFrame;
                case Key.F4: return kb.f4Key.wasPressedThisFrame;
                case Key.F5: return kb.f5Key.wasPressedThisFrame;
                case Key.F6: return kb.f6Key.wasPressedThisFrame;
                case Key.F7: return kb.f7Key.wasPressedThisFrame;
                case Key.F8: return kb.f8Key.wasPressedThisFrame;
                case Key.F9: return kb.f9Key.wasPressedThisFrame;
                case Key.F10: return kb.f10Key.wasPressedThisFrame;
                case Key.F11: return kb.f11Key.wasPressedThisFrame;
                case Key.F12: return kb.f12Key.wasPressedThisFrame;
                default: return false;
            }
        }

        public override void OnGUI()
        {
            // Only drawn when the TextMeshPro readout could not be built. The game's own drawing
            // is left alone otherwise; the readout is a canvas of its own and draws itself.
            if (!_ready || !_showHud.Value || !_hudFallback)
            {
                return;
            }

            const float width = 360f;
            const float height = 56f;
            var box = new Rect((Screen.width - width) * 0.5f, 12f, width, height);
            GUI.Box(box, string.Empty);
            GUI.Label(new Rect(box.x, box.y + 6f, box.width, 20f),
                      "layer " + (_activeLayer + 1) + " / " + LayerCount +
                      "      ghost: " + (_ghost ? "on" : "off"));
            GUI.Label(new Rect(box.x, box.y + 30f, box.width, 20f),
                      KeyName(_kLayer1) + "-" + KeyName(_kLayer4) + " layer      " +
                      KeyName(_kGhost) + " ghost");
        }

        /// <summary>
        /// Builds the readout: a screen-space canvas with a small panel across the top middle of
        /// the screen and two lines of text on it.
        /// <para>
        /// TextMeshPro rather than the immediate-mode GUI, because the built-in GUI font has no
        /// Chinese glyphs and the only way to give that one a different font goes through
        /// GUI.skin - which is one of the calls this build of the game has stripped. TextMeshPro
        /// is what the fire-control mod's Chinese runs on, so it is known to work here.
        /// </para>
        /// <para>
        /// If any of it fails, the readout falls back to a plain English box rather than
        /// disappearing: a readout nobody can see is worse than one in the wrong language.
        /// </para>
        /// </summary>
        private void EnsureHud()
        {
            if (_hudRoot != null || _hudTried)
            {
                return;
            }
            _hudTried = true;

            try
            {
                var root = new GameObject("NotepadPlus HUD");
                UnityEngine.Object.DontDestroyOnLoad(root);

                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;

                var scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

                // No GraphicRaycaster: the readout must never take a click meant for the game.

                var panel = new GameObject("Panel");
                panel.transform.SetParent(root.transform, false);
                var background = panel.AddComponent<Image>();
                background.color = new Color(0f, 0f, 0f, 0.32f);
                background.raycastTarget = false;

                var rect = panel.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 1f);
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = new Vector2(0f, -8f);
                rect.sizeDelta = new Vector2(360f, 56f);

                TMP_FontAsset font = PickHudFont();
                _hudLine1 = MakeHudLine(panel.transform, font, 5f);
                _hudLine2 = MakeHudLine(panel.transform, font, 29f);

                _hudRoot = root;
                LoggerInstance.Msg("Notepad Plus: readout built with font '" +
                                   (font != null && font.name != null ? font.name : "<built-in>") + "'.");
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not build the readout, falling back to the plain " +
                                       "English one: " + e);
                _hudRoot = null;
                _hudFallback = true;
            }
        }

        private static TextMeshProUGUI MakeHudLine(Transform parent, TMP_FontAsset font, float top)
        {
            var go = new GameObject("Line");
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                text.font = font;
            }
            text.fontSize = 15f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false;
            text.enableWordWrapping = false;

            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -top);
            rect.sizeDelta = new Vector2(-12f, 20f);
            return text;
        }

        /// <summary>
        /// The game's own text font, the same one the fire-control mod's Chinese runs on.
        /// <para>
        /// A font built from an operating-system font is only tried if that one is missing,
        /// because the calls that reach for the system fonts are among the ones this build of the
        /// game has stripped - asking for them is what made the readout disappear outright.
        /// </para>
        /// </summary>
        private TMP_FontAsset PickHudFont()
        {
            try
            {
                TMP_FontAsset gameFont = TMP_Settings.defaultFontAsset;
                if (gameFont != null)
                {
                    return gameFont;
                }
                LoggerInstance.Warning("The game has no default text font to borrow.");
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not read the game's text font: " + e.Message);
            }

            string[] wanted = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" };
            foreach (string name in wanted)
            {
                try
                {
                    Font os = Font.CreateDynamicFontFromOSFont(name, 16);
                    if (os == null)
                    {
                        continue;
                    }
                    TMP_FontAsset made = TMP_FontAsset.CreateFontAsset(os);
                    if (made != null)
                    {
                        return made;
                    }
                }
                catch
                {
                    // Not usable here; the readout falls back to the plain English box.
                }
            }
            return null;
        }

        /// <summary>
        /// Works out whether the player is at the map table with the drawing view open, so the
        /// readout only appears while it is useful.
        /// <para>
        /// Pressing E at the table is what turns the drawing view on, and the map placer switches
        /// its click actions on to match, so those actions are the signal to read. Every candidate
        /// signal is logged when it changes, so which one actually follows the view can be checked
        /// from the log rather than guessed at. If no signal can be read at all the readout stays
        /// up: hiding it on a signal nobody can read is how it went missing before.
        /// </para>
        /// </summary>
        private void UpdateDrawingView()
        {
            // The map table's camera zone is the signal: it is the collider the player stands in
            // when the drawing view is open. It drops the moment they step back, so the readout is
            // held on for a moment after it clears and for as long as a stroke is being drawn -
            // otherwise it would blink out mid-line.
            bool inZone = false;
            try
            {
                inZone = CameraZoneTrigger.s_currentActiveZone != null;
            }
            catch
            {
                inZone = true;   // unreadable: keep the readout up rather than lose it
            }

            if (inZone)
            {
                _zoneHold = ZoneHoldFrames;
            }
            else if (_zoneHold > 0)
            {
                _zoneHold--;
            }

            bool drawing = false;
            try
            {
                drawing = _placer.currentMarker != null;
            }
            catch
            {
                drawing = false;
            }
            if (drawing)
            {
                _drawHold = ZoneHoldFrames;
            }
            else if (_drawHold > 0)
            {
                _drawHold--;
            }

            _atDrawingView = inZone || _zoneHold > 0 || drawing || _drawHold > 0;

            string diag = "atTable=" + (inZone ? "yes" : "no") +
                          " mapZone='" + MapZoneName() + "'" +
                          " drawing=" + (drawing ? "yes" : "no") +
                          " readout=" + (_atDrawingView ? "shown" : "hidden");
            if (diag != _lastViewDiag)
            {
                _lastViewDiag = diag;
                LoggerInstance.Msg("Notepad Plus: " + diag);
            }
        }

        private static string CameraName()
        {
            try
            {
                Camera cam = Camera.main;
                return cam != null ? cam.name : "<none>";
            }
            catch
            {
                return "<unreadable>";
            }
        }

        /// <summary>Which camera zone the player is standing in, if any.</summary>
        private static string MapZoneName()
        {
            try
            {
                CameraZoneTrigger zone = CameraZoneTrigger.s_currentActiveZone;
                return zone != null ? zone.gameObject.name : "<none>";
            }
            catch
            {
                return "<unreadable>";
            }
        }

        private void RefreshHud()
        {
            if (_hudRoot == null)
            {
                return;
            }

            bool wanted = _showHud.Value && _atDrawingView;
            if (_hudRoot.activeSelf != wanted)
            {
                _hudRoot.SetActive(wanted);
            }
            if (!wanted)
            {
                return;   // nothing to write while it is off
            }

            string line1 = "图层 " + (_activeLayer + 1) + " / " + LayerCount +
                           "      叠影：" + (_ghost ? "开" : "关");
            string line2 = KeyName(_kLayer1) + "-" + KeyName(_kLayer4) + " 切图层      " +
                           KeyName(_kGhost) + " 切换叠影";

            if (_hudLine1 != null && _hudLine1.text != line1)
            {
                _hudLine1.text = line1;
            }
            if (_hudLine2 != null && _hudLine2.text != line2)
            {
                _hudLine2.text = line2;
            }
        }

        // =====================================================================================
        // Small helpers
        // =====================================================================================

        /// <summary>
        /// The live component, or null.
        /// <para>
        /// FindObjectsOfTypeAll also returns uninstantiated prefabs and objects left over from
        /// scenes that are no longer loaded, and neither is the notepad the player is using.
        /// </para>
        /// </summary>
        private static T FindLive<T>() where T : Component
        {
            foreach (T o in Resources.FindObjectsOfTypeAll<T>())
            {
                if (o == null)
                {
                    continue;
                }
                GameObject go = o.gameObject;
                if (go == null || !go.scene.IsValid() || !go.scene.isLoaded)
                {
                    continue;
                }
                if (!go.activeInHierarchy)
                {
                    continue;
                }
                return o;
            }
            return null;
        }

        private static GameObject FindChild(Transform parent, string name)
        {
            if (parent == null)
            {
                return null;
            }
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c != null && c.name == name)
                {
                    return c.gameObject;
                }
            }
            return null;
        }

        /// <summary>
        /// Reads a key name from the preferences.
        /// <para>
        /// A bare digit has to be spelled out: Enum.TryParse reads "1" as the enum's *number*, and
        /// in the input system's key enum number 1 is the space bar, 4 is the backquote and 5 is
        /// the quote key. Asking for "1" and silently getting the space bar is exactly the sort of
        /// thing that makes a mod look like it is fighting the game.
        /// </para>
        /// </summary>
        private static Key ParseKey(string text, Key fallback)
        {
            try
            {
                string t = text == null ? string.Empty : text.Trim();
                if (t.Length == 0)
                {
                    return fallback;
                }
                if (t.Length == 1 && t[0] >= '0' && t[0] <= '9')
                {
                    return Enum.TryParse("Digit" + t, true, out Key digit) ? digit : fallback;
                }
                return Enum.TryParse(t, true, out Key parsed) ? parsed : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static string Fmt(Vector3 v)
        {
            return "(" + v.x.ToString("0.0000") + ", " + v.y.ToString("0.0000") + ", " +
                   v.z.ToString("0.0000") + ")";
        }
    }
}
