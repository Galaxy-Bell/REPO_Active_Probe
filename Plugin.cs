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

namespace REPO_Active_Probe
{
    [BepInPlugin("GalaxyBell.REPO_Active_Probe", "REPO_Active_Probe", "0.3.6")]
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
        private static bool StaticLogEnabled = true;

        // Trace window (only log selected methods when TraceActive==true)
        private static bool TraceEnabled = false;
        private static bool TraceActive = false;
        private static float TraceEndTime = 0f;

        // for a bit of rate limiting
        private static int _logLines = 0;
        private const int LOG_LINE_SOFT_CAP = 20000;

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
            StaticLogEnabled = EnableProbeLog.Value;

            TraceEnabled = EnableTrace.Value;

            SWL($"=== REPO_Active_Probe 0.3.6 Awake ===");
            SWL($"Unity={Application.unityVersion}");
            SWL($"Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            SWL($"MainTID={MainThreadId}");
            SWL($"TraceEnabled={TraceEnabled} TraceSeconds={TraceSeconds.Value}");
            SWL($"Keys: F1={KeyMark.Value} F2={KeyF2_ButtonPress.Value} F3={KeyF3_OnClick.Value} F4={KeyTraceToggle.Value}");

            try
            {
                _harmony = new Harmony("GalaxyBell.REPO_Active_Probe.Harmony");
                PatchExtractionPoint();
                PatchTraceCandidates(); // safe: prefixes early-return unless TraceActive
                SWL("=== Probe ready ===");
            }
            catch (Exception e)
            {
                SWL("[ERR] Harmony patch failed: " + e);
            }
        }

        private void OnDestroy()
        {
            try
            {
                _harmony?.UnpatchSelf();
                SWL("=== OnDestroy: Unpatched ===");
            }
            catch { }
        }

        private void Update()
        {
            if (!StaticLogEnabled) return;

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
                TraceEnabled = !TraceEnabled;
                SWL($"[{Now()}] [TRACE] TraceEnabled={TraceEnabled}");
            }

            if (Input.GetKeyDown(KeyF2_ButtonPress.Value))
            {
                SWL($"\n===== FORCE TEST (F2 ButtonPress) {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====");
                ForceInvokeNearest("ButtonPress");
            }

            if (Input.GetKeyDown(KeyF3_OnClick.Value))
            {
                SWL($"\n===== FORCE TEST (F3 OnClick) {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====");

                // open trace window for a short time (only if trace enabled)
                if (TraceEnabled)
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
                var eps = FindObjectsOfType<MonoBehaviour>()
                    .Where(mb => mb != null && mb.GetType().Name == "ExtractionPoint")
                    .ToList();

                SWL($"[{Now()}] [FORCE] ExtractionPoint objects={eps.Count}");

                if (eps.Count == 0)
                {
                    SWL($"[{Now()}] [FORCE] No ExtractionPoint found.");
                    return;
                }

                Vector3 refPos = GetReferencePos();
                MonoBehaviour nearest = null;
                float best = float.MaxValue;

                foreach (var ep in eps)
                {
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

                SWL($"[{Now()}] [FORCE] Invoke: {methodName}()");
                m.Invoke(nearest, null);
                SWL($"[{Now()}] [FORCE] Invoke done.");
            }
            catch (Exception e)
            {
                SWL($"[{Now()}] [ERR] ForceInvokeNearest({methodName}) failed: {e}");
            }
        }

        private Vector3 GetReferencePos()
        {
            // safest on main thread
            try
            {
                // prefer player camera
                if (Camera.main != null) return Camera.main.transform.position;
            }
            catch { }

            return Vector3.zero;
        }

        // ----------------- patches -----------------

        private void PatchExtractionPoint()
        {
            // We don't reference game type at compile time; locate by name in loaded assemblies.
            var epType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t != null && t.Name == "ExtractionPoint");

            if (epType == null)
            {
                SWL("[REF] ExtractionPoint type NOT FOUND.");
                return;
            }

