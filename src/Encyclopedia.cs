using System;
using UnityEngine;

namespace ZirconHV {
  /// <summary>
  /// Clone-only UI/Encyclopedia naming via reflection (no Harmony patches on foreign methods -> R3-safe).
  /// </summary>
  public static class Encyclopedia {
    static readonly string[] NameNeedles = { "displayName", "techName", "entryName", "weaponName", "title" };
    static readonly string[] DescNeedles  = { "description", "desc", "encyclopediaText", "info" };

    public static void Patch(GameObject clone) {
      float y = Plugin.YieldKt.Value;
      string disp = y >= 1000f ? string.Format("Zircon HV ({0:0.#} Mt)", y / 1000f)
                               : string.Format("Zircon HV ({0:0.#} kt)", y);
      string desc = "Heavy zirconium variant. Yield " + y + " kt. Blast/thermal/radiation/EMP radii scale ~ yield^(1/3).";
      int set = 0;
      foreach (var comp in clone.GetComponentsInChildren<Component>(true)) {
        if (comp == null) continue;
        foreach (var n in NameNeedles) if (Refl.SetDeep(comp, n, disp)) set++;
        foreach (var n in DescNeedles) if (Refl.SetDeep(comp, n, desc)) set++;
      }
      Plugin.Log.LogInfo("encyclopedia/UI name patched on " + set + " fields (" + disp + ")");
    }
  }
}
