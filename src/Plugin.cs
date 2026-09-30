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
    public const string GUID="com.godfather2365.zirconhv", NAME="Zircon Heavy Variant", VERSION="1.0.0";
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
      EncyclopediaPatches.Apply(); // v3: postfix on Encyclopedia.AfterLoad (Tsar Bomba pattern)
      // v6: НИКАКОГО отдельного GameObject/Runner-компонента — игра удаляла ZirconHV_Root
      // сразу после "Chainloader startup complete" (OnDestroy на попытке 0).
      // Конечный автомат стадий a-f живёт в Update() самого Plugin (BaseUnityPlugin —
      // MonoBehaviour, созданный Chainloader'ом, не уничтожается до выхода из игры).
      InitLoop();
      Log.LogInfo(NAME+" "+VERSION+" loaded. YieldKt="+YieldKt.Value);
    }
  }
}

namespace ZirconHV {
  public static partial class ApiDump {
    public static void Run(BepInEx.Logging.ManualLogSource log) { }
  }
}