            var mButtonPress = epType.GetMethod("ButtonPress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mOnClick = epType.GetMethod("OnClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mStateSet = epType.GetMethod("StateSet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mStateSetRPC = epType.GetMethod("StateSetRPC", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mHaulGoal = epType.GetMethod("HaulGoalSetRPC", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mSurplus = epType.GetMethod("ExtractionPointSurplusRPC", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mDeny = epType.GetMethod("ButtonDenyRPC", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mAuto = epType.GetMethod("ActivateTheFirstExtractionPointAutomaticallyWhenAPlayerLeaveTruck", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            SWL($"[REF] ExtractionPoint type={epType.FullName} ButtonPress={(mButtonPress != null)} OnClick={(mOnClick != null)}");

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
                    SWL("[PATCH] SKIP " + label + " (missing)");
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

        // patch a small set of candidate methods that likely relate to:
        // map markers / white dots / UI toasts / money displays
        // IMPORTANT: prefix returns immediately unless TraceActive == true
        private void PatchTraceCandidates()
        {
            int patched = 0;
            int considered = 0;

            var keywords = new[]
            {
                "Map","MiniMap","Minimap","Marker","Waypoint","Ping","Indicator","Compass",
                "Toast","Popup","Notify","Notification","Money","Value","Reward","Price",
                "Extraction","Haul","Goal"
            };

            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                SWL("[TRACE] Assembly-CSharp not found.");
                return;
            }

            Type[] types;
            try { types = asm.GetTypes(); } catch { types = Array.Empty<Type>(); }

            foreach (var t in types)
            {
                if (t == null) continue;

                MethodInfo[] ms;
                try { ms = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); } catch { continue; }

                foreach (var m in ms)
                {
                    if (m == null) continue;
                    considered++;

                    // skip property getters/setters and compiler gen
                    if (m.IsSpecialName) continue;
                    if (m.DeclaringType == null) continue;

                    var name = m.Name ?? "";
                    var typeName = m.DeclaringType.Name ?? "";

                    bool hit = keywords.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0
                                              || typeName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!hit) continue;

                    // avoid patching too many
                    if (patched >= 160) break;

                    try
                    {
                        var pre = new HarmonyMethod(typeof(Patches), nameof(Patches.TracePrefix));
                        _harmony.Patch(m, prefix: pre);
                        patched++;
                    }
                    catch { }
                }

                if (patched >= 160) break;
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
                if (!StaticLogEnabled) return;
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

        // ----------------- patches impl -----------------

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
                        SWL($"[{Now()}] [EP][T{Tid()}] PRE {tName}.{mName} argsLen={( __args==null ? 0 : __args.Length)}");
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
                    SWL($"[{Now()}] [EP][T{Tid()}] PRE {tName}.{mName} go={goName} pos={pos} argsLen={( __args==null ? 0 : __args.Length)} args={args}");

                    // stack origin dump for ButtonPress / OnClick only
                    if (EnableStacksSafe() && (mName == "ButtonPress" || mName == "OnClick"))
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

            // Trace patched methods: log ONLY inside trace window.
            public static void TracePrefix(MethodBase __originalMethod)
            {
                try
                {
                    if (!TraceActive) return;

                    string mName = __originalMethod?.Name ?? "nullMethod";
                    string tName = __originalMethod?.DeclaringType?.FullName ?? "nullType";
                    // keep it short
                    SWL($"[{Now()}] [TR][T{Tid()}] {tName}.{mName}");
                }
                catch { }
            }

            private static string DumpArgs(object[] args)
            {
                try
                {
                    if (args == null || args.Length == 0) return "[]";
                    // keep it short; avoid deep object ToString on weird Unity objects
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
                    // determine origin
                    string origin = stack.Contains("RuntimeMethodInfo.Invoke") || stack.Contains("MethodBase.Invoke")
                        ? "REFLECT"
                        : "NATIVE";

                    SWL($"[{Now()}] [EP][T{Tid()}] STACK {method} origin={origin}");
                    // dump stack with indentation, but not too huge
                    var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(40);
                    foreach (var l in lines)
                        SWL("      " + l);
                }
                catch { }
            }

            private static bool EnableStacksSafe()
            {
                try
                {
                    // config is instance-bound; simplest: always true here because user can disable in cfg if needed
                    // (we keep it stable to avoid null refs)
                    return true;
                }
                catch { return true; }
            }
        }
    }
}