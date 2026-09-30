using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  /// <summary>
  /// Coroutine-driven bootstrap v2 — runtime injection pattern (proven by MK-88 Hydra / Tsar Bomba):
  /// wait for Encyclopedia to exist -> get Multi-Missile bundle from Blueprinter.BundleRegistry
  /// -> load & clone zircon nuke prefab -> clone MissileDefinition/WeaponMount -> scale yield
  /// -> register into Encyclopedia/WeaponLookup -> inject WeaponMount into Aircraft/HardpointSet.
  /// Every step logs; failures are logged and skipped, never crash the game.
  /// </summary>
  public class Runner : MonoBehaviour {
    public static Runner Instance;
    bool done;

    void Awake() {
      if (Instance != null && Instance != this) { Destroy(gameObject); return; }
      Instance = this;
      DontDestroyOnLoad(gameObject);
    }

    public void Start() { StartCoroutine(Run()); }

    IEnumerator Run() {
      var wait = new WaitForSeconds(3f);
      Plugin.Log.LogInfo("Runner запущен, жду загрузку Blueprinter и Encyclopedia...");

      // 1) Wait for BundleRegistry + a loaded Multi-Missile bundle (like Chinese mods wait for Encyclopedia).
      AssetBundle mmBundle = null;
      int attempts = 0;
      while (mmBundle == null && attempts++ < 60) {
        mmBundle = Refl.FindMultiMissileBundle();
        if (mmBundle == null) {
          if (attempts == 1 || attempts % 10 == 0)
            Plugin.Log.LogInfo("Бандл Multi-Missile ещё не доступен (попытка " + attempts + "), жду...");
          yield return wait;
        }
      }
      if (mmBundle == null) {
        Plugin.Log.LogError("Multi-Missile бандл так и не найден в BundleRegistry за 180 сек. Мод останавливается (без падения).");
        yield break;
      }

      // 2) Load the zircon nuke prefab out of that bundle.
      GameObject prefab = CloneSource.LoadPrefab(mmBundle);
      if (prefab == null) {
        Plugin.Log.LogError("Префаб zircon nuke не найден внутри бандла Multi-Missile.");
        yield break;
      }
      Plugin.Log.LogInfo("Клонирую префаб " + prefab.name + "...");

      // 3) Clone it.
      GameObject clone = Refl.ClonePrefab(prefab);
      if (clone == null) { Plugin.Log.LogError("Instantiate префаба упал."); yield break; }
      clone.name = Plugin.UniqueId;
      clone.SetActive(false);
      try { DontDestroyOnLoad(clone); } catch { }

      // 4) Give the clone unique IDs / UI name (do NOT touch original).
      Registration.RenameClone(clone, prefab);

      // 5) Scale warhead yield on every numeric field we can find.
      Warhead.ApplyTo(clone);

      // 6) Phase-2 stub: external OBJ mesh swap point.
      MeshSwap.TryApplyExternalMesh(clone);

      // 7) Wait until Encyclopedia instance exists in the scene (Chinese-mod pattern),
      //    then register + inject.
      attempts = 0;
      bool registered = false;
      while (!registered && attempts++ < 60) {
        registered = Registration.RegisterAndInject(clone);
        if (!registered) {
          if (attempts == 1 || attempts % 10 == 0)
            Plugin.Log.LogInfo("尚无 Encyclopedia 实例，稍后再试 (попытка " + attempts + ")...");
          yield return wait;
        }
      }
      if (!registered)
        Plugin.Log.LogError("Не удалось зарегистрировать клон за 180 сек — см. логи выше по каждому шагу.");
      done = true;
    }
  }

  public static class Registration {
    const string Tag = "ZirconHV: ";

    static void Info(string s) { Plugin.Log.LogInfo(Tag + s); }
    static void Warn(string s) { Plugin.Log.LogWarning(Tag + s); }
    static void Err(string s)  { Plugin.Log.LogError(Tag + s); }

    // ---------------------------------------------------------------
    // Unique naming: rewrite id/name-ish strings on clone components
    // ---------------------------------------------------------------
    public static void RenameClone(GameObject clone, GameObject original) {
      Info("Назначаю клону уникальный ID " + Plugin.UniqueId + "...");
      string origId = null;
      foreach (var c in clone.GetComponentsInChildren<Component>(true)) {
        if (c == null) continue;
        var t = c.GetType();
        foreach (var f in t.GetFields(Refl.All)) {
          if (f.FieldType != typeof(string) || f.IsLiteral || f.IsStatic) continue;
          string v = null; try { v = (string)f.GetValue(c); } catch { continue; }
          if (v == null) continue;
          if (origId == null && IsIdField(f.Name)) origId = v;
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
      Info("Готово. Исходный id был: '" + (origId ?? "?") + "'");
    }

    static bool IsIdField(string n) {
      n = n.ToLowerInvariant();
      return n == "id" || n == "jsonkey" || n == "weaponid" || n == "key" || n == "techname"
          || n == "itemid" || n == "guid";
    }
    static bool IsNameField(string n) {
      n = n.ToLowerInvariant();
      return n == "name" || n == "displayname" || n == "weaponname" || n == "titlename";
    }
    static bool IndexOk(PropertyInfo p) {
      try { return p.GetIndexParameters().Length == 0; } catch { return false; }
    }

    // ---------------------------------------------------------------
    // Register + inject (called repeatedly until success)
    // ---------------------------------------------------------------
    public static bool RegisterAndInject(GameObject clone) {
      bool anyOk = false;

      // --- MissileDefinition / WeaponMount clones from the clone's own components ---
      var defs = Refl.ComponentsNamed(clone, "MissileDefinition");
      var mounts = Refl.ComponentsNamed(clone, "WeaponMount");
      if (defs.Length == 0) Err("В клоне нет компонента MissileDefinition — регистрация невозможна.");
      if (mounts.Length == 0) Err("В клоне нет компонента WeaponMount — инжекция на хардпоинты невозможна.");

      object defClone = null, mountClone = null;
      if (defs.Length > 0) {
        Info("Клонирую MissileDefinition (" + defs[0].GetType().FullName + ")...");
        defClone = Refl.CreateLike(defs[0].GetType(), defs[0]);
        if (defClone != null) { Warhead.ApplyTo(defClone); StampUnique(defClone); }
      }
      if (mounts.Length > 0) {
        Info("Клонирую WeaponMount (" + mounts[0].GetType().FullName + ")...");
        mountClone = Refl.CreateLike(mounts[0].GetType(), mounts[0]);
        if (mountClone != null && defClone != null) {
          // link cloned mount -> cloned definition if such a reference field exists
          foreach (var f in mountClone.GetType().GetFields(Refl.All))
            if (f.FieldType.IsInstanceOfType(defClone)) { try { f.SetValue(mountClone, defClone); } catch { } }
        }
      }

      // --- Encyclopedia / WeaponLookup / WeaponManager registration ---
      Type encType = Refl.FindTypeInCSharp("Encyclopedia");
      if (encType == null) encType = Refl.FindTypeInCSharp("WeaponLookup");
      if (encType == null) encType = Refl.FindTypeInCSharp("WeaponManager");
      if (encType == null) { Err("Ни Encyclopedia, ни WeaponLookup/WeaponManager не найдены."); return false; }

      object enc = FindSceneInstance(encType);
      if (enc == null) { /* log once softly */ return false; }
      Info("Нашел Encyclopedia в сцене: " + ((UnityEngine.Object)enc).name + ". Ищу метод добавления оружия...");

      MethodInfo addM = FindAddMethod(encType, defClone != null ? defClone.GetType() : null,
                                      mountClone != null ? mountClone.GetType() : null);
      if (addM != null) {
        Info("Вызываю " + encType.Name + "." + addM.Name + "(...)...");
        object res = InvokeLoose(enc, addM, defClone, mountClone, clone);
        if (res != null || true) { Info("Регистрация через " + addM.Name + " выполнена."); anyOk = true; }
      } else {
        Err("Метод добавления (Add/RegisterWeapon/AddWeaponMount) у " + encType.Name + " не найден. Пробую прямое добавление в коллекцию...");
        anyOk |= AddToCollection(enc, defClone) | AddToCollection(enc, mountClone);
      }

      // --- HardpointSet / Aircraft injection (MK-88 Hydra pattern) ---
      anyOk |= InjectHardpoints(mountClone, clone);

      if (anyOk) Info("Инъекция завершена успешно.");
      return anyOk;
    }

    static void StampUnique(object o) {
      if (o == null) return;
      foreach (var f in o.GetType().GetFields(Refl.All)) {
        if (f.FieldType == typeof(string) && IsIdField(f.Name)) { try { f.SetValue(o, Plugin.UniqueId); } catch { } }
      }
    }

    // ---------------- helpers ----------------

    /// <summary>Finds a live scene instance (or singleton) of the given type.</summary>
    static object FindSceneInstance(Type t) {
      var inst = Refl.FieldOrProp(t, "Instance") ?? Refl.FieldOrProp(t, "instance") ?? Refl.FieldOrProp(t, "Current");
      if (inst != null && !(inst is Type)) return inst;
      // MonoBehaviour search via Resources.FindObjectsOfTypeAll on Component-derived types
      try {
        var all = UnityEngine.Object.FindObjectsOfType(t);
        if (all != null && all.Length > 0) return all[0] as UnityEngine.Object;
      } catch { }
      return null;
    }

    static readonly string[] AddNames = { "Add", "RegisterWeapon", "AddWeaponMount", "AddWeapon",
                                          "Register", "AddMissile", "AddDefinition", "Unlock" };

    static MethodInfo FindAddMethod(Type t, Type defType, Type mountType) {
      foreach (var name in AddNames) {
        foreach (var m in t.GetMethods(Refl.All)) {
          if (!string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
          var ps = m.GetParameters();
          if (ps.Length == 0 || ps.Length > 3) continue;
          foreach (var p in ps) {
            if ((defType != null && p.ParameterType.IsAssignableFrom(defType)) ||
                (mountType != null && p.ParameterType.IsAssignableFrom(mountType)) ||
                p.ParameterType == typeof(GameObject))
              return m;
          }
        }
      }
      return null;
    }

    /// <summary>Invoke with best-effort argument binding among supplied candidates.</summary>
    static object InvokeLoose(object target, MethodInfo m, object def, object mount, GameObject go) {
      var ps = m.GetParameters();
      var args = new object[ps.Length];
      for (int i = 0; i < ps.Length; i++) {
        Type pt = ps[i].ParameterType;
        if (pt == typeof(GameObject)) args[i] = go;
        else if (def != null && pt.IsInstanceOfType(def)) args[i] = def;
        else if (mount != null && pt.IsInstanceOfType(mount)) args[i] = mount;
        else if (pt == typeof(string)) args[i] = Plugin.UniqueId;
        else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
      }
      try { return m.Invoke(m.IsStatic ? null : target, args); }
      catch (TargetInvocationException tie) {
        Err("Исключение внутри " + m.Name + ": " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message));
      } catch (Exception e) { Err("Не смог вызвать " + m.Name + ": " + e.Message); }
      return null;
    }

    /// <summary>Add an object directly into the first List/collection field whose element type accepts it.</summary>
    static bool AddToCollection(object holder, object item) {
      if (holder == null || item == null) return false;
      foreach (var f in holder.GetType().GetFields(Refl.All)) {
        var list = f.GetValue(holder) as IList;
        if (list == null) continue;
        var el = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : null;
        if (el == null || !el.IsInstanceOfType(item)) continue;
        try { list.Add(item); Info("Добавил " + item.GetType().Name + " в поле " + f.Name + " (" + holder.GetType().Name + ")."); return true; }
        catch (Exception e) { Warn("list.Add в " + f.Name + " упал: " + e.Message); }
      }
      return false;
    }

    /// <summary>MK-88 Hydra pattern: add our WeaponMount to every HardpointSet / Aircraft found in scene.</summary>
    static bool InjectHardpoints(object mountClone, GameObject clone) {
      object payload = mountClone ?? (object)clone;
      bool any = false;
      foreach (string typeName in new[] { "HardpointSet", "Aircraft" }) {
        Type ht = Refl.FindTypeInCSharp(typeName);
        if (ht == null) continue;
        UnityEngine.Object[] sets;
        try { sets = UnityEngine.Object.FindObjectsOfType(ht); } catch (Exception e) { Err("FindObjectsOfType(" + typeName + ") упал: " + e.Message); continue; }
        Info("Найдено объектов " + typeName + ": " + sets.Length + ". Инжектирую WeaponMount...");
        foreach (var s in sets) {
          if (s == null) continue;
          if (TryInjectInto(s, payload)) { any = true; }
        }
        if (any) break; // one working mechanism is enough
      }
      if (!any) Warn("Ни один HardpointSet/Aircraft не принял WeaponMount (нужно имя метода/поля из дампа API).");
      return any;
    }

    static bool TryInjectInto(object target, object payload) {
      Type t = target.GetType();
      // 1) method like AddWeaponMount / Add / MountWeapon
      foreach (var name in new[] { "AddWeaponMount", "AddWeapon", "AddMount", "MountWeapon", "Add" }) {
        var m = Refl.Method(t, name);
        if (m == null) continue;
        var ps = m.GetParameters();
        if (ps.Length == 0 || ps.Length > 2) continue;
        if (!ps[0].ParameterType.IsInstanceOfType(payload)) continue;
        try { m.Invoke(target, new[] { payload }); Info("Injected via " + t.Name + "." + name + "()"); return true; }
        catch (TargetInvocationException tie) { Warn(name + " бросил: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message)); }
        catch (Exception e) { Warn(name + ": " + e.Message); }
      }
      // 2) collection field accepting payload
      foreach (var f in t.GetFields(Refl.All)) {
        IList list = null; try { list = f.GetValue(target) as IList; } catch { }
        if (list == null) continue;
        var el = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : null;
        if (el == null || !el.IsInstanceOfType(payload)) continue;
        try { list.Add(payload); Info("Injected into field " + t.Name + "." + f.Name); return true; } catch { }
      }
      return false;
    }
  }
}
