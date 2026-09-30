using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  /// <summary>
  /// Locates and loads the "zircon nuke" prefab from Multi-Missile:
  ///   1) already-loaded AssetBundle (R6 mitigation),
  ///   2) .nobp file next to the MM plugin DLL,
  ///   3) embedded resource stream inside Multi-Missile.dll (resource:&lt;Assembly&gt;:&lt;path&gt;.nobp).
  /// No compile-time reference to Multi-Missile types (R3).
  /// </summary>
  public static class CloneSource {
    const string FuzzyNeedle = "zircon nuke";

    public static Assembly FindMultiMissile() {
      foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) {
        try {
          var n = a.GetName().Name ?? "";
          if (n.IndexOf("multi", StringComparison.OrdinalIgnoreCase) >= 0 &&
              n.IndexOf("missile", StringComparison.OrdinalIgnoreCase) >= 0) return a;
          if (string.Equals(n, "MultiMissile", StringComparison.OrdinalIgnoreCase)) return a;
          if (a.FullName.Contains("kingwixly")) return a;
        } catch { }
      }
      return null;
    }

    public static GameObject LoadPrefab(out string how) {
      how = null;
      var mm = FindMultiMissile();
      if ((object)mm == null) { how = "Multi-Missile assembly not found"; return null; }

      // --- 1) already loaded bundles (Blueprinter BundleRegistry may have loaded it) ---
      foreach (object oab in AllLoadedBundles()) {
        var ab = (AssetBundle)oab;
        var go = TryLoadFromBundle(ab);
        if ((object)go != null) { how = "already-loaded bundle " + ab.name; return go; }
      }

      // --- 2) .nobp on disk near the MM dll / plugins dir ---
      foreach (var f in EnumerateNobp(mm)) {
        AssetBundle ab = null;
        try { ab = AssetBundle.LoadFromFile(f); } catch (Exception e) { Plugin.Log.LogWarning("LoadFromFile failed for " + f + ": " + e.Message); }
        if ((object)ab == null) continue;
        LoadDepsFirst(ab); // R7
        var go = TryLoadFromBundle(ab);
        if ((object)go != null) { how = "file " + Path.GetFileName(f); return go; }
        try { ab.Unload(false); } catch { }
      }

      // --- 3) embedded resource stream inside Multi-Missile.dll ---
      foreach (var res in mm.GetManifestResourceNames().Where(r => r.EndsWith(".nobp", StringComparison.OrdinalIgnoreCase))) {
        try {
          byte[] bytes;
          using (var s = mm.GetManifestResourceStream(res))
          using (var ms = new MemoryStream()) { Copy(s, ms); bytes = ms.ToArray(); }
          var ab2 = AssetBundle.LoadFromMemory(bytes);
          if ((object)ab2 == null) continue;
          var go2 = TryLoadFromBundle(ab2);
          if ((object)go2 != null) { how = "resource " + res; return go2; }
          try { ab2.Unload(false); } catch { }
        } catch (Exception e) { Plugin.Log.LogWarning("resource load failed " + res + ": " + e.Message); }
      }

      how = "prefab not found in any source";
      return null;
    }

    static void Copy(Stream src, Stream dst) {
      var buf = new byte[8192]; int n;
      while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
    }

    static System.Collections.IEnumerable AllLoadedBundles() {
      var m = typeof(AssetBundle).GetMethod("GetAllLoadedAssetBundles",
            BindingFlags.Public | BindingFlags.Static);
      if ((object)m == null) yield break;
      System.Collections.IEnumerable r = null;
      try { r = m.Invoke(null, null) as System.Collections.IEnumerable; } catch { }
      if (r == null) yield break;
      foreach (object o in r) if (o is AssetBundle) yield return (AssetBundle)o;
    }

    static IEnumerable<string> EnumerateNobp(Assembly mm) {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      string baseDir = null;
      try { baseDir = Path.GetDirectoryName(mm.Location); } catch { }
      var roots = new List<string>();
      if (!string.IsNullOrEmpty(baseDir)) roots.Add(baseDir);
      try { roots.Add(BepInEx.Paths.PluginPath); } catch { }
      foreach (var root in roots) {
        if (!Directory.Exists(root)) continue;
        IEnumerable<string> files;
        try { files = Directory.GetFiles(root, "*.nobp", SearchOption.AllDirectories); } catch { continue; }
        foreach (var f in files) {
          if (f.IndexOf("missile", StringComparison.OrdinalIgnoreCase) >= 0 ||
              f.IndexOf("zircon", StringComparison.OrdinalIgnoreCase) >= 0) {
            if (seen.Add(f)) yield return f;
          } else if (seen.Add(f)) { /* keep as generic fallback candidate */ }
        }
        // also generic pass
        foreach (var f in files) if (seen.Add(f)) yield return f;
      }
    }

    static void LoadDepsFirst(AssetBundle ab) {
      try {
        // net35 facade lacks the convenience overload -> call it reflectively (it exists in the runtime).
        var gm = typeof(AssetBundle).GetMethod("GetAllDependencies", BindingFlags.Public | BindingFlags.Static);
        if ((object)gm == null) return;
        string[] deps = null;
        try { deps = gm.Invoke(null, new object[] { ab.name }) as string[]; } catch { }
        if (deps == null || deps.Length == 0) return;
        Plugin.Log.LogInfo("bundle deps: " + string.Join(", ", deps));
      } catch { }
    }

    static GameObject TryLoadFromBundle(AssetBundle ab) {
      if ((object)ab == null) return null;
      // exact configured path first
      var exact = Plugin.PrefabPath.Value;
      var go = ab.LoadAsset<GameObject>(exact);
      if ((object)go != null) return go;
      // fuzzy: normalized suffix match
      string[] names = null;
      try { names = ab.GetAllAssetNames(); } catch { }
      if (names == null) return null;
      foreach (var n in names) {
        var nn = n.Replace('\\', '/').ToLowerInvariant();
        if (nn.EndsWith("/" + FuzzyNeedle) || nn.EndsWith("/" + FuzzyNeedle + ".prefab") ||
            nn.Contains(FuzzyNeedle)) {
          var g = ab.LoadAsset<GameObject>(n);
          if ((object)g != null) return g;
        }
      }
      return null;
    }

    // v2 entry point: load the zircon nuke prefab from a bundle already found via BundleRegistry
    public static GameObject LoadPrefab(AssetBundle ab) {
      Plugin.Log.LogInfo("Ищу префаб zircon nuke внутри бандла \"" + (ab != null ? ab.name : "?") + "\"...");
      var go = LoadPrefabInto(ab);
      if (go == null) Plugin.Log.LogError("Префаб zircon nuke не найден в переданном бандле.");
      else Plugin.Log.LogInfo("Нашел префаб " + go.name + " в бандле.");
      return go;
    }

    static GameObject LoadPrefabInto(AssetBundle ab) { return TryLoadFromBundle(ab); }
  }
}
