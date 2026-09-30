using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  public static class Registration {
    const string Tag = "ZirconHV: ";
    public static string LastAssetName;     
    public static int LastInjectedSets;     
    public static string LastJsonKey;       
    public static string LastOpsResult = "не вызывалось"; 

    static void Info(string s) { Plugin.Log.LogInfo(Tag + s); }
    static void Warn(string s) { Plugin.Log.LogWarning(Tag + s); }
    static void Err(string s)  { Plugin.Log.LogError(Tag + s); }

    public static void RenameClone(GameObject clone, GameObject original) {
      Info("Назначаю клону уникальный ID...");
      foreach (var c in clone.GetComponentsInChildren<Component>(true)) {
        if (c == null) continue;
        var t = c.GetType();
        foreach (var f in t.GetFields(Refl.All)) {
          if (f.FieldType != typeof(string) || f.IsLiteral || f.IsStatic) continue;
          string v = null; try { v = (string)f.GetValue(c); } catch { continue; }
          if (v == null) continue;
          if (IsIdField(f.Name)) { try { f.SetValue(c, Plugin.UniqueId); } catch { } }
          else if (IsNameField(f.Name)) { try { f.SetValue(c, "Zircon HV (1 Mt)"); } catch { } }
        }
        foreach (var p in t.GetProperties(Refl.All)) {
          if (p.PropertyType != typeof(string) || !p.CanWrite || !IndexOk(p)) continue;
          string v = null; try { v = (string)p.GetValue(c, null); } catch { continue; }
          if (v == null) continue;
          if (IsIdField(p.Name)) { try { p.SetValue(c, Plugin.UniqueId, null); } catch { } }
          else if (IsNameField(p.Name)) { try { p.SetValue(c, "Zircon HV (1 Mt)", null); } catch { } }
        }
      }
    }

    static bool IsIdField(string n) {
      n = n.ToLowerInvariant();
      return n == "id" || n == "jsonkey" || n == "weaponid" || n == "key" || n == "techname" || n == "itemid" || n == "guid";
    }
    static bool IsNameField(string n) {
      n = n.ToLowerInvariant();
      return n == "name" || n == "displayname" || n == "weaponname" || n == "titlename";
    }
    static bool IndexOk(PropertyInfo p) {
      try { return p.GetIndexParameters().Length == 0; } catch { return false; }
    }

    public static bool RegisterAndInject(GameObject clone, object enc, object bundleMountSO, object bundleDefSO) {
      if (bundleMountSO == null || bundleDefSO == null) return false;
      object mountClone = Refl.CreateLike(bundleMountSO.GetType(), bundleMountSO);
      object defClone   = Refl.CreateLike(bundleDefSO.GetType(), bundleDefSO);
      if (mountClone == null || defClone == null) return false;
      
      Refl.SetDeep(mountClone, "jsonKey", CloneJsonKey);
      Refl.SetDeep(defClone, "jsonKey", CloneJsonKey);
      StampUnique(defClone);
      StampUnique(mountClone);
      
      try {
        if (!Refl.SetDeep(mountClone, "unitPrefab", clone)) Refl.SetDeep(mountClone, "prefab", clone);
      } catch { }
      
      foreach (var f in mountClone.GetType().GetFields(Refl.All)) {
        if (f.FieldType.IsInstanceOfType(defClone)) {
          try { f.SetValue(mountClone, defClone); } catch { }
        }
      }
      Warhead.ApplyToBlastYield(defClone);
      bool opsOk = RegisterViaOps(clone, enc, defClone, mountClone);
      bool anyOk = opsOk;
      if (!opsOk) {
        Type encType = Refl.FindTypeInCSharp("Encyclopedia");
        if (encType != null) {
          MethodInfo addM = FindAddMethod(encType, defClone.GetType(), mountClone.GetType());
          if (addM != null) {
            InvokeLoose(enc, addM, defClone, mountClone, clone);
            anyOk = true;
          } else {
            anyOk |= AddToCollection(enc, defClone) | AddToCollection(enc, mountClone);
          }
        }
      }
      bool injOk = InjectHardpointsV3(mountClone);
      anyOk |= injOk;
      if (anyOk && injOk) LastJsonKey = CloneJsonKey;
      return anyOk && injOk;
    }

    static bool RegisterViaOps(GameObject clone, object enc, object defClone, object mountClone) {
      Type loaderT = Refl.FindType("Blueprinter.Ops.EncyclopediaLoader") ?? Refl.FindTypeInCSharp("EncyclopediaLoader");
      if (loaderT == null) return false;
      object loader; try { loader = Activator.CreateInstance(loaderT, true); } catch { return false; }
      bool ok = false;
      var addMount = Refl.Method(loaderT, "AddWeaponMount");
      if (addMount != null && mountClone != null) if (InvokeTyped(loader, addMount, enc, mountClone)) ok = true;
      var addUnit = Refl.Method(loaderT, "AddUnit");
      if (addUnit != null && defClone != null) if (InvokeTyped(loader, addUnit, enc, defClone)) ok = true;
      LastOpsResult = ok ? "УСПЕХ" : "НЕТ";
      return ok;
    }

    static bool InvokeTyped(object target, MethodInfo m, params object[] candidates) {
      var ps = m.GetParameters(); var args = new object[ps.Length];
      for (int i = 0; i < ps.Length; i++) {
        bool found = false;
        foreach (var c in candidates) {
          if (c != null && ps[i].ParameterType.IsInstanceOfType(c)) { args[i] = c; found = true; break; }
        }
        if (!found) return false;
      }
      try { m.Invoke(m.IsStatic ? null : target, args); return true; } catch { return false; }
    }

    static bool InjectHardpointsV3(object mountClone) {
      if (mountClone == null) return false;
      Type hpT = Refl.FindTypeInCSharp("Hardpoint"); if (hpT == null) return false;
      FieldInfo mountF = hpT.GetField("mount", Refl.All);
      FieldInfo optsF  = hpT.GetField("pylonOptions", Refl.All);
      if (mountF == null || optsF == null) return false;
      Type optT = optsF.FieldType.IsArray ? optsF.FieldType.GetElementType() : null; if (optT == null) return false;
      FieldInfo optMountF  = optT.GetField("mount", Refl.All);
      FieldInfo optRenderF = optT.GetField("renderer", Refl.All);
      if (optMountF == null || optRenderF == null) return false;
      UnityEngine.Object[] hps = null; try { hps = UnityEngine.Resources.FindObjectsOfTypeAll(hpT); } catch { return false; }
      foreach (var o in hps) {
        if (o == null) continue;
        try {
          object hp = o; object curMount = mountF.GetValue(hp); if (curMount == null) continue;
          string wn = GetWeaponName(curMount); if (wn == null || wn.IndexOf("Zircon", StringComparison.OrdinalIgnoreCase) < 0) continue;
          Array opts = optsF.GetValue(hp) as Array; if (opts == null || opts.Length == 0) continue;
          bool already = false;
          for (int q = 0; q < opts.Length; q++) {
            if (ReferenceEquals(optMountF.GetValue(opts.GetValue(q)), mountClone)) { already = true; break; }
          }
          if (already) continue;
          object copy = ShallowCopyOption(opts.GetValue(opts.Length - 1), optT); if (copy == null) continue;
          optMountF.SetValue(copy, mountClone); optRenderF.SetValue(copy, null);
          var newArr = Array.CreateInstance(optT, opts.Length + 1);
          for (int q = 0; q < opts.Length; q++) newArr.SetValue(opts.GetValue(q), q);
          newArr.SetValue(copy, opts.Length); optsF.SetValue(hp, newArr);
        } catch { }
      }
      return injDone = true;
    }

    static bool injDone;

    static string GetWeaponName(object mount) {
      if (mount == null) return null;
      try {
        object info = Refl.FieldOrProp(mount, "info");
        if (info != null) {
          var wn = Refl.FieldOrProp(info, "weaponName") as string; if (wn != null) return wn;
        }
        return Refl.FieldOrProp(mount, "weaponName") as string;
      } catch { return null; }
    }

    static object ShallowCopyOption(object src, Type optT) {
      try {
        object copy = Activator.CreateInstance(optT, true);
        foreach (var f in optT.GetFields(Refl.All)) if (!f.IsLiteral) f.SetValue(copy, f.GetValue(src));
        return copy;
      } catch { return null; }
    }

    public const string CloneJsonKey = "ZirconHV_1Mt";

    public static void RewriteJsonKey(GameObject clone, object bundleMountSO) {
      string oldKey = FindJsonKey(clone, bundleMountSO);
      var targets = new List<object>();
      if (clone != null) targets.AddRange(clone.GetComponentsInChildren<Component>(true));
      if (bundleMountSO != null) targets.Add(bundleMountSO);
      foreach (var t0 in targets) {
        if (t0 == null) continue;
        Type t = t0.GetType();
        foreach (var f in t.GetFields(Refl.All)) {
          if (f.FieldType != typeof(string) || f.IsStatic || f.IsLiteral) continue;
          string v = null; try { v = (string)f.GetValue(t0); } catch { continue; }
          if (v == null) continue;
          bool hit = string.Equals(f.Name, "jsonKey", StringComparison.OrdinalIgnoreCase) || (oldKey != null && v == oldKey);
          if (hit) try { f.SetValue(t0, CloneJsonKey); } catch { }
        }
        foreach (var pr in t.GetProperties(Refl.All)) {
          if (pr.PropertyType != typeof(string) || !pr.CanWrite) continue;
          try { if (pr.GetIndexParameters().Length != 0) continue; } catch { continue; }
          string v = null; try { v = (string)pr.GetValue(t0, null); } catch { continue; }
          if (v == null) continue;
          bool hit = string.Equals(pr.Name, "jsonKey", StringComparison.OrdinalIgnoreCase) || (oldKey != null && v == oldKey);
          if (hit) try { pr.SetValue(t0, CloneJsonKey, null); } catch { }
        }
      }
      LastJsonKey = CloneJsonKey;
    }

    static string FindJsonKey(GameObject clone, object bundleMountSO) {
      var probes = new List<object>();
      if (bundleMountSO != null) probes.Add(bundleMountSO);
      if (clone != null) probes.AddRange(clone.GetComponentsInChildren<Component>(true));
      foreach (var o in probes) {
        if (o != null) {
          var v = Refl.FieldOrProp(o, "jsonKey") as string; if (!string.IsNullOrEmpty(v)) return v;
        }
      }
      return null;
    }

static void StampUnique(object o) {
if (o == null) return;
foreach (var f in o.GetType().GetFields(Refl.All)) {
if (f.FieldType == typeof(string) && IsIdField(f.Name)) try { f.SetValue(o, Plugin.UniqueId); } catch { }
}
}
static readonly string[] AddNames = { "Add", "RegisterWeapon", "AddWeaponMount", "AddWeapon", "Register", "AddMissile", "AddDefinition" };
static MethodInfo FindAddMethod(Type t, Type defType, Type mountType) {
foreach (var name in AddNames) {
foreach (var m in t.GetMethods(Refl.All)) {
if (!string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
var ps = m.GetParameters(); if (ps.Length == 0 || ps.Length > 3) continue;
foreach (var p in ps) {
if ((defType != null && p.ParameterType.IsAssignableFrom(defType)) || (mountType != null && p.ParameterType.IsAssignableFrom(mountType)) || p.ParameterType == typeof(GameObject))
return m;
}
}
}
return null;
}
static object InvokeLoose(object target, MethodInfo m, object def, object mount, GameObject go) {
var ps = m.GetParameters(); var args = new object[ps.Length];
for (int i = 0; i < ps.Length; i++) {
Type pt = ps[i].ParameterType;
if (pt == typeof(GameObject)) args[i] = go;
else if (def != null && pt.IsInstanceOfType(def)) args[i] = def;
else if (mount != null && pt.IsInstanceOfType(mount)) args[i] = mount;
else if (pt == typeof(string)) args[i] = Plugin.UniqueId;
else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
}
try { return m.Invoke(m.IsStatic ? null : target, args); } catch { return null; }
}
static bool AddToCollection(object holder, object item) {
if (holder == null || item == null) return false;
foreach (var f in holder.GetType().GetFields(Refl.All)) {
var list = f.GetValue(holder) as IList; if (list == null) continue;
var el = list.GetType().IsGenericType ? list.GetType().GetGenericArguments() : null;
if (el == null || !el.IsInstanceOfType(item)) continue;
try { list.Add(item); return true; } catch { }
}
return false;
}
}
}
