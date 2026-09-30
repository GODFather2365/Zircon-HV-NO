using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  public partial class Plugin {
    // Debug helper: writes every Blueprinter/Multi-Missile-ish type and its members to
    // BepInEx/LogOutput.log so the author can confirm real API names without dnSpy.
    void DumpBlueprinterApi() {
      var path = Path.Combine(BepInEx.Paths.PluginPath, "zirconhv_apidump.txt");
      using (var w = new StreamWriter(path, false)) {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) {
          string an; try { an = a.GetName().Name; } catch { continue; }
          if (an.IndexOf("blueprinter", StringComparison.OrdinalIgnoreCase) < 0 &&
              an.IndexOf("missile", StringComparison.OrdinalIgnoreCase) < 0 &&
              an.IndexOf("meridian", StringComparison.OrdinalIgnoreCase) < 0 &&
              an != "Assembly-CSharp") continue;
          Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray(); } catch { continue; }
          foreach (var t in ts) {
            if (t.Namespace == null) continue;
            bool hit = t.Namespace.IndexOf("lueprinter", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.Namespace.IndexOf("Multi", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.Name.IndexOf("Registry", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.Name.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.Name.Contains("EventGate");
            if (!hit) continue;
            w.WriteLine("== " + a.GetName().Name + " :: " + t.FullName);
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                w.WriteLine("   M " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), x => x.ParameterType.Name + " " + x.Name)) + ")");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                w.WriteLine("   F " + f.FieldType.Name + " " + f.Name);
            foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                w.WriteLine("   P " + pr.PropertyType.Name + " " + pr.Name);
          }
        }
      }
      Log.LogInfo("API dump written to " + path);
    }
  }
}
