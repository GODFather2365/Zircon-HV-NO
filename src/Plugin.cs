using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace ZirconHV {
  [BepInPlugin(GUID, NAME, VERSION)]
  [BepInDependency("com.kingwixly.multimissile", BepInDependency.DependencyFlags.SoftDependency)]
  public partial class Plugin : BaseUnityPlugin {
    public const string GUID="com.godfather2365.zirconhv", NAME="Zircon Heavy Variant", VERSION="1.0.1";
    public const string UniqueId = "Zircon_HV_GODFather";
    internal static ConfigEntry<float> YieldKt; internal static ConfigEntry<string> PrefabPath;
    internal static ConfigEntry<bool> EnableTwinFallback, OverrideOriginal, DumpApi;
    internal static ManualLogSource Log;
    public static Plugin Instance; public Plugin() { Instance = this; }

    void Awake() {
      Log = Logger;
      YieldKt = Config.Bind("Warhead","YieldKt",1000f,"Yield in kilotons. 1000 = 1 Mt. Radii scale ~ yield^(1/3).");
      PrefabPath = Config.Bind("Source","PrefabPath","Assets/Blueprinter/Mods/Multi - Missile/zircon nuke/zircon nuke.prefab","Exact asset name in the Multi-Missile .nobp (fallback: fuzzy match).");
      EnableTwinFallback = Config.Bind("Registration","TwinFallback",true,"If honest registration fails, use EventGate twin fallback (F1).");
      OverrideOriginal = Config.Bind("Registration","OverrideOriginal",false,"F3: patch original instead of cloning (conflicts with base weapon!).");
      DumpApi = Config.Bind("Debug","DumpBlueprinterApi",false,"true -> dump all Blueprinter-ish types/members to LogOutput.log (run once on live game, then report member names back to the author).");
      if (DumpApi.Value) DumpBlueprinterApi();

      EncyclopediaPatches.Apply(); // v1.0.1: раньше Apply() не вызывался нигде — Harmony-патч был мёртвым
      InitLoop();
      Log.LogInfo(NAME+" "+VERSION+" loaded. YieldKt="+YieldKt.Value);
    }
  }
}

namespace ZirconHV {
  public partial class Plugin {
    const float TickInterval = 2.5f;
    const int   MaxAttempts  = 72;          // 72 x 2.5s ~= 180 sec
    const float HeartbeatEvery = 20f;

    bool done;
    int attempt;                            
    float nextAt;                           
    float lastHeartbeat;
    string stage = "a) поиск бандла";
    bool reinjectPending;   // v1.0.1: повторный инжект пилонов после регистрации
    float reinjectNextAt;

    AssetBundle mmBundle;
    GameObject prefab;
    string assetName;
    object bundleMountSO;   
    object bundleDefSO;     
    public object enc;                             
    GameObject clone;                       
    bool cloned;

    void InitLoop() {
      nextAt = Time.realtimeSinceStartup + TickInterval;
      lastHeartbeat = Time.realtimeSinceStartup;
      Log.LogInfo("Цикл v6 запущен ВНУТРИ Plugin: Update()-тики каждые " + TickInterval + " сек.");
    }

    void Update() {
      // v1.0.1: после успешной регистрации НЕ останавливаемся — раз в 5 сек перепроходим
      // инжект пилонов, пока Hardpoint'ы борта реально не появятся в сцене (Encyclopedia.AfterLoad).
      if (done && reinjectPending) {
        float n = Time.realtimeSinceStartup;
        if (n >= reinjectNextAt) {
          reinjectNextAt = n + 5f;
          try { Registration.ReinjectHardpoints(clone, bundleMountSO); } catch (Exception e) { Log.LogWarning("Reinject: " + e.Message); }
          if (Registration.injDone) reinjectPending = false;
        }
        return;
      }
      if (done) return;
      float now = Time.realtimeSinceStartup;
      if (now < nextAt) return;
      nextAt += TickInterval;
      attempt++;
      try {
        Tick();
      } catch (Exception e) {
        Log.LogError("Попытка " + attempt + " (стадия " + stage + ") бросила исключение: " + e);
      }
      if (now - lastHeartbeat >= HeartbeatEvery) {
        lastHeartbeat = now;
        Log.LogInfo("Runner жив, попытка " + attempt + "/" + MaxAttempts + ", стадия: " + stage);
      }
      if (!done && attempt >= MaxAttempts) {
        done = true;
        Log.LogError("=== ИТОГ ПРОВАЛА ===");
        Log.LogError("Бюджет 180 сек исчерпан — опрос остановлен.");
      }
    }

    void Tick() {
      if (prefab == null) {
        stage = "a) поиск бандла";
        if (mmBundle == null) {
          mmBundle = Refl.FindMultiMissileBundleByApi();
          if (mmBundle == null) mmBundle = Refl.FindMultiMissileBundle();
        }
        if (mmBundle != null) {
          stage = "b) загрузка zircon-ассетов";
          var za = Refl.LoadZirconAssets(mmBundle);
          prefab = za.Prefab; assetName = za.PrefabName; bundleMountSO = za.MountSO; bundleDefSO = za.DefSO;
          if (prefab == null) mmBundle = null;
        }
        if (prefab == null) return;
        Registration.LastAssetName = assetName;
      }

      if (enc == null) {
        stage = "c) ожидание Encyclopedia";
        Type encType = Refl.FindTypeInCSharp("Encyclopedia");
        if (encType == null) return;
        
        UnityEngine.Object[] all = null;
        try {
          var m = typeof(Resources).GetMethod("FindObjectsOfTypeAll", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(Type) }, null);
          if (m != null) all = m.Invoke(null, new object[] { encType }) as UnityEngine.Object[];
        } catch (Exception e) { Log.LogError("Resources.FindObjectsOfTypeAll упал: " + e.Message); return; }
        
        if (all != null && all.Length > 0) {
          foreach (var o in all) if (o != null) { enc = o; break; }
        }
        if (enc == null) return;
      }

      if (!cloned) {
        stage = "d) клонирование префаба";
        clone = Refl.ClonePrefab(prefab);
        if (clone == null) return;
        clone.name = Plugin.UniqueId;
        clone.SetActive(false);
        Registration.RenameClone(clone, prefab);
        Warhead.ApplyTo(clone);
        MeshSwap.TryApplyExternalMesh(clone); 
        cloned = true;
      }

      // ШАГ 3 ИСПРАВЛЕН: Передаем null, чтобы не перезаписать исходный бандл донора!
      Registration.RewriteJsonKey(clone, null);

      stage = "e/f) регистрация + инжект";
      bool ok = Registration.RegisterAndInject(clone, enc, bundleMountSO, bundleDefSO);
      if (ok) { done = true; stage = "завершено"; }
      else if (Registration.injectedAtLeastOnce) {
        // v1.0.1: оружие зарегистрировано, но пилоны ещё неpatched (Hardpoint'ы не созданы).
        // Считаем регистрацию успешной и продолжаем перепробовать инжект в Update().
        done = true; reinjectPending = !Registration.injDone;
        stage = reinjectPending ? "регистрация ок, ждём Hardpoint'ы" : "завершено";
      }
    }
  }
}

namespace ZirconHV {
  public static partial class ApiDump {
    public static void Run(BepInEx.Logging.ManualLogSource log) { }
  }
}
