using System;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  /// <summary>
  /// Yield scaling for the cloned warhead (Tsar-Bomba-style runtime patch, no Harmony needed).
  /// Radii scale ~ yield^(1/3); raw yield/damage fields scale linearly.
  /// </summary>
  public static class Warhead {
    // assumed original zircon nuke yield in kt — used only as the reference point for the ratio
    const float OrigYieldKt = 100f;

    static readonly string[] RadiusNeedles = { "blast", "explos", "rang", "thermal", "heat",
      "radiat", "fallout", "emp", "numpulse", "damage", "radius" };
    static readonly string[] LinearNeedles = { "yield", "kt", "kiloton", "megaton" };

    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // aliases used by the rewritten Runner/Registration
    public static void ApplyTo(GameObject clone) { if (clone != null) Scale(clone); }

    /// <summary>
    /// v3: MissileDefinition has field blastYield (next to mass, finArea).
    /// Rule: blastYield = orig * (YieldKt / origYieldKt); if orig reads 0 ->
    /// set blastYield = YieldKt * 1000 and log the value actually set.
    /// </summary>
    public static void ApplyToBlastYield(object def) {
      if (def == null) return;
      var t = def.GetType();
      var f = Refl.Field(t, "blastYield");
      if (f == null || (f.FieldType != typeof(float) && f.FieldType != typeof(double))) {
        Plugin.Log.LogError("ZirconHV: поле blastYield (float/double) не найдено в " + t.FullName + ". Мини-дамп публичных членов:");
        Refl.DumpMembers(t);
        return;
      }
      float y = Plugin.YieldKt.Value;
      try {
        double orig = Convert.ToDouble(f.GetValue(def));
        double nv;
        if (orig > 0.0) {
          nv = orig * (double)(y / OrigYieldKt);
          Plugin.Log.LogInfo(string.Format("ZirconHV: blastYield {0} -> {1} (orig*YieldKt/{2})", orig, nv, OrigYieldKt));
        } else {
          nv = (double)y * 1000.0;
          Plugin.Log.LogInfo(string.Format("ZirconHV: blastYield читается как {0} (0) -> ставлю YieldKt*1000 = {1}", orig, nv));
        }
        f.SetValue(def, f.FieldType == typeof(float) ? (object)(float)nv : (object)nv);
        Plugin.Log.LogInfo("ZirconHV: blastYield установлен = " + f.GetValue(def) + " (" + f.FieldType.Name + ")");
      } catch (Exception e) {
        Plugin.Log.LogError("ZirconHV: не смог прочитать/записать blastYield: " + e.Message);
      }
    }

    public static void ApplyTo(object componentOrDefinition) {
      if (componentOrDefinition == null) return;
      float y = Plugin.YieldKt.Value;
      if (y <= 0f) return;
      float k = Mathf.Pow(y / OrigYieldKt, 1f / 3f);
      float lin = y / OrigYieldKt;
      int touched = 0;
      var t = componentOrDefinition.GetType();
      foreach (var f in t.GetFields(All)) {
        if (f.IsStatic) continue;
        if (f.FieldType != typeof(float) && f.FieldType != typeof(double)
            && f.FieldType != typeof(int)) continue;
        var ln = f.Name.ToLowerInvariant();
        bool isRadius = false, isLinear = false;
        foreach (var r in RadiusNeedles) if (ln.Contains(r)) { isRadius = true; break; }
        if (!isRadius) foreach (var r in LinearNeedles) if (ln.Contains(r)) { isLinear = true; break; }
        if (!isRadius && !isLinear) continue;
        try {
          double v = Convert.ToDouble(f.GetValue(componentOrDefinition));
          double nv = isLinear ? v * lin : v * k;
          f.SetValue(componentOrDefinition, f.FieldType == typeof(int) ? (object)(int)nv
                        : f.FieldType == typeof(float) ? (object)(float)nv : (object)nv);
          touched++;
        } catch { }
      }
      Plugin.Log.LogInfo("warhead scaled on " + t.Name + ": fields touched=" + touched);
    }

    public static void Scale(GameObject clone) {
      float y = Plugin.YieldKt.Value;
      if (y <= 0f) { Plugin.Log.LogWarning("YieldKt<=0, skipping warhead scaling."); return; }
      float k = Mathf.Pow(y / OrigYieldKt, 1f / 3f);   // radii scale
      float lin = y / OrigYieldKt;                    // yield/damage-linear scale
      int touched = 0;
      foreach (var comp in clone.GetComponentsInChildren<Component>(true)) {
        if (comp == null) continue;
        var t = comp.GetType();
        foreach (var f in t.GetFields(All)) {
          if (f.FieldType != typeof(float) && f.FieldType != typeof(double)) continue;
          var ln = f.Name.ToLowerInvariant();
          bool isRadius = false, isLinear = false;
          foreach (var r in RadiusNeedles) if (ln.Contains(r)) { isRadius = true; break; }
          foreach (var r in LinearNeedles) if (ln.Contains(r)) { isLinear = true; break; }
          if (!isRadius && !isLinear) continue;
          try {
            object cur = f.GetValue(comp);
            double v = Convert.ToDouble(cur);
            double nv = isLinear ? v * lin : v * k;
            f.SetValue(comp, f.FieldType == typeof(float) ? (object)(float)nv : (object)nv);
            touched++;
          } catch { }
        }
      }
      Plugin.Log.LogInfo(string.Format("warhead scaled: yield={0} kt, radiusK={1:F3}, linearK={2:F3}, fields touched={3}",
        y, k, lin, touched));
    }
  }
}
