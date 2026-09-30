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
    object bundleMountSO;   // v6 ПРАВКА 2: WeaponMount SO прямо из бандла
    object bundleDefSO;     // v7: MissileDefinition SO прямо из бандла (если есть)
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
        Log.LogError("  Encyclopedia: " + (enc != null ? "найден" : "НЕ найден (Resources.FindObjectsOfTypeAll(Type) пуст во всех попытках)"));
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
          stage = "b) загрузка zircon-ассетов";
          Log.LogInfo("Попытка " + attempt + "/" + MaxAttempts + " [b]: GetAllAssetNames() бандла \"" + mmBundle.name + "\", имена с \"zircon\" гружу LoadAsset(name) и раскладываю по типам (v6 ПРАВКА 2)...");
          var za = Refl.LoadZirconAssets(mmBundle);
          prefab = za.Prefab; assetName = za.PrefabName; bundleMountSO = za.MountSO; bundleDefSO = za.DefSO;
          if (bundleMountSO != null) Log.LogInfo("[b] Найден исходный WeaponMount SO в бандле: \"" + za.MountSOName + "\" (тип " + bundleMountSO.GetType().FullName + ").");
          if (bundleDefSO != null) Log.LogInfo("[b] Найден исходный MissileDefinition SO в бандле: \"" + za.DefSOName + "\" (тип " + bundleDefSO.GetType().FullName + ") — v7: шаблон для AddUnit.");
          if (prefab == null) {
            Log.LogError("Попытка " + attempt + " [b]: бандл \"" + mmBundle.name + "\" найден, но GameObject-префаба с \"zircon\" в нём нет — сбрасываю бандл и ищу другой.");
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
        Log.LogInfo("Попытка " + attempt + "/" + MaxAttempts + " [c]: ищу Encyclopedia (v6 ПРАВКА 1: прямое не-generic Resources.FindObjectsOfTypeAll(Type), без Harmony-postfix)...");
        Type encType = Refl.FindTypeInCSharp("Encyclopedia");
        if (encType == null) {
          Log.LogError("Попытка " + attempt + " [c]: тип Encyclopedia в Assembly-CSharp не найден (продолжаю искать в следующих тиках).");
          return;
        }
        Log.LogInfo("Нашел тип Encyclopedia = " + encType.FullName);
        UnityEngine.Object[] all = null;
        try { all = UnityEngine.Resources.FindObjectsOfTypeAll(encType); }
        catch (Exception e) { Log.LogError("Попытка " + attempt + " [c]: Resources.FindObjectsOfTypeAll(Type) упал: " + e.Message); return; }
        if (all != null && all.Length > 0) {
          Log.LogInfo("[c] Resources.FindObjectsOfTypeAll(Encyclopedia) вернул " + all.Length + " объектов, беру первый ненулевой...");
          foreach (var o in all) if (o != null) { enc = o; break; }
        }
        if (enc == null) {
          Log.LogInfo("Попытка " + attempt + " [c]:尚无 Encyclopedia 实例，稍后再试 (FindObjectsOfTypeAll пуст)...");
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

      // v6 ПРАВКА 3: ДО AddWeaponMount переписываем jsonKey клона на "ZirconHV_1Mt"
      // (и все string-поля, равные старому jsonKey) — иначе словим Duplicate WeaponMount JSON key.
      Registration.RewriteJsonKey(clone, bundleMountSO);

      // (e)+(f) регистрация EncyclopediaLoader + инжект хардпоинтов
      stage = "e/f) регистрация + инжект";
      Log.LogInfo("Попытка " + attempt + "/" + MaxAttempts + " [e]: EncyclopediaLoader.AddWeaponMount/AddUnit + [f] InjectHardpointsV3...");
      bool ok = Registration.RegisterAndInject(clone, enc, bundleMountSO, bundleDefSO);
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
    // v6 ПРАВКА 4: инъекция в лоад-аут — ДОСЛОВНО по открытому коду Multi-Missile
    // (brimstonerotarypanelpatch.cs, BSD):
    //   Hardpoint.mount, Hardpoint.spawnedPrefab, Hardpoint.pylonOptions (массив);
    //   элемент pylonOptions: поля mount, renderer.
    //   Для каждого Hardpoint из Resources.FindObjectsOfTypeAll(hpT): если текущий
    //   mount != null и его info.weaponName содержит "Zircon" (оригинал MM) ->
    //   новый массив pylonOptions длиной +1, копия ПОСЛЕДНЕЙ опции,
    //   optMountF.SetValue(копия, mountClone), optRenderF.SetValue(копия, null),
    //   optsF.SetValue(hp, новый массив). Add-only, как MK-88 Hydra.
    // ---------------------------------------------------------------
    static bool InjectHardpointsV3(object mountClone) {
      if (mountClone == null) { Err("InjectHardpointsV3: mountClone=null."); return false; }
      Type hpT = Refl.FindTypeInCSharp("Hardpoint");
      if (hpT == null) { Err("Тип Hardpoint не найден в Assembly-CSharp."); return false; }

      FieldInfo mountF = hpT.GetField("mount", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
      FieldInfo optsF  = hpT.GetField("pylonOptions", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
      if (mountF == null || optsF == null) {
        Err("У Hardpoint не найдены поля mount/pylonOptions (mount=" + (mountF != null) + ", pylonOptions=" + (optsF != null) + "). Мини-дамп ВСЕХ публичных членов Hardpoint:");
        Refl.DumpMembers(hpT);
        return false;
      }
      Info("Нашел поле Hardpoint.mount : " + mountF.FieldType.FullName);
      Info("Нашел поле Hardpoint.pylonOptions : " + optsF.FieldType.FullName);
      Type optT = optsF.FieldType.IsArray ? optsF.FieldType.GetElementType() : null;
      if (optT == null) { Err("Hardpoint.pylonOptions — не массив (" + optsF.FieldType.FullName + "). Мини-дамп Hardpoint:"); Refl.DumpMembers(hpT); return false; }
      FieldInfo optMountF  = optT.GetField("mount", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
      FieldInfo optRenderF = optT.GetField("renderer", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
      if (optMountF == null || optRenderF == null) {
        Err("У элемента pylonOptions (" + optT.Name + ") не найдены поля mount/renderer (mount=" + (optMountF != null) + ", renderer=" + (optRenderF != null) + "). Мини-дамп " + optT.Name + ":");
        Refl.DumpMembers(optT);
        return false;
      }
      Info("Нашел поле " + optT.Name + ".mount : " + optMountF.FieldType.FullName);
      Info("Нашел поле " + optT.Name + ".renderer : " + optRenderF.FieldType.FullName);

      UnityEngine.Object[] hps = null;
      try { hps = UnityEngine.Resources.FindObjectsOfTypeAll(hpT); }
      catch (Exception e) { Err("Resources.FindObjectsOfTypeAll(Hardpoint) упал: " + e.Message); return false; }
      Info("Resources.FindObjectsOfTypeAll(Hardpoint) вернул " + hps.Length + " объектов. Ищу те, у которых info.weaponName содержит \"Zircon\"...");

      int extended = 0, matched = 0;
      foreach (var o in hps) {
        if (o == null) continue;
        try {
          object hp = o;
          object curMount = null;
          try { curMount = mountF.GetValue(hp); } catch { }
          if (curMount == null) continue;               // требование: текущий mount != null
          string wn = GetWeaponName(curMount);          // info.weaponName через рефлексию
          if (wn == null || wn.IndexOf("Zircon", StringComparison.OrdinalIgnoreCase) < 0) continue;
          matched++;
          Array opts = optsF.GetValue(hp) as Array;
          if (opts == null) { Warn("Hardpoint с mount \"" + wn + "\": pylonOptions=null, расширять не из чего."); continue; }
          if (opts.Length == 0) { Warn("Hardpoint с mount \"" + wn + "\": pylonOptions пуст — копии последней опции нет, пропускаю."); continue; }
          // v7 idempotency: skip hardpoints that already carry our clone option
          bool already = false;
          for (int q = 0; q < opts.Length; q++) {
            var om = optMountF.GetValue(opts.GetValue(q)) as UnityEngine.Object;
            if (ReferenceEquals(om, mountClone)) { already = true; break; }
          }
          if (already) { Info("Hardpoint (\"" + ((UnityEngine.Object)hp).name + "\"): наша опция уже в pylonOptions[" + opts.Length + "] — пропускаю (idempotent)."); continue; }
          object last = opts.GetValue(opts.Length - 1);
          object copy = ShallowCopyOption(last, optT);
          if (copy == null) continue;
          optMountF.SetValue(copy, mountClone);         // наш клон WeaponMount
          optRenderF.SetValue(copy, null);              // рендер оригинала не тащим
          var newArr = Array.CreateInstance(optT, opts.Length + 1);
          for (int q = 0; q < opts.Length; q++) newArr.SetValue(opts.GetValue(q), q);
          newArr.SetValue(copy, opts.Length);
          optsF.SetValue(hp, newArr);                   // add-only
          extended++;
          Info("Hardpoint (\"" + ((UnityEngine.Object)hp).name + "\"): pylonOptions[" + opts.Length + "] добавлен, mount->jsonKey=\"" + CloneJsonKey + "\" (исходный weaponName=\"" + wn + "\").");
        } catch (Exception e) { Warn("Инъекция в один Hardpoint упала: " + e.Message); }
      }
      LastInjectedSets = extended;
      if (extended > 0) { Info("HardpointInjector: расширено хардпоинтов = " + extended + " (совпало с \"Zircon\": " + matched + ")."); injDone = true; return true; }
      if (matched > 0 && !injDone) Warn("Ни один Hardpoint не расширен (совпадений \"Zircon\" = " + matched + ", но наша опция уже стоит/пирамиды пусты).");
      // v7: hardpoints appear only after the player opens a hangar / spawns aircraft.
      // If we never saw a single "Zircon" match yet, keep stage e/f alive (return false)
      // so the loop retries every tick until budget ends — otherwise the mod would mark
      // itself done while the weapon is nowhere in the game.
      if (matched == 0) return false;
      return injDone;
    }

    static bool injDone;   // v7: sticky flag — injection succeeded at least once

    /// <summary>info.weaponName of a WeaponMount via reflection (field or property chain).</summary>
    static string GetWeaponName(object mount) {
      if (mount == null) return null;
      try {
        object info = Refl.FieldOrProp(mount, "info");
        if (info != null) {
          var wn = Refl.FieldOrProp(info, "weaponName") as string;
          if (wn != null) return wn;
        }
        var direct = Refl.FieldOrProp(mount, "weaponName") as string;
        if (direct != null) return direct;
      } catch { }
      return null;
    }

    /// <summary>Shallow field-by-field copy of one pylonOptions element (struct or class).</summary>
    static object ShallowCopyOption(object src, Type optT) {
      try {
        object copy = optT.IsValueType ? Activator.CreateInstance(optT) : Activator.CreateInstance(optT, true);
        foreach (var f in optT.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
          if (f.IsLiteral) continue;
          try { f.SetValue(copy, f.GetValue(src)); } catch { }
        }
        return copy;
      } catch (Exception e) { Warn("ShallowCopyOption(" + optT.Name + ") упал: " + e.Message); return null; }
    }


    // ---------------------------------------------------------------
    // Register + inject (called repeatedly until success) — v7 flow:
    // WeaponMount/MissileDefinition берутся КАК СОБСТВЕННЫЕ КОМПОНЕНТЫ КЛОНА
    // (Instantiate глубокого копирует и MonoBehaviour-компоненты, и вложенные
    // ScriptableObjects через сериализованные ссылки) — это ровно то, что
    // реально принимает EncyclopediaLoader.AddWeaponMount/AddUnit.
    // SO из бандла — только источник имени для диагностики.
    // Далее EncyclopediaLoader.AddWeaponMount/AddUnit, прямой add как фолбэк;
    // инжекция pylonOptions (add-only) — с повтором на каждом тике, чтобы
    // захватить хардпоинты, заспавненные позже.
    // ---------------------------------------------------------------
    public static bool RegisterAndInject(GameObject clone, object enc, object bundleMountSO, object bundleDefSO) {
      // diagnostic names from the bundle SOs (v6 ПРАВКА 2 results), not used as templates
      if (bundleMountSO != null) Info("[e] WeaponMount SO из бандла: \"" + ((UnityEngine.Object)bundleMountSO).name + "\" (только имя для диагностики, шаблон берём из клона).");
      if (bundleDefSO != null) Info("[e] MissileDefinition SO из бандла: \"" + ((UnityEngine.Object)bundleDefSO).name + "\" (только имя для диагностики, шаблон берём из клона).");

      // --- MissileDefinition / WeaponMount clones = components of the instantiated prefab ---
      var defs = Refl.ComponentsNamed(clone, "MissileDefinition");
      var mounts = Refl.ComponentsNamed(clone, "WeaponMount");
      if (defs.Length == 0) Err("В клоне нет компонента MissileDefinition — AddUnit будет пропущен (см. мини-дамп ниже, если и WeaponMount нет).");
      if (mounts.Length == 0) Err("В клоне нет компонента WeaponMount — AddWeaponMount/инжекция невозможны.");

      object defClone   = defs.Length   > 0 ? defs[0]   : null;   // Instantiate already made it a private copy
      object mountClone = mounts.Length > 0 ? mounts[0] : null;

      if (defClone != null) { Warhead.ApplyToBlastYield(defClone); StampUnique(defClone); }
      if (mountClone != null) StampUnique(mountClone);
      if (defClone != null && mountClone != null) {
        // link cloned mount -> cloned definition if such a reference field exists
        foreach (var f in mountClone.GetType().GetFields(Refl.All))
          if (f.FieldType.IsInstanceOfType(defClone)) { try { f.SetValue(mountClone, defClone); Info("Связал WeaponMount." + f.Name + " -> клон MissileDefinition."); } catch { } }
      }
      if (mountClone == null || defClone == null) {
        Type probe = mountClone != null ? mountClone.GetType() : (defClone != null ? defClone.GetType() : typeof(Component));
        Err("Не хватает " + (mountClone == null ? "WeaponMount" : "MissileDefinition") + " среди компонентов клона. Мини-дамп типа " + probe.FullName + ":");
        Refl.DumpMembers(probe);
      }

      // --- PRIMARY: Blueprinter.Ops.EncyclopediaLoader.AddWeaponMount / AddUnit ---
      bool opsOk = RegisterViaOps(clone, enc, defClone, mountClone);
      bool anyOk = opsOk;

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

      // --- HardpointSet injection via pylonOptions (MK-88 Hydra add-only pattern) ---
      // v7: runs EVERY tick even after Ops succeeded (aircraft/hangars spawn later),
      // so stage e/f only finishes when at least one hardpoint accepted the mount.
      bool injOk = InjectHardpointsV3(mountClone);
      anyOk |= injOk;

      if (anyOk && injOk) {
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
      return anyOk && injOk;   // v7: done only when the weapon is also on real hardpoints
    }

    static void StampUnique(object o) {
      if (o == null) return;
      foreach (var f in o.GetType().GetFields(Refl.All)) {
        if (f.FieldType == typeof(string) && IsIdField(f.Name)) { try { f.SetValue(o, Plugin.UniqueId); } catch { } }
      }
    }

    // ---------------------------------------------------------------
    // v6 ПРАВКА 3: уникальный jsonKey ДО EncyclopediaLoader.AddWeaponMount.
    // Читаем старый jsonKey (поле/свойство "jsonKey" на WeaponMount SO из бандла или
    // на компонентах клона), затем переписываем ЕГО ЗНАЧЕНИЕ на "ZirconHV_1Mt" во всех
    // string-полях/свойствах с именем jsonKey и во всех string-полях, равных старому
    // ключу. Без этого AddWeaponMount даёт "Duplicate WeaponMount JSON key".
    // ---------------------------------------------------------------
    public const string CloneJsonKey = "ZirconHV_1Mt";

    public static void RewriteJsonKey(GameObject clone, object bundleMountSO) {
      Info("v6 ПРАВКА 3: читаю старый jsonKey клона (поле/свойство \"jsonKey\")...");
      string oldKey = FindJsonKey(clone, bundleMountSO);
      Info("Старый jsonKey = \"" + (oldKey ?? "?") + "\". Переписываю на \"" + CloneJsonKey + "\" (jsonKey + все string-поля, равные старому ключу)...");
      int n = 0;
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
          bool hit = string.Equals(f.Name, "jsonKey", StringComparison.OrdinalIgnoreCase)
                  || (oldKey != null && oldKey.Length > 0 && v == oldKey);
          if (!hit) continue;
          try { f.SetValue(t0, CloneJsonKey); n++; Info("  " + t.Name + "." + f.Name + ": \"" + v + "\" -> \"" + CloneJsonKey + "\""); }
          catch (Exception e) { Warn("  не смог переписать " + t.Name + "." + f.Name + ": " + e.Message); }
        }
        foreach (var pr in t.GetProperties(Refl.All)) {
          if (pr.PropertyType != typeof(string) || !pr.CanWrite) continue;
          try { if (pr.GetIndexParameters().Length != 0) continue; } catch { continue; }
          string v = null; try { v = (string)pr.GetValue(t0, null); } catch { continue; }
          if (v == null) continue;
          bool hit = string.Equals(pr.Name, "jsonKey", StringComparison.OrdinalIgnoreCase)
                  || (oldKey != null && oldKey.Length > 0 && v == oldKey);
          if (!hit) continue;
          try { pr.SetValue(t0, CloneJsonKey, null); n++; Info("  " + t.Name + "." + pr.Name + " (prop): \"" + v + "\" -> \"" + CloneJsonKey + "\""); } catch { }
        }
      }
      LastJsonKey = CloneJsonKey;   // v6 ПРАВКА 3: для fail-summary и лога успеха
      if (n == 0) Warn("Ни одного поля jsonKey/старого ключа не найдено — AddWeaponMount может дать Duplicate key. Логирую как есть.");
      else Info("Готово: переписано полей = " + n + ", jsonKey клона = \"" + CloneJsonKey + "\".");
    }

    static string FindJsonKey(GameObject clone, object bundleMountSO) {
      var probes = new List<object>();
      if (bundleMountSO != null) probes.Add(bundleMountSO);
      if (clone != null) probes.AddRange(clone.GetComponentsInChildren<Component>(true));
      foreach (var o in probes) {
        if (o == null) continue;
        var v = Refl.FieldOrProp(o, "jsonKey") as string;
        if (!string.IsNullOrEmpty(v)) return v;
      }
      return null;
    }


    // ---------------- helpers ----------------

    /// <summary>
    /// v6 ПРАВКА 1: поиск Encyclopedia БЕЗ Harmony-postfix (postfix убран из потока).
    /// Только поллинг Resources.FindObjectsOfTypeAll (используется Ops-fallback путём;
    /// основной стадией (c) поллит прямо Plugin.Tick()).
    /// </summary>
    /// </summary>
public static object FindEncyclopedia(Type encType) {
      if (encType == null) return null;
      var found = FindSceneInstance(encType);
      if (found != null) Info("Encyclopedia получен поллингом (Resources.FindObjectsOfTypeAll).");
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
