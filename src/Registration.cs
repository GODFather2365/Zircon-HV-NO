using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  /// <summary>
  /// Coroutine-driven bootstrap: waits until Blueprinter + Multi-Missile are loaded,
  /// then clones the prefab and registers it (R2 mitigation).
  /// </summary>
  public class Runner : MonoBehaviour {
    public static Runner Instance;
    int attempts;
    bool done;

    void Awake() {
      if (Instance != null && Instance != this) { Destroy(gameObject); return; }
      Instance = this;
      DontDestroyOnLoad(gameObject);
    }

    public void Start() { StartCoroutine(Run()); }

    IEnumerator Run() {
      var mmWait = new WaitForSeconds(2f);
      // wait for Multi-Missile assembly to appear (BepInEx load order safety)
      while (CloneSource.FindMultiMissile() == null && attempts++ < 30) yield return mmWait;
      if (CloneSource.FindMultiMissile() == null) {
        Plugin.Log.LogWarning("Multi-Missile not found — disabled, no crash.");
        yield break;
      }
      // wait for Blueprinter types to be available
      attempts = 0;
      while (Registration.BlueprinterReady() == null && attempts++ < 30) yield return mmWait;

      string how;
      var prefab = CloneSource.LoadPrefab(out how);
      if ((object)prefab == null) { Plugin.Log.LogError("No source prefab: " + how); yield break; }
      Plugin.Log.LogInfo("Source prefab acquired via " + how);

      var clone = Instantiate(prefab);
      clone.name = Plugin.UniqueId;
      try { DontDestroyOnLoad(clone); } catch { }
      clone.SetActive(false);

      Registration.Apply(clone, prefab);
      done = true;
    }
  }

  public static class Registration {
    static readonly string[] RegCandidates = {
      "Blueprinter.WeaponRegistry", "Blueprinter.Registry", "Blueprinter.BlueprintRegistry",
      "Blueprinter.Mods.ModRegistry", "Blueprinter.Data.WeaponDatabase", "Blueprinter.PatchRunner"
    };
    static readonly string[] IdFieldNames = { "id", "jsonKey", "weaponId", "key", "name", "techName" };

    /// <summary>Returns the first found Blueprinter-ish type or null (used as readiness probe).</summary>
    public static Type BlueprinterReady() {
      foreach (var c in RegCandidates) {
        var t = Reflection.FindType(c);
        if ((object)t != null) return t;
      }
      return Reflection.FindType("Blueprinter"); // any namespace hit
    }

    public static void Apply(GameObject clone, GameObject original) {
      // 1. unique id rewrite on every component of the clone (R4)
      string oldId = null;
      foreach (var comp in clone.GetComponents<Component>()) {
        if (comp == null) continue;
        foreach (var n in IdFieldNames) {
          var v = Reflection.FieldOrProp(comp, n) as string;
          if (!string.IsNullOrEmpty(v)) { oldId = v; break; }
        }
        if (oldId != null) break;
      }
      int rewritten = 0;
      foreach (var comp in clone.GetComponentsInChildren<Component>(true)) {
        if (comp == null) continue;
        if (oldId != null) rewritten += Reflection.RewriteStringIds(comp, oldId, Plugin.UniqueId, 4);
        foreach (var n in IdFieldNames) Reflection.SetDeep(comp, n, Plugin.UniqueId);
      }
      Plugin.Log.LogInfo("id rewrite: old='" + oldId + "' -> '" + Plugin.UniqueId + "' (" + rewritten + " fields)");

      // 2. warhead scaling from YieldKt
      Warhead.Scale(clone);

      // 3. encyclopedia / UI name patch (clone only)
      Encyclopedia.Patch(clone);

      // 4. external mesh hook (Phase 2 stub)
      MeshSwap.TryApplyExternalMesh(clone);

      // 5. honest registration attempt
      if (!TryRegister(clone)) {
        if (Plugin.EnableTwinFallback.Value) {
          Plugin.Log.LogWarning("Honest registration failed -> EventGate twin fallback (F1). Weapon appears as a warhead variant of the carrier, single UI entry.");
          TwinFallback(clone);
        } else if (Plugin.OverrideOriginal.Value) {
          Plugin.Log.LogWarning("F3 active: patching ORIGINAL zircon nuke instead (conflicts by design).");
        } else {
          Plugin.Log.LogError("Clone built but NOT registered. Enable TwinFallback or OverrideOriginal in config.");
        }
      }
    }

    static bool TryRegister(GameObject clone) {
      foreach (var name in RegCandidates) {
        var t = Reflection.FindType(name);
        if ((object)t == null) continue;
        // a) static Register*/Add* methods taking GameObject / Type / string
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
          if (!(m.Name.StartsWith("Register", StringComparison.OrdinalIgnoreCase) ||
                m.Name.StartsWith("Add", StringComparison.OrdinalIgnoreCase))) continue;
          var ps = m.GetParameters();
          if (ps.Length == 0 || ps.Length > 3) continue;
          object[] args = BuildArgs(ps, clone);
          if (args == null) continue;
          try { m.Invoke(null, args); Plugin.Log.LogInfo("registered via " + t.FullName + "." + m.Name); return true; }
          catch (Exception e) { Plugin.Log.LogWarning(m.Name + " failed: " + e.Message); }
        }
        // b) static collection field (List/Dictionary) — add raw entry
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
          var col = f.GetValue(null) as IList;
          if (col != null) { try { col.Add(clone); Plugin.Log.LogInfo("added to list " + f.Name); return true; } catch { } }
          var dic = f.GetValue(null) as IDictionary;
          if (dic != null && !dic.Contains(Plugin.UniqueId)) {
            try { dic[Plugin.UniqueId] = clone; Plugin.Log.LogInfo("added to dict " + f.Name); return true; } catch { }
          }
        }
      }
      return false;
    }

    static object[] BuildArgs(ParameterInfo[] ps, GameObject clone) {
      var args = new object[ps.Length];
      for (int i = 0; i < ps.Length; i++) {
        var pt = ps[i].ParameterType;
        if (pt == typeof(GameObject)) args[i] = clone;
        else if (pt == typeof(string)) args[i] = Plugin.UniqueId;
        else if (pt == typeof(Type)) args[i] = typeof(GameObject);
        else if (pt.IsValueType && HasDefaultCtor(pt)) args[i] = Activator.CreateInstance(pt);
        else if (i == 0 && pt.IsAssignableFrom(typeof(GameObject))) args[i] = clone;
        else return null; // cannot synthesize this parameter honestly
      }
      return args;
    }

    static bool HasDefaultCtor(Type t) { return t.GetConstructor(Type.EmptyTypes) != null; }

    /// <summary>F1: Meridian-Works-style event twins — reuse EventGate.BuildTwins if present.</summary>
    static void TwinFallback(GameObject clone) {
      var gate = Reflection.FindType("EventGate", "MeridianWorks.EventGate");
      if ((object)gate == null) { Plugin.Log.LogError("EventGate type not found; F1 unavailable."); return; }
      var r = Reflection.CallStatic(gate, "BuildTwins", clone);
      Plugin.Log.LogInfo("BuildTwins result: " + (r == null ? "null" : r.ToString()));
    }
  }
}
