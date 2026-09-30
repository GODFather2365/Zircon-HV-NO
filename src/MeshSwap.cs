using System;
using System.IO;
using UnityEngine;

namespace ZirconHV {
  /// <summary>
  /// PHASE 2 STUB — extension point for replacing the clone mesh with an external .obj.
  /// Planned pipeline (not implemented yet):
  ///   TODO Phase 2: ObjLoader.Parse(modFolder/"mesh/model.obj") -> Mesh,
  ///   Material via Shader.Find("Universal Render Pipeline/Lit"), textures via Texture2D.LoadImage.
  /// </summary>
  public static class MeshSwap {
    public static bool TryApplyExternalMesh(GameObject clone) {
      string p = null;
      try { p = Path.Combine(Path.Combine(Path.Combine(BepInEx.Paths.PluginPath, "zirconhv"), "mesh"), "model.obj"); } catch { }
      if (p == null || !File.Exists(p)) return false;   // stub: external mesh is optional
      Plugin.Log.LogWarning("External mesh found but Phase-2 OBJ pipeline is not implemented yet.");
      return false;
    }
  }
}
