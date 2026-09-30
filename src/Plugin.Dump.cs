using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  public partial class Plugin {
    void DumpBlueprinterApi() {
      var path = Path.Combine(BepInEx.Paths.PluginPath, "zirconhv_apidump_full.txt");
      using (var w = new StreamWriter(path, false)) {
        w.WriteLine("========== FULL API DUMP ==========");
        w.WriteLine("Game version: " + Application.version);
        w.WriteLine("Unity version: " + Application.unityVersion);
        
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) {
          string an; 
          try { an = a.GetName().Name; } catch { continue; }
          
          // Dump Blueprinter types
          if (an.IndexOf("blueprinter", StringComparison.OrdinalIgnoreCase) >= 0) {
            w.WriteLine("\n\n===== ASSEMBLY: " + an + " =====");
            DumpAssembly(w, a, true);
          }
          
          // Dump Assembly-CSharp weapon types
          if (an == "Assembly-CSharp") {
            w.WriteLine("\n\n===== ASSEMBLY: Assembly-CSharp (WEAPON TYPES) =====");
            var targetTypes = new[] {
              "WeaponMount", "MissileDefinition", "WeaponInfo", "Encyclopedia",
              "Hardpoint", "HardpointSet", "Aircraft", "WeaponManager", "WeaponStation",
              "Missile", "MountedMissile", "INetworkDefinition", "UnitDefinition",
              "Warhead", "NuclearWarhead", "BlastWarhead"
            };
            foreach (var typeName in targetTypes) {
              var t = a.GetType(typeName) 
                   ?? a.GetType("NuclearOption." + typeName) 
                   ?? a.GetType("NuclearOption.Weapons." + typeName)
                   ?? a.GetType("NuclearOption.Units." + typeName);
              if (t != null) DumpType(w, t);
            }
          }
        }
      }
      Log.LogInfo("FULL API dump written to " + path);
    }

    void DumpAssembly(StreamWriter w, Assembly a, bool filterBlueprinter) {
      Type[] ts;
      try { ts = a.GetTypes(); }
      catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray(); }
      catch { return; }
      foreach (var t in ts) {
        if (!filterBlueprinter || (t.Namespace != null && t.Namespace.IndexOf("Blueprinter", StringComparison.OrdinalIgnoreCase) >= 0)) {
          DumpType(w, t);
        }
      }
    }

    void DumpType(StreamWriter w, Type t) {
      w.WriteLine("\n== " + t.Assembly.GetName().Name + " :: " + t.FullName);
      if (t.BaseType != null) w.WriteLine("   BASE: " + t.BaseType.FullName);
      foreach (var i in t.GetInterfaces()) {
        if (i.Namespace != null && (i.Namespace.Contains("NuclearOption") || i.Namespace.Contains("Blueprinter")))
          w.WriteLine("   IMPL: " + i.FullName);
      }
      foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        w.WriteLine("   F " + f.FieldType.Name + " " + f.Name + (f.IsPublic ? " [pub]" : " [priv]"));
      foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        w.WriteLine("   P " + p.PropertyType.Name + " " + p.Name);
      foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        w.WriteLine("   M " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name).ToArray()) + ")");
    }
  }
}
