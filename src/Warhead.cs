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
