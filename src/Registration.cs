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
      // v1.0.1: запоминаем ДОНОРСКИЙ jsonKey/имя исходного WeaponMount до перебивания клону.
      DonorJsonKey = Refl.FieldOrProp(bundleMountSO, "jsonKey") as string;
      DonorWeaponName = GetWeaponName(bundleMountSO);
      Info("Донорский WeaponMount: jsonKey=\"" + DonorJsonKey + "\", weaponName=\"" + DonorWeaponName + "\"");
      object mountClone = Refl.CreateLike(bundleMountSO.GetType(), bundleMountSO);
      object defClone   = Refl.CreateLike(bundleDefSO.GetType(), bundleDefSO);
      if (mountClone == null || defClone == null) return false;
      clonedLocal = true; mountCloneRef = mountClone; // v1.0.1: для повторного инжекта после AfterLoad
      
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
      bool injOk = InjectHardpointsV3(mountClone) > 0;
      // v1.0.1: инжект мог провалиться из-за тайминга — Hardpoint'ы борта создаются позже.
      // Plugin.Update продолжит вызывать ReinjectHardpoints каждые 5 сек (см. reinjectPending).
      if (injOk || anyOk) { injectedAtLeastOnce = true; }
      if (anyOk && injOk) LastJsonKey = CloneJsonKey;
      return anyOk && injOk;
    }

    // v1.0.1: донорский jsonKey исходного WeaponMount SO из бандла Multi-Missile
    // (например "zircon_3m22"). Находится в RegisterAndInject до перебивания клону jsonKey.
    public static string DonorJsonKey;
    public static string DonorWeaponName;
    public static bool injectedAtLeastOnce;

    /// <summary>
    /// v1.0.1: повторный инжект пилон-опций (вызывается из Plugin после Encyclopedia.AfterLoad).
    /// Нужен, потому что в момент первой попытки Hardpoint'ы борта ещё не созданы
    /// (FindObjectsOfTypeAll возвращает пустой список) — инъекция молча пропускалась.
    /// </summary>
    public static void ReinjectHardpoints(GameObject clone, object bundleMountSO) {
      if (!clonedLocal || clone == null || mountCloneRef == null) return;
      if (bundleMountSO != null && string.IsNullOrEmpty(DonorJsonKey))
        DonorJsonKey = Refl.FieldOrProp(bundleMountSO, "jsonKey") as string;
      int n = InjectHardpointsV3(mountCloneRef);
      if (n > 0) { LastJsonKey = CloneJsonKey; Info("Reinject успешен: добавлено пилонов: " + n); }
    }

    static bool clonedLocal;
    static object mountCloneRef;

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

    // v1.0.1: полный переписанный инжект пилонов.
    // СТАРЫЕ БАГИ, которые чиним:
    //  1) сопоставление шло по weaponName (у WeaponMount его может не быть -> null -> ни один
    //     Hardpoint не матчился);
    //  2) matcился ТЕКУЩИЙ mount пилона — после смены опции в UI это уже клон/другое оружие;
    //     нужно матчить ВСЮ pylonOptions (любое звено с донорским jsonKey);
    //  3) return injDone = true возвращался даже при нулевых изменениях, маскируя провал;
    //  4) renderer новой опции обнулялся — модель на пилоне не отображается.
    static int InjectHardpointsV3(object mountClone) {
      if (mountClone == null) return -1;
      Type hpT = Refl.FindTypeInCSharp("Hardpoint"); if (hpT == null) return -1;
      FieldInfo mountF = hpT.GetField("mount", Refl.All);
      FieldInfo optsF  = hpT.GetField("pylonOptions", Refl.All);
      if (mountF == null || optsF == null) { Err("Hardpoint.mount/pylonOptions не найдены."); Refl.DumpMembers(hpT); return -1; }
      Type optT = optsF.FieldType.IsArray ? optsF.FieldType.GetElementType() : null; if (optT == null) return -1;
      FieldInfo optMountF  = optT.GetField("mount", Refl.All);
      FieldInfo optRenderF = optT.GetField("renderer", Refl.All);
      if (optMountF == null) return -1;

      string donorKey = DonorJsonKey;
      Info("Инжект пилон-опций: ищу Hardpoint'ы, у которых в pylonOptions есть mount с jsonKey=\"" + donorKey + "\"...");

      UnityEngine.Object[] hps = null; try { hps = UnityEngine.Resources.FindObjectsOfTypeAll(hpT); } catch { return -1; }
      int patched = 0, scanned = 0;
      foreach (var o in hps) {
        if (o == null) continue;
        try {
          object hp = o;
          Array opts = optsF.GetValue(hp) as Array; if (opts == null || opts.Length == 0) continue;
          scanned++;
          // ищем ЛЮБУЮ опцию этого пилона, чей mount — донорский Циркон (по jsonKey, фолбэк по имени)
          bool donorHere = false;
          object lastDonorOpt = null;
          for (int q = 0; q < opts.Length; q++) {
            object om = optMountF.GetValue(opts.GetValue(q));
            if (om == null) continue;
            if (ReferenceEquals(om, mountClone)) { donorHere = true; break; } // уже инжектировали
            if (MatchesDonor(om, donorKey)) { donorHere = true; lastDonorOpt = opts.GetValue(q); }
          }
          if (!donorHere || lastDonorOpt == null) continue;

          object copy = ShallowCopyOption(lastDonorOpt, optT); if (copy == null) continue;
          optMountF.SetValue(copy, mountClone);
          if (optRenderF != null) {
            // НЕ обнуляем renderer: берём из текущей смонтированной модели пилона,
            // иначе превью/модель оружия на пилоне не отрисуется.
            object cur = mountF.GetValue(hp);
            if (cur == null) {
              for (int q = 0; q < opts.Length; q++) {
                var r = optRenderF.GetValue(opts.GetValue(q)) as UnityEngine.Object;
                if (r != null) { optRenderF.SetValue(copy, r); break; }
              }
            } else {
              var rm = Refl.FieldOrProp(cur, "model") as UnityEngine.Component;
              var rr = rm != null ? rm.GetComponent<Renderer>() : null;
              if (rr != null) optRenderF.SetValue(copy, rr);
            }
          }
          var newArr = Array.CreateInstance(optT, opts.Length + 1);
          for (int q = 0; q < opts.Length; q++) newArr.SetValue(opts.GetValue(q), q);
          newArr.SetValue(copy, opts.Length); optsF.SetValue(hp, newArr);
          patched++;
        } catch (Exception e) { Warn("Hardpoint пропущен из-за исключения: " + e.Message); }
      }
      injDone = patched > 0;
      Info("Проверено Hardpoint'ов с опциями: " + scanned + ", добавлено звено с клоном: " + patched + ".");
      if (patched == 0)
        Err("НИ ОДИН Hardpoint не содержит донорский Циркон (jsonKey=\"" + donorKey + "\"). " +
            "Проверь: включён ли Multi-Missile, загружены ли его бандлы до этой попытки.");
      return patched;
    }

    static bool MatchesDonor(object mount, string donorKey) {
      if (string.IsNullOrEmpty(donorKey)) return false;
      var k = Refl.FieldOrProp(mount, "jsonKey") as string;
      if (!string.IsNullOrEmpty(k) && string.Equals(k, donorKey, StringComparison.OrdinalIgnoreCase)) return true;
      // фолбэк: имя префаба/weaponName содержит "zircon"
      try {
        var go = Refl.FieldOrProp(mount, "unitPrefab") as GameObject ?? Refl.FieldOrProp(mount, "prefab") as GameObject;
        if (go != null && go.name.IndexOf("zircon", StringComparison.OrdinalIgnoreCase) >= 0) return true;
      } catch { }
      var wn = GetWeaponName(mount);
      return wn != null && wn.IndexOf("Zircon", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool injDone;

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
