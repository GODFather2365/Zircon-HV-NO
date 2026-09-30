using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  /// <summary>
  /// v6: КОНЕЧНЫЙ АВТОМАТ ПЕРЕЕХАЛ В САМ Plugin (partial-класс, см. Plugin.Loop.cs).
  /// Диагноз v5: игра уничтожает GameObject ZirconHV_Root сразу после
  /// "Chainloader startup complete" (OnDestroy на попытке 0) — поэтому все прошлые
  /// версии молчали. BaseUnityPlugin — MonoBehaviour, который BepInEx создаёт сам
  /// и НЕ уничтожает до выхода из игры, отдельный GameObject больше не нужен.
  /// Логика тиков/стадий (a-f) НЕ изменена — перенесена дословно.
  /// Стадии (переход только при успехе, лог на каждом тике):
  ///   (a) бандл   — AssetBundle.GetAllLoadedAssetBundles() через рефлексию
  ///   (b) префаб  — GetAllAssetNames() -> zirconnuke (нормализованно), fuzzy *zircon*.prefab
  ///   (c) Encyclopedia — Resources.FindObjectsOfTypeAll через MakeGenericMethod
  ///   (d) клон    — Instantiate -> RenameClone/RewriteStringIds -> Warhead.blastYield
  ///   (e) регистрация — EncyclopediaLoader.AddWeaponMount/AddUnit (Activator.CreateInstance)
  ///   (f) инжект  — InjectHardpointsV3
  /// Проигрыш любой стадии НЕ убивает цикл: следующий тик повторит попытку,
  /// пока не кончится бюджет 180 сек (done=true останавливает опрос).
  /// NOTE: первые ~50 сек попытка видит 0 бандлов — это норма, бандлы
  /// ("multi - missile") появляются после загрузки Blueprinter.
  /// </summary>
  public partial class Plugin {
    const float TickInterval = 2.5f;
    const int   MaxAttempts  = 72;          // 72 x 2.5s ~= 180 sec
    const float HeartbeatEvery = 20f;

    bool done;
    int attempt;                            // общий счётчик тиков для лога
    float nextAt;                           // realtimeSinceStartup, когда делать следующий тик
    float lastHeartbeat;
    string stage = "a) поиск бандла";

    // результаты стадий (переживаются между тиками)
    AssetBundle mmBundle;
    GameObject prefab;
    string assetName;
    object enc;                             // экземпляр Encyclopedia
    GameObject clone;                       // клон префаба (стадия d)
    bool cloned;

    /// <summary>Вызывается из Plugin.Awake() (v6: вместо создания ZirconHV_Root+Runner).</summary>
    void InitLoop() {
      nextAt = Time.realtimeSinceStartup + TickInterval;
      lastHeartbeat = Time.realtimeSinceStartup;
      Log.LogInfo("Цикл v6 запущен ВНУТРИ Plugin (BaseUnityPlugin-объект BepInEx не удаляет): "
        + "Update()-тики каждые " + TickInterval + " сек, максимум " + MaxAttempts
        + " попыток (~180 сек), heartbeat каждые " + HeartbeatEvery + " сек, каждое тело тика в try/catch."
        + " Первые ~50 сек бандлов может быть 0 — это норма (Blueprinter грузится позже).");
    }

    void Update() {
      if (done) return;
      float now = Time.realtimeSinceStartup;
      if (now < nextAt) return;
      nextAt += TickInterval;
      attempt++;
      try {
        Tick();
      } catch (Exception e) {
        Log.LogError("Попытка " + attempt + " (стадия " + stage + ") бросила исключение (продолжаю): " + e);
      }
      if (now - lastHeartbeat >= HeartbeatEvery) {
        lastHeartbeat = now;
        Log.LogInfo("Runner жив, попытка " + attempt + "/" + MaxAttempts + ", стадия: " + stage);
      }
      if (!done && attempt >= MaxAttempts) {
        done = true;
        Log.LogError("=== ИТОГ ПРОВАЛА ===");
        Log.LogError("  остановился на стадии: " + stage);
        Log.LogError("  бандл: " + (mmBundle != null ? "\"" + mmBundle.name + "\"" : "не найден (см. список бандлов в логе GetAllLoadedAssetBundles выше)"));
        Log.LogError("  asset name: \"" + (assetName ?? "?") + "\"");
        Log.LogError("  Encyclopedia: " + (enc != null ? "найден" : "НЕ найден (Resources.FindObjectsOfTypeAll пуст, postfix AfterLoad не сработал)"));
        Log.LogError("  клон создан: " + cloned);
        Log.LogError("  jsonKey клона: \"" + (Registration.LastJsonKey ?? "?") + "\"");
        Log.LogError("  AddWeaponMount/AddUnit: " + Registration.LastOpsResult);
        Log.LogError("  инжектировано hardpoint set(s): " + Registration.LastInjectedSets);
        Log.LogError("Бюджет 180 сек исчерпан — опрос остановлен (флаг done), мод больше не шумит.");
      }
    }

    /// <summary>Один тик конечного автомата. Каждая стадия выполняется только если предыдущая успешна.</summary>
    void Tick() {
      // (a)+(b) бандл и префаб
      if (prefab == null) {
        stage = "a) поиск бандла";
        Log.LogInfo("Попытка " + attempt + "/" + MaxAttempts + " [a]: ищу загруженный бандл Multi-Missile через GetAllLoadedAssetBundles...");
        if (mmBundle == null) {
          mmBundle = Refl.FindMultiMissileBundleByApi();
          if (mmBundle == null) {
            Log.LogInfo("Попытка " + attempt + " [a]: по API бандл не найден, пробую фолбэк BundleRegistry.Bundles...");
            mmBundle = Refl.FindMultiMissileBundle();
          }
        }
        if (mmBundle != null) {
          stage = "b) загрузка префаба";
          Log.LogInfo("Попытка " + attempt + "/" + MaxAttempts + " [b]: грузу zirconnuke из бандла \"" + mmBundle.name + "\"...");
          string an;
          prefab = Refl.LoadZirconPrefab(mmBundle, out an);
          if (prefab != null) assetName = an;
          else {
            Log.LogError("Попытка " + attempt + " [b]: бандл \"" + mmBundle.name + "\" найден, но префаб zirconnuke в нём нет — сбрасываю бандл и ищу другой.");
            mmBundle = null;
          }
        }
        if (prefab == null) return;
        Registration.LastAssetName = assetName;
        Log.LogInfo("Стадия (a)+(b) пройдена: префаб \"" + prefab.name + "\" (asset name: \"" + assetName + "\").");
      }

      // (c) Encyclopedia должна существовать ДО клонирования — иначе переписанные
      //     id/name попадут в реестр раньше самой энциклопедии (порядок как в v4).
      if (enc == null) {
        stage = "c) ожидание Encyclopedia";
        Log.LogInfo("Попытка " + attempt + "/" + MaxAttempts + " [c]: ищу Encyclopedia (Resources.FindObjectsOfTypeAll)...");
        Type encType = Refl.FindTypeInCSharp("Encyclopedia");
        if (encType == null) {
          Log.LogError("Попытка " + attempt + " [c]: тип Encyclopedia в Assembly-CSharp не найден (продолжаю искать в следующих тиках).");
          return;
        }
        Log.LogInfo("Нашел тип Encyclopedia = " + encType.FullName);
        enc = Registration.FindEncyclopedia(encType);
        if (enc == null) {
          Log.LogInfo("Попытка " + attempt + " [c]:尚无 Encyclopedia 实例，稍后再试 (postfix-статика и поллинг пусты)...");
          return;
        }
        Log.LogInfo("Стадия (c) пройдена: Encyclopedia-экземпляр \"" + ((UnityEngine.Object)enc).name + "\" (" + enc.GetType().FullName + ").");
      }

      // (d) клон: Instantiate -> RenameClone/RewriteStringIds -> Warhead.blastYield
      if (!cloned) {
        stage = "d) клонирование префаба";
        Log.LogInfo("Попытка " + attempt + "/" + MaxAttempts + " [d]: Клонирую префаб " + prefab.name + " (asset name: \"" + assetName + "\")...");
        clone = Refl.ClonePrefab(prefab);
        if (clone == null) {
          Log.LogError("Попытка " + attempt + " [d]: Instantiate префаба упал (повторю в следующем тике).");
          return;
        }
        clone.name = Plugin.UniqueId;
        clone.SetActive(false);
        // v6: DontDestroyOnLoad для клона больше не нужен — корневой объект плагина (BaseUnityPlugin-гобъект) не уничтожается, клон живёт как отдельный неактивный root.
        Registration.RenameClone(clone, prefab);
        Warhead.ApplyTo(clone);
        MeshSwap.TryApplyExternalMesh(clone); // Phase-2 stub
        cloned = true;
        Log.LogInfo("Стадия (d) пройдена: клон \"" + clone.name + "\" создан, ID переписаны на " + Plugin.UniqueId + ".");
      }

      // (e)+(f) регистрация EncyclopediaLoader + инжект хардпоинтов
      stage = "e/f) регистрация + инжект";
      Log.LogInfo("Попытка " + attempt + "/" + MaxAttempts + " [e]: EncyclopediaLoader.AddWeaponMount/AddUnit + [f] InjectHardpointsV3...");
      bool ok = Registration.RegisterAndInject(clone, enc);
      if (ok) { done = true; stage = "завершено"; }
      else Log.LogWarning("Попытка " + attempt + " [e/f]: регистрация/инжект не удались (см. ошибки выше), повторю в следующем тике.");
    }
  }

  public static class Registration {
    const string Tag = "ZirconHV: ";
    public static string LastAssetName;     // v4 p.5 summary
    public static int LastInjectedSets;     // v4 p.5 summary
    public static string LastJsonKey;       // v5 fail-summary
    public static string LastOpsResult = "не вызывалось"; // v5 fail-summary

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
    // v3 Register: Blueprinter.Ops.EncyclopediaLoader instance methods
    //   Void AddWeaponMount(Encyclopedia encyclopedia, WeaponMount mount)
    //   Void AddUnit(Encyclopedia encyclopedia, UnitDefinition unit)
    // ---------------------------------------------------------------
    static bool RegisterViaOps(GameObject clone, object enc, object defClone, object mountClone) {
      Type loaderT = Refl.FindType("Blueprinter.Ops.EncyclopediaLoader");
      if (loaderT == null) { Err("Тип Blueprinter.Ops.EncyclopediaLoader не найден. Пробую поиск по короткому имени..."); loaderT = Refl.FindTypeInCSharp("EncyclopediaLoader"); }
      if (loaderT == null) { Err("EncyclopediaLoader нигде не найден — фолбэк на прямое добавление."); LastOpsResult = "EncyclopediaLoader не найден"; return false; }

      object loader;
      try { loader = Activator.CreateInstance(loaderT, true); Info("Создал экземпляр " + loaderT.FullName + " через Activator.CreateInstance..."); }
      catch (Exception e) { Err("Activator.CreateInstance(" + loaderT.FullName + ") упал: " + e.Message + ". Мини-дамп:"); Refl.DumpMembers(loaderT); LastOpsResult = "Activator.CreateInstance упал: " + e.Message; return false; }

      // v5: Encyclopedia instance comes from the Runner stage (c): postfix-captured static
      // OR Resources.FindObjectsOfTypeAll polling — passed in as parameter, we do NOT re-poll here.
      if (enc == null) { Info("尚无 Encyclopedia 实例，稍后再试 (Runner ещё не получил инстанс)..."); LastOpsResult = "Encyclopedia-инстанс не передан"; return false; }
      Info("Использую Encyclopedia экземпляр: " + enc.GetType().FullName);

      bool ok = false;
      var addMount = Refl.Method(loaderT, "AddWeaponMount");
      if (addMount != null && mountClone != null) {
        Info("Вызываю " + loaderT.Name + ".AddWeaponMount(Encyclopedia, WeaponMount)...");
        if (InvokeTyped(loader, addMount, enc, mountClone)) ok = true;
      } else Warn("AddWeaponMount не найден на " + loaderT.Name + " или mountClone=null.");

      var addUnit = Refl.Method(loaderT, "AddUnit");
      if (addUnit != null && defClone != null) {
        // defClone must be assignable to UnitDefinition param; MissileDefinition usually derives from it
        Info("Вызываю " + loaderT.Name + ".AddUnit(Encyclopedia, UnitDefinition)...");
        if (InvokeTyped(loader, addUnit, enc, defClone)) ok = true;
      } else Warn("AddUnit не найден на " + loaderT.Name + " или defClone=null.");

      if (!ok) { Err("Ни один метод EncyclopediaLoader не подошёл под наши клоны. Мини-дамп методов:"); Refl.DumpMembers(loaderT); LastOpsResult = "ни AddWeaponMount, ни AddUnit не подошли"; }
      else LastOpsResult = ok ? "УСПЕХ" : "НЕТ";
      return ok;
    }

    /// <summary>Invoke m(target, args) with per-parameter type filtering; returns success.</summary>
    static bool InvokeTyped(object target, MethodInfo m, params object[] candidates) {
      var ps = m.GetParameters();
      var args = new object[ps.Length];
      for (int i = 0; i < ps.Length; i++) {
        bool found = false;
        foreach (var c in candidates) {
          if (c == null) continue;
          if (ps[i].ParameterType.IsInstanceOfType(c)) { args[i] = c; found = true; break; }
        }
        if (!found) { Warn("Метод " + m.Name + ": параметр #" + i + " (" + ps[i].ParameterType.Name + ") нечем заполнить — пропускаю вызов."); return false; }
      }
      try { m.Invoke(m.IsStatic ? null : target, args); Info("Метод " + m.Name + " выполнен успешно."); return true; }
      catch (TargetInvocationException tie) { Err(m.Name + " бросил: " + (tie.InnerException != null ? tie.InnerException.Message + "\n" + tie.InnerException.StackTrace : tie.Message)); }
      catch (Exception e) { Err("Не смог вызвать " + m.Name + ": " + e.Message); }
      return false;
    }

    // ---------------------------------------------------------------
    // v3 Hardpoint injection (Multi-Missile open source pattern, BSD):
    //   Hardpoint fields: mount, spawnedPrefab, pylonOptions(array);
    //   pylonOptions element fields: mount, renderer.
    //   HardpointSet collection field: hardpoints/Hardpoints/_hardpoints/pylons.
    //   Add-only: clone an existing option, swap its mount -> ours, append.
    // ---------------------------------------------------------------
    static readonly string[] HpSetFields = { "hardpoints", "Hardpoints", "_hardpoints", "pylons" };
    static readonly string[] MountFieldNames = { "mount", "weaponMount", "WeaponMount" };
    static readonly string[] OptArrayFieldNames = { "pylonOptions", "options", "pylonOptionsList" };
    static readonly string[] RendererFieldNames = { "renderer", "meshRenderer" };

    static bool InjectHardpointsV3(object mountClone) {
      if (mountClone == null) { Err("InjectHardpointsV3: mountClone=null."); return false; }
      Type hpSetT = Refl.FindTypeInCSharp("HardpointSet");
      if (hpSetT == null) { Err("Тип HardpointSet не найден."); return false; }
      UnityEngine.Object[] sets;
      try { sets = UnityEngine.Object.FindObjectsOfType(hpSetT); }
      catch (Exception e) { Err("FindObjectsOfType(HardpointSet) упал: " + e.Message); return false; }
      Info("Найдено HardpointSet в сцене: " + sets.Length + ". Ищу поле-коллекцию среди " + string.Join("/", HpSetFields) + "...");
      int injected = 0;
      foreach (var s in sets) {
        if (s == null) continue;
        try { if (InjectIntoOneSet(s, mountClone)) injected++; }
        catch (Exception e) { Warn("Инъекция в " + s.name + " упала: " + e.Message); }
      }
      LastInjectedSets = injected;
      if (injected > 0) { Info("HardpointInjector: добавил опции с нашим WeaponMount в " + injected + " hardpoint set(s)."); return true; }
      Warn("Ни один HardpointSet не принял инъекцию. Мини-дамп HardpointSet:");
      Refl.DumpMembers(hpSetT);
      return false;
    }

    static bool InjectIntoOneSet(UnityEngine.Object set, object mountClone) {
      Type t = set.GetType();
      Array hps = null; string hpFieldName = null;
      foreach (var name in HpSetFields) {
        var arr = Refl.FieldOrProp(set, name) as Array;
        if (arr != null && arr.Length > 0) { hps = arr; hpFieldName = name; break; }
      }
      if (hps == null) { Warn(t.Name + " (" + set.name + "): поле хардпоинтов не найдено/пусто (" + string.Join("/", HpSetFields) + ")."); return false; }
      Info("Set \"" + set.name + "\": поле " + hpFieldName + ", хардпоинтов = " + hps.Length + ".");
      bool any = false;
      foreach (var hp in hps) {
        if (hp == null) continue;
        if (InjectIntoHardpoint(hp, mountClone)) any = true;
      }
      return any;
    }

    static bool InjectIntoHardpoint(object hp, object mountClone) {
      Type ht = hp.GetType();
      // find our array (pylonOptions)
      Array opts = null; string optName = null;
      foreach (var name in OptArrayFieldNames) {
        var a = Refl.FieldOrProp(hp, name) as Array;
        if (a != null) { opts = a; optName = name; break; }
      }
      if (opts == null) { Warn("Hardpoint " + ht.Name + ": массив pylonOptions не найден (" + string.Join("/", OptArrayFieldNames) + "). Мини-дамп:"); Refl.DumpMembers(ht); return false; }
      if (opts.Length == 0) { Warn("Hardpoint " + ht.Name + "." + optName + " пуст — клонировать структуру не из чего, пропускаю."); return false; }
      // find mount field on the hardpoint itself and on the option element
      FieldInfo hpMountF = null;
      foreach (var name in MountFieldNames) { var f = Refl.Field(ht, name); if (f != null && f.FieldType.IsInstanceOfType(mountClone)) { hpMountF = f; break; } }
      object templateOpt = opts.GetValue(opts.Length - 1);
      Type ot = templateOpt.GetType();
      FieldInfo optMountF = null;
      foreach (var name in MountFieldNames) { var f = Refl.Field(ot, name); if (f != null && f.FieldType.IsInstanceOfType(mountClone)) { optMountF = f; break; } }
      if (optMountF == null) { Warn("Элемент " + ot.Name + ": поле mount нужного типа нет. Мини-дамп:"); Refl.DumpMembers(ot); return false; }
      // clone the option structure (class -> Instantiate for Unity objects / shallow copy otherwise; struct -> box-copy)
      object newOpt = CloneOption(templateOpt, mountClone, optMountF);
      if (newOpt == null) return false;
      // grow the array (add-only)
      var newArr = Array.CreateInstance(ot, opts.Length + 1);
      for (int i = 0; i < opts.Length; i++) newArr.SetValue(opts.GetValue(i), i);
      newArr.SetValue(newOpt, opts.Length);
      if (!Refl.SetDeep(hp, optName, newArr)) { Warn("Не смог записать расширенный массив " + ht.Name + "." + optName + "."); return false; }
      // also point the hardpoint's own default mount at ours if such a field exists (optional, best-effort)
      if (hpMountF != null) { try { hpMountF.SetValue(hp, mountClone); Info("Hardpoint." + hpMountF.Name + " переключён на наш WeaponMount."); } catch { } }
      Info("Добавлена опция хардпоинта: " + ht.Name + "." + optName + "[" + opts.Length + "] -> mount=" + Plugin.UniqueId);
      return true;
    }

    static object CloneOption(object templateOpt, object mountClone, FieldInfo optMountF) {
      Type ot = templateOpt.GetType();
      object copy;
      if (ot.IsValueType) {
        copy = Activator.CreateInstance(ot); // boxed struct copy below via field-by-field
        foreach (var f in ot.GetFields(Refl.All)) {
          if (f.IsStatic || f.IsLiteral) continue;
          try { f.SetValue(copy, f.GetValue(templateOpt)); } catch { }
        }
      } else if (templateOpt is UnityEngine.Object) {
        copy = UnityEngine.Object.Instantiate((UnityEngine.Object)templateOpt);
      } else {
        try {
          copy = Activator.CreateInstance(ot, true);
          foreach (var f in ot.GetFields(Refl.All)) {
            if (f.IsStatic || f.IsLiteral) continue;
            try { f.SetValue(copy, f.GetValue(templateOpt)); } catch { }
          }
        } catch (Exception e) { Warn("Клон опции " + ot.Name + " упал: " + e.Message); return null; }
      }
      try { optMountF.SetValue(copy, mountClone); } catch (Exception e) { Warn("не смог подставить mount в опцию: " + e.Message); return null; }
      // rename-ish: set display-name strings to our UI name if present
      foreach (var f in ot.GetFields(Refl.All))
        if (f.FieldType == typeof(string) && !f.IsStatic && f.Name.ToLowerInvariant().Contains("name"))
          { try { f.SetValue(copy, "Zircon HV (1 Mt)"); } catch { } }
      return copy;
    }

    // ---------------------------------------------------------------
    // Register + inject (called repeatedly until success) — v3 flow:
    // EncyclopediaLoader.AddWeaponMount/AddUnit first, direct collection
    // add as fallback; hardpoint injection via pylonOptions (add-only).
    // ---------------------------------------------------------------
    public static bool RegisterAndInject(GameObject clone, object enc) {
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
        if (defClone != null) { Warhead.ApplyToBlastYield(defClone); StampUnique(defClone); }
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

      // --- v3 PRIMARY: Blueprinter.Ops.EncyclopediaLoader.AddWeaponMount / AddUnit ---
      bool opsOk = RegisterViaOps(clone, enc, defClone, mountClone);
      anyOk |= opsOk;

      // --- FALLBACK: direct Encyclopedia/WeaponLookup collection add (only if Ops failed) ---
      if (!opsOk) {
        Type encType = Refl.FindTypeInCSharp("Encyclopedia");
        if (encType == null) encType = Refl.FindTypeInCSharp("WeaponLookup");
        if (encType == null) encType = Refl.FindTypeInCSharp("WeaponManager");
        if (encType == null) Err("Ни Encyclopedia, ни WeaponLookup/WeaponManager не найдены — фолбэк невозможен.");
        else {
          if (enc == null) enc = FindEncyclopedia(encType);
          if (enc == null) { Info("尚无 Encyclopedia 实例，稍后再试 (фолбэк тоже ждёт)..."); return false; }
          Info("Нашел Encyclopedia в сцене: " + ((UnityEngine.Object)enc).name + ". Фолбэк: ищу метод добавления оружия...");
          MethodInfo addM = FindAddMethod(encType, defClone != null ? defClone.GetType() : null,
                                          mountClone != null ? mountClone.GetType() : null);
          if (addM != null) {
            Info("Вызываю " + encType.Name + "." + addM.Name + "(...)...");
            InvokeLoose(enc, addM, defClone, mountClone, clone);
            anyOk = true;
          } else {
            Err("Метод добавления (Add/RegisterWeapon/AddWeaponMount) у " + encType.Name + " не найден. Мини-дамп + прямое добавление в коллекцию...");
            Refl.DumpMembers(encType);
            anyOk |= AddToCollection(enc, defClone) | AddToCollection(enc, mountClone);
          }
        }
      }

      // --- HardpointSet injection via pylonOptions (v3, MK-88 Hydra add-only pattern) ---
      anyOk |= InjectHardpointsV3(mountClone);

      if (anyOk) {
        // v4 p.5 / v5 p.5: final success summary
        string jk = null;
        foreach (var target in new object[] { defClone, mountClone, clone }) {
          if (target == null) continue;
          var v = Refl.FieldOrProp(target, "jsonKey") as string;
          if (v != null) { jk = v; break; }
        }
        LastJsonKey = jk;
        Info("=== ИТОГ УСПЕХА ===");
        Info("  asset name префаба: \"" + (LastAssetName ?? "?") + "\"");
        Info("  jsonKey клона: \"" + (jk ?? "?") + "\" (уникальный ID = " + Plugin.UniqueId + ")");
        Info("  EncyclopediaLoader.AddWeaponMount/AddUnit: " + (opsOk ? "УСПЕХ" : "НЕТ (использован фолбэк)"));
        Info("  инжектировано hardpoint set(s): " + LastInjectedSets);
        Info("Инъекция завершена успешно.");
      }
      return anyOk;
    }

    static void StampUnique(object o) {
      if (o == null) return;
      foreach (var f in o.GetType().GetFields(Refl.All)) {
        if (f.FieldType == typeof(string) && IsIdField(f.Name)) { try { f.SetValue(o, Plugin.UniqueId); } catch { } }
      }
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// v5 p.3: Encyclopedia instance lookup for the Runner stage (c).
    /// PRIMARY = Harmony-postfix static (EncyclopediaPatches.LastInstance, secondary source),
    /// then Resources.FindObjectsOfTypeAll via MakeGenericMethod (first non-null),
    /// then static Instance/instance/Current, then Object.FindObjectsOfType.
    /// </summary>
    public static object FindEncyclopedia(Type encType) {
      if (encType == null) return null;
      object byPostfix = EncyclopediaPatches.LastInstance;
      if (byPostfix != null && encType.IsInstanceOfType(byPostfix)) {
        Info("Encyclopedia получен из Harmony-postfix (Encyclopedia::AfterLoad): \"" + ((UnityEngine.Object)byPostfix).name + "\".");
        return byPostfix;
      }
      var found = FindSceneInstance(encType);
      if (found != null) Info("Encyclopedia получен поллингом (Resources.FindObjectsOfTypeAll/статика).");
      return found;
    }

    /// <summary>
    /// Finds a live instance of the given type. v4 p.2: PRIMARY = Resources.FindObjectsOfTypeAll(type)
    /// (reflection MakeGenericMethod, first non-null) — catches prefassets/inactive too;
    /// secondary = static Instance/instance/Current; tertiary = Object.FindObjectsOfType.
    /// </summary>
    static object FindSceneInstance(Type t) {
      if (t == null) return null;
      try {
        var gm = typeof(Resources).GetMethod("FindObjectsOfTypeAll",
          BindingFlags.Public | BindingFlags.Static);
        if (gm != null) {
          var made = gm.MakeGenericMethod(t);
          var arr = made.Invoke(null, null) as UnityEngine.Object[];
          if (arr != null && arr.Length > 0) {
            foreach (var o in arr) if (o != null) { Info("Resources.FindObjectsOfTypeAll<" + t.Name + "> -> " + o.name + " (" + o.GetType().FullName + ")"); return o; }
          }
        }
      } catch (Exception e) { Warn("Resources.FindObjectsOfTypeAll<" + t.Name + "> упал: " + e.Message); }
      var inst = Refl.FieldOrProp(t, "Instance") ?? Refl.FieldOrProp(t, "instance") ?? Refl.FieldOrProp(t, "Current");
      if (inst != null && !(inst is Type)) return inst;
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
