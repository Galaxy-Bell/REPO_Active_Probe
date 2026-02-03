using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace REPO_Active_Probe
{
    [BepInPlugin("GalaxyBell.REPO_Active_Probe", "REPO_Active_Probe", "0.4.0")]
    public class Plugin : BaseUnityPlugin
    {
        // ---------- config ----------
        private ConfigEntry<bool> EnableProbeLog;
        private ConfigEntry<bool> EnableStacks;
        private ConfigEntry<bool> EnableTrace;
        private ConfigEntry<float> TraceSeconds;
        private ConfigEntry<KeyCode> KeyMark;
        private ConfigEntry<KeyCode> KeyF2_ButtonPress;
        private ConfigEntry<KeyCode> KeyF3_OnClick;
        private ConfigEntry<KeyCode> KeyTraceToggle;

        // ---------- runtime ----------
        private Harmony _harmony;

        private static readonly object _fileLock = new object();
        private static string LogFile = "";
        private static int MainThreadId = -1;

        // config snapshot into statics (so static patch class can read)
        private static bool S_LogEnabled = true;
        private static bool S_StacksEnabled = true;
        private static bool S_TraceEnabled = true;

        // Trace window (only log selected methods when TraceActive==true)
        private static bool TraceActive = false;
        private static float TraceEndTime = 0f;

        // rate limiting
        private static int _logLines = 0;
        private const int LOG_LINE_SOFT_CAP = 20000;

        // cached EP list (avoid FindObjectsOfType<MonoBehaviour>() heavy scan every keypress)
        private static Type _epType;
        private static List<Component> _cachedEPs = new List<Component>();
        private static float _lastScanRealtime = -999f;
        private const float RESCAN_COOLDOWN = 2.0f;

        private void Awake()
        {
            EnableProbeLog = Config.Bind("General", "EnableProbeLog", true, "Write probe logs to file.");
            EnableStacks = Config.Bind("General", "EnableStacks", true, "Dump stacktrace for ButtonPress/OnClick to tell NATIVE vs REFLECT.");
            EnableTrace = Config.Bind("Trace", "EnableTrace", true, "Enable trace mode (logs extra methods only inside a time window).");
            TraceSeconds = Config.Bind("Trace", "TraceSeconds", 3.0f, "Trace duration after F3 (seconds).");

            KeyMark = Config.Bind("Keybinds", "MarkKey", KeyCode.F1, "Write a MARK line into the log.");
            KeyF2_ButtonPress = Config.Bind("Keybinds", "ForceButtonPressKey", KeyCode.F2, "Invoke nearest ExtractionPoint.ButtonPress() by reflection.");
            KeyF3_OnClick = Config.Bind("Keybinds", "ForceOnClickKey", KeyCode.F3, "Invoke nearest ExtractionPoint.OnClick() by reflection (closest to native).");
            KeyTraceToggle = Config.Bind("Keybinds", "TraceToggleKey", KeyCode.F4, "Toggle Trace mode. When ON: F3 auto-opens a trace window.");

            InitFileLog();

            MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

            S_LogEnabled = EnableProbeLog.Value;
            S_StacksEnabled = EnableStacks.Value;
            S_TraceEnabled = EnableTrace.Value;

            SWL($"=== REPO_Active_Probe 0.4.0 Awake ===");
            SWL($"Unity={Application.unityVersion}");
            SWL($"Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            SWL($"MainTID={MainThreadId}");
            SWL($"TraceEnabled={S_TraceEnabled} TraceSeconds={TraceSeconds.Value}");
            SWL($"Keys: F1={KeyMark.Value} F2={KeyF2_ButtonPress.Value} F3={KeyF3_OnClick.Value} F4={KeyTraceToggle.Value}");

            try
            {
                _harmony = new Harmony("GalaxyBell.REPO_Active_Probe.Harmony");
                PatchExtractionPoint();
                PatchTraceCandidates(); // prefix early-return unless TraceActive
                SWL("=== Probe ready ===");
            }
            catch (Exception e)
            {
                SWL("[ERR] Harmony patch failed: " + e);
            }

            // scene hook: rescan EPs after load
            try
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch { }
        }

        private void OnDestroy()
        {
            try
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
            }
            catch { }

            try
            {
                _harmony?.UnpatchSelf();
                SWL("=== OnDestroy: Unpatched ===");
            }
            catch { }
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            // small delayed scan; game objects may spawn after sceneLoaded
            try
            {
                StartCoroutine(DelayedRescan());
            }
            catch { }
        }

        private System.Collections.IEnumerator DelayedRescan()
        {
            yield return new WaitForSeconds(1.0f);
            ScanExtractionPoints(force: true);
        }

        private void Update()
        {
            if (!S_LogEnabled) return;

            // Trace window end
            if (TraceActive && Time.realtimeSinceStartup > TraceEndTime)
            {
                TraceActive = false;
                SWL($"[{Now()}] [TRACE] window ended");
            }

            if (Input.GetKeyDown(KeyMark.Value))
            {
                SWL($"\n===== MARK {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====\n");
            }

            if (Input.GetKeyDown(KeyTraceToggle.Value))
            {
                S_TraceEnabled = !S_TraceEnabled;
                SWL($"[{Now()}] [TRACE] TraceEnabled={S_TraceEnabled}");
            }

            if (Input.GetKeyDown(KeyF2_ButtonPress.Value))
            {
                SWL($"\n===== FORCE TEST (F2 ButtonPress) {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====");
                ForceInvokeNearest("ButtonPress");
            }

            if (Input.GetKeyDown(KeyF3_OnClick.Value))
            {
                SWL($"\n===== FORCE TEST (F3 OnClick) {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====");

                // open trace window for a short time
                if (S_TraceEnabled)
                {
                    TraceActive = true;
                    TraceEndTime = Time.realtimeSinceStartup + Mathf.Max(0.5f, TraceSeconds.Value);
                    SWL($"[{Now()}] [TRACE] window started for {TraceSeconds.Value:0.00}s");
                }

                ForceInvokeNearest("OnClick");
            }
        }

        // ----------------- core invoke -----------------

        private void ForceInvokeNearest(string methodName)
        {
            try
            {
                ScanExtractionPoints(force: false);

                SWL($"[{Now()}] [FORCE] ExtractionPoint cached={_cachedEPs.Count}");

                if (_cachedEPs.Count == 0)
                {
                    SWL($"[{Now()}] [FORCE] No ExtractionPoint found.");
                    return;
                }

                Vector3 refPos = GetReferencePos();
                Component nearest = null;
                float best = float.MaxValue;

                foreach (var ep in _cachedEPs)
                {
                    if (ep == null) continue;
                    float d = Vector3.Distance(refPos, ep.transform.position);
                    if (d < best)
                    {
                        best = d;
                        nearest = ep;
                    }
                }

                if (nearest == null)
                {
                    SWL($"[{Now()}] [FORCE] nearest=null");
                    return;
                }

                SWL($"[{Now()}] [FORCE] Nearest EP: name={nearest.gameObject.name} pos={nearest.transform.position.ToString("F2")} dist={best:0.00}");

                var t = nearest.GetType();
                var m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (m == null)
                {
                    SWL($"[{Now()}] [FORCE] Method missing: {t.FullName}.{methodName}()");
                    return;
                }

                var args = BuildDefaultArgs(m);
                SWL($"[{Now()}] [FORCE] Invoke: {methodName}{FormatSig(m)} argsLen={(args == null ? 0 : args.Length)}");
                m.Invoke(nearest, args);
                SWL($"[{Now()}] [FORCE] Invoke done.");
            }
            catch (Exception e)
            {
                SWL($"[{Now()}] [ERR] ForceInvokeNearest({methodName}) failed: {e}");
            }
        }

        private static object[] BuildDefaultArgs(MethodInfo mi)
        {
            try
            {
                var ps = mi.GetParameters();
                if (ps == null || ps.Length == 0) return null;

                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                        continue;
                    }

                    var pt = p.ParameterType;

                    // ref/out -> just null / default
                    if (pt.IsByRef)
                    {
                        pt = pt.GetElementType();
                    }

                    if (pt == null)
                    {
                        args[i] = null;
                        continue;
                    }

                    // reference types
                    if (!pt.IsValueType)
                    {
                        args[i] = null;
                        continue;
                    }

                    // structs / primitives
                    try
                    {
                        args[i] = Activator.CreateInstance(pt);
                    }
                    catch
                    {
                        args[i] = null;
                    }
                }
                return args;
            }
            catch
            {
                return null;
            }
        }

        private void ScanExtractionPoints(bool force)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (!force && (now - _lastScanRealtime) < RESCAN_COOLDOWN) return;

                _lastScanRealtime = now;

                if (_epType == null)
                {
                    _epType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a =>
                        {
                            try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                        })
                        .FirstOrDefault(t => t != null && t.Name == "ExtractionPoint");
                }

                if (_epType == null)
                {
                    // don't spam
                    return;
                }

                // Use Unity overload by Type (no compile-time dependency)
                var found = UnityEngine.Object.FindObjectsOfType(_epType);
                _cachedEPs = found
                    .OfType<Component>()
                    .Where(c => c != null)
                    .ToList();

                SWL($"[{Now()}] [SCAN] EP rescan done: {_cachedEPs.Count}");
            }
            catch (Exception e)
            {
                SWL($"[{Now()}] [ERR] ScanExtractionPoints failed: {e.GetType().Name} {e.Message}");
            }
        }

        private Vector3 GetReferencePos()
        {
            try
            {
                if (Camera.main != null) return Camera.main.transform.position;
            }
            catch { }

            // Try player-tag object (common in many Unity games)
            try
            {
                var p = GameObject.FindWithTag("Player");
                if (p != null) return p.transform.position;
            }
            catch { }

            // fallback: 0
            return Vector3.zero;
        }

        // ----------------- patches -----------------

        private void PatchExtractionPoint()
        {
            _epType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t != null && t.Name == "ExtractionPoint");

            if (_epType == null)
            {
                SWL("[REF] ExtractionPoint type NOT FOUND.");
                return;
            }

            var mButtonPress = _epType.GetMethod("ButtonPress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mOnClick = _epType.GetMethod("OnClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mStateSet = _epType.GetMethod("StateSet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mStateSetRPC = _epType.GetMethod("StateSetRPC", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mHaulGoal = _epType.GetMethod("HaulGoalSetRPC", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mSurplus = _epType.GetMethod("ExtractionPointSurplusRPC", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mDeny = _epType.GetMethod("ButtonDenyRPC", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mAuto = _epType.GetMethod("ActivateTheFirstExtractionPointAutomaticallyWhenAPlayerLeaveTruck",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            SWL($"[REF] ExtractionPoint type={_epType.FullName} ButtonPress={(mButtonPress != null)} OnClick={(mOnClick != null)}");

            PatchIf(mOnClick, "EP.OnClick");
            PatchIf(mButtonPress, "EP.ButtonPress");
            PatchIf(mStateSet, "EP.StateSet");
            PatchIf(mStateSetRPC, "EP.StateSetRPC");
            PatchIf(mHaulGoal, "EP.HaulGoalSetRPC");
            PatchIf(mSurplus, "EP.ExtractionPointSurplusRPC");
            PatchIf(mDeny, "EP.ButtonDenyRPC");
            PatchIf(mAuto, "EP.AutoActivateFromTruckDoor");
        }

        private void PatchIf(MethodInfo mi, string label)
        {
            try
            {
                if (mi == null)
                {
                    SWL($"[PATCH] SKIP {label} (missing)");
                    return;
                }

                var pre = new HarmonyMethod(typeof(Patches), nameof(Patches.AnyMethodPrefix));
                var post = new HarmonyMethod(typeof(Patches), nameof(Patches.AnyMethodPostfix));
                _harmony.Patch(mi, prefix: pre, postfix: post);
                SWL($"[PATCH] OK {label} -> {mi.DeclaringType?.Name}.{mi.Name}{FormatSig(mi)}");
            }
            catch (Exception e)
            {
                SWL($"[PATCH] FAIL {label}: {e.GetType().Name} {e.Message}");
            }
        }

        private static string FormatSig(MethodInfo mi)
        {
            try
            {
                var ps = mi.GetParameters();
                var p = string.Join(",", ps.Select(x => x.ParameterType.Name));
                return "(" + p + ")";
            }
            catch { return "()"; }
        }

        // Trace patched methods: log ONLY inside trace window.
        // v0.4.0 改进：减少 patch 数量 + 更严格过滤，避免卡加载/爆日志
        private void PatchTraceCandidates()
        {
            int patched = 0;
            int considered = 0;

            var keywords = new[]
            {
                "Map","MiniMap","Minimap","Marker","Waypoint","Ping","Indicator","Compass",
                "Toast","Popup","Notify","Notification","Money","Value","Reward","Price",
                "Extraction","Haul","Goal","HUD","UI"
            };

            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                SWL("[TRACE] Assembly-CSharp not found.");
                return;
            }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch { types = Array.Empty<Type>(); }

            foreach (var t in types)
            {
                if (t == null) continue;

                MethodInfo[] ms;
                try
                {
                    ms = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch { continue; }

                foreach (var m in ms)
                {
                    if (m == null) continue;
                    considered++;

                    if (m.IsSpecialName) continue;
                    if (m.DeclaringType == null) continue;

                    // avoid patching huge generic / iterator / compiler-gen noise
                    var name = m.Name ?? "";
                    if (name.StartsWith("<")) continue;

                    // keep trace lightweight
                    var ps = m.GetParameters();
                    if (ps != null && ps.Length > 3) continue;

                    var typeName = m.DeclaringType.Name ?? "";

                    bool hit = keywords.Any(k =>
                        name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0
                        || typeName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!hit) continue;

                    if (patched >= 80) break;

                    try
                    {
                        var pre = new HarmonyMethod(typeof(Patches), nameof(Patches.TracePrefix));
                        _harmony.Patch(m, prefix: pre);
                        patched++;
                    }
                    catch { }
                }

                if (patched >= 80) break;
            }

            SWL($"[TRACE] candidate methods considered={considered}, patched={patched} (logs only when TraceActive=true)");
        }

        // ----------------- thread-safe log -----------------

        private void InitFileLog()
        {
            try
            {
                string dir = Path.Combine(Paths.ConfigPath, "REPO_Active_Probe", "logs");
                Directory.CreateDirectory(dir);
                LogFile = Path.Combine(dir, $"REPO_Active_Probe_{DateTime.Now:yyyyMMdd_HHmmss}.log");

                lock (_fileLock)
                {
                    File.AppendAllText(LogFile, $"[FileLog] {LogFile}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch { }
        }

        private static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");
        private static int Tid() => System.Threading.Thread.CurrentThread.ManagedThreadId;

        private static void SWL(string line)
        {
            try
            {
                if (!S_LogEnabled) return;
                if (string.IsNullOrEmpty(LogFile)) return;

                lock (_fileLock)
                {
                    if (_logLines < LOG_LINE_SOFT_CAP)
                    {
                        File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
                        _logLines++;
                    }
                }
            }
            catch { }
        }

        internal class Patches
        {
            private static bool OnMainThread() => Tid() == MainThreadId;

            public static void AnyMethodPrefix(MethodBase __originalMethod, object __instance, object[] __args)
            {
                try
                {
                    string mName = __originalMethod?.Name ?? "nullMethod";
                    string tName = __originalMethod?.DeclaringType?.Name ?? "nullType";

                    // non-main thread: never touch Unity objects
                    if (!OnMainThread())
                    {
                        SWL($"[{Now()}] [EP][T{Tid()}] PRE {tName}.{mName} argsLen={(__args == null ? 0 : __args.Length)}");
                        return;
                    }

                    string goName = "n/a";
                    string pos = "n/a";
                    if (__instance is Component c)
                    {
                        goName = c.gameObject ? c.gameObject.name : "nullGO";
                        pos = c.transform ? c.transform.position.ToString("F2") : "nullTf";
                    }

                    string args = DumpArgs(__args);
                    SWL($"[{Now()}] [EP][T{Tid()}] PRE {tName}.{mName} go={goName} pos={pos} argsLen={(__args == null ? 0 : __args.Length)} args={args}");

                    if (S_StacksEnabled && (mName == "ButtonPress" || mName == "OnClick"))
                    {
                        DumpStackIfNeeded(mName);
                    }
                }
                catch { }
            }

            public static void AnyMethodPostfix(MethodBase __originalMethod)
            {
                try
                {
                    string mName = __originalMethod?.Name ?? "nullMethod";
                    string tName = __originalMethod?.DeclaringType?.Name ?? "nullType";
                    SWL($"[{Now()}] [EP][T{Tid()}] POST {tName}.{mName}");
                }
                catch { }
            }

            public static void TracePrefix(MethodBase __originalMethod)
            {
                try
                {
                    if (!TraceActive) return;

                    string mName = __originalMethod?.Name ?? "nullMethod";
                    string tName = __originalMethod?.DeclaringType?.FullName ?? "nullType";
                    SWL($"[{Now()}] [TR][T{Tid()}] {tName}.{mName}");
                }
                catch { }
            }

            private static string DumpArgs(object[] args)
            {
                try
                {
                    if (args == null || args.Length == 0) return "[]";
                    var parts = new List<string>();
                    for (int i = 0; i < args.Length; i++)
                    {
                        object a = args[i];
                        if (a == null) parts.Add("null");
                        else
                        {
                            string s;
                            try { s = a.ToString(); } catch { s = a.GetType().Name; }
                            if (s.Length > 120) s = s.Substring(0, 120) + "...";
                            parts.Add(s);
                        }
                    }
                    return "[" + string.Join(", ", parts) + "]";
                }
                catch { return "[?]"; }
            }

            private static void DumpStackIfNeeded(string method)
            {
                try
                {
                    string stack = Environment.StackTrace ?? "";
                    string origin = (stack.Contains("RuntimeMethodInfo.Invoke") || stack.Contains("MethodBase.Invoke"))
                        ? "REFLECT"
                        : "NATIVE";

                    SWL($"[{Now()}] [EP][T{Tid()}] STACK {method} origin={origin}");
                    var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(40);
                    foreach (var l in lines)
                        SWL("      " + l);
                }
                catch { }
            }
        }
    }
}
