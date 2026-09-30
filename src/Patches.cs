using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace ZirconHV {
  /// <summary>
  /// v3: Harmony postfix on Encyclopedia.AfterLoad (Tsar Bomba / Chinese-mod pattern,
  /// visible as DMD [Encyclopedia::AfterLoad] in Player.log). Keeps a static reference
  /// to the freshly loaded Encyclopedia instance so Registration can use it immediately.
  /// Patch is applied via reflection (AccessTools + HarmonyPatch) so this assembly has
  /// NO compile-time dependency on Assembly-CSharp (Kestrel-40 TypeLoadException lesson).
  /// </summary>
  [HarmonyPatch]
  public static class EncyclopediaPatches {
    public static ManualLogSource Log => Plugin.Log;

    /// <summary>Last known live Encyclopedia instance (set by postfix, read by Runner).</summary>
    public static object LastInstance;

    static MethodBase TargetMethod() {
      var t = Refl.FindTypeInCSharp("Encyclopedia");
      if (t == null) return null;
      var m = Refl.Method(t, "AfterLoad");
      if (m == null) Plugin.Log.LogError("ZirconHV: метод Encyclopedia.AfterLoad не найден — патч не применён.");
      return m;
    }

    // Postfix signature matched dynamically: we accept __instance as object.
    // Works whether AfterLoad is void or returns something (return value untouched).
    public static void Postfix(object __instance) {
      try {
        LastInstance = __instance;
        Plugin.Log.LogInfo("ZirconHV: Encyclopedia.AfterLoad postfix сработал, __instance = " +
          (__instance == null ? "null" : AccessToolsTypeName(__instance)));
      } catch (Exception e) {
        Plugin.Log.LogError("ZirconHV: ошибка в postfix: " + e.Message);
      }
    }

    static string AccessToolsTypeName(object o) {
      try { return ((UnityEngine.Object)o).name + " (" + o.GetType().FullName + ")"; }
      catch { return o.GetType().FullName; }
    }

    /// <summary>Apply the AfterLoad patch. Safe to call multiple times.</summary>
    public static void Apply() {
      try {
        var harm = new HarmonyLib.Harmony(Plugin.GUID + ".encyclopedia");
        harm.PatchAll(typeof(EncyclopediaPatches));
        Plugin.Log.LogInfo("ZirconHV: Harmony-patch на Encyclopedia.AfterLoad применён.");
      } catch (Exception e) {
        Plugin.Log.LogWarning("ZirconHV: не удалось применить патч Encyclopedia.AfterLoad (" + e.Message +
          ") — остаётся фолбэк-поллинг Instance/FindObjectOfType.");
      }
    }
  }
}
