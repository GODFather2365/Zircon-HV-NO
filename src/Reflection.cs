using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  /// <summary>
  /// Safe reflection helpers v2 (rewrite per first-run API dump analysis).
  /// NO compile-time references to Blueprinter / Multi-Missile / Assembly-CSharp types
  /// (lesson of Kestrel-40 TypeLoadException: R3 mitigation).
  /// </summary>
  public static class Refl {
    public const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Static | BindingFlags.Instance
                             | BindingFlags.FlattenHierarchy;

    static void Info(string s)  { if (Plugin.Log != null) Plugin.Log.LogInfo(s); }
    static void Warn(string s)  { if (Plugin.Log != null) Plugin.Log.LogWarning(s); }
    static void Err(string s)   { if (Plugin.Log != null) Plugin.Log.LogError(s); }

    static Assembly[] GameAssemblies() {
      var list = new List<Assembly>();
      foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) {
        string n; try { n = a.GetName().Name; } catch { continue; }
        if (n == "Assembly-CSharp" || n.IndexOf("blueprinter", StringComparison.OrdinalIgnoreCase) >= 0)
          list.Add(a);
      }
      // Assembly-CSharp first
      list.Sort(delegate (Assembly x, Assembly y) {
        bool xm = x.GetName().Name == "Assembly-CSharp", ym = y.GetName().Name == "Assembly-CSharp";
        return (xm == ym) ? 0 : (xm ? -1 : 1);
      });
      return list.ToArray();
    }

    static Type[] TypesOf(Assembly a) {
      try { return a.GetTypes(); }
      catch (ReflectionTypeLoadException e) {
        var l = new List<Type>();
        try { foreach (var t in e.Types) if (t != null) l.Add(t); } catch { }
        return l.ToArray();
      } catch { return new Type[0]; }
    }

    /// <summary>
    /// Find a type by SHORT name across Assembly-CSharp + Blueprinter assemblies,
    /// ignoring namespaces (WeaponMount may live in NuclearOption.Weapons etc.).
    /// Prefers non-abstract concrete classes.
    /// </summary>
    public static Type FindTypeInCSharp(string simpleName) {
      Info("Ищу тип " + simpleName + "...");
      Type best = null;
      foreach (var a in GameAssemblies()) {
        foreach (var t in TypesOf(a)) {
          if (t == null) continue;
          if (!string.Equals(t.Name, simpleName, StringComparison.OrdinalIgnoreCase)) continue;
          if (best == null) best = t;
          else if (best.IsAbstract && !t.IsAbstract) best = t; // prefer concrete
        }
      }
      if (best == null) Err("Тип " + simpleName + " НЕ найден ни в Assembly-CSharp, ни в Blueprinter.");
      else Info("Нашел тип " + simpleName + " = " + best.FullName + " (" + best.Assembly.GetName().Name + ")");
      return best;
    }

    /// <summary>Find a type by full name anywhere (kept for BundleRegistry lookup fallback).</summary>
    public static Type FindType(params string[] names) {
      foreach (var asmb in AppDomain.CurrentDomain.GetAssemblies()) {
        foreach (var n in names) {
          Type t = null;
          try { t = asmb.GetType(n); } catch { }
          if (t != null) return t;
        }
        foreach (var tt in TypesOf(asmb)) {
          if (tt == null) continue;
          foreach (var n in names)
            if (string.Equals(tt.FullName, n, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tt.Name, n, StringComparison.OrdinalIgnoreCase))
              return tt;
        }
      }
      return null;
    }

    public static FieldInfo Field(Type t, string name) {
      for (; t != null; t = t.BaseType) {
        var f = t.GetField(name, All);
        if (f != null) return f;
      }
      return null;
    }

    public static PropertyInfo Prop(Type t, string name) {
      for (; t != null; t = t.BaseType) {
        var p = t.GetProperty(name, All);
        if (p != null) return p;
      }
      return null;
    }

    public static object FieldOrProp(object target, string name) {
      if (target == null) return null;
      var t = target is Type ? (Type)target : target.GetType();
      var f = Field(t, name);
      if (f != null) { try { return f.GetValue(target is Type ? null : target); } catch { } }
      var p = Prop(t, name);
      if (p != null && p.CanRead) { try { return p.GetValue(target, null); } catch { } }
      return null;
    }

    public static bool SetDeep(object target, string name, object value) {
      if (target == null) return false;
      var t = target is Type ? (Type)target : target.GetType();
      var f = Field(t, name);
      if (f != null) { try { f.SetValue(target is Type ? null : target, value); return true; } catch { } }
      var p = Prop(t, name);
      if (p != null && p.CanWrite) { try { p.SetValue(target, value, null); return true; } catch { } }
      return false;
    }

    public static MethodInfo Method(Type t, string name) {
      for (; t != null; t = t.BaseType) {
        foreach (var m in t.GetMethods(All))
          if (m.Name == name) return m;
      }
      return null;
    }

    public static object Call(object target, Type t, string name, params object[] args) {
      var m = Method(t ?? (target == null ? null : target.GetType()), name);
      if (m == null) return null;
      try { return m.Invoke(m.IsStatic ? null : target, args); }
      catch (TargetInvocationException tie) { Err("Метод " + name + " бросил: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message)); }
      catch (Exception e) { Err("Метод " + name + " не вызван: " + e.Message); }
      return null;
    }

    /// <summary>Every component on the GameObject whose type short-name matches, incl. inactive children.</summary>
    public static Component[] ComponentsNamed(GameObject go, string simpleTypeName) {
      var t = FindTypeInCSharp(simpleTypeName);
      if (go == null || t == null) return new Component[0];
      var list = new List<Component>();
      foreach (var c in go.GetComponentsInChildren(t, true))
        if (c != null) list.Add(c);
      return list.ToArray();
    }

    // ------------------------------------------------------------------
    // Multi-Missile bundle discovery via Blueprinter.BundleRegistry.Bundles
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads Blueprinter.BundleRegistry property/field "Bundles" (List&lt;LoadedBundle&gt;),
    /// finds the LoadedBundle whose AssetBundle name contains "multi - missile" (or "missile"),
    /// returns its .AssetBundle (UnityEngine.Object). Null if not found.
    /// </summary>
    public static AssetBundle FindMultiMissileBundle() {
      Info("Ищу Blueprinter.BundleRegistry через GetTypes() по всем сборкам...");
      Type reg = FindType("Blueprinter.BundleRegistry");
      if (reg == null) { Err("BundleRegistry не найден — Blueprinter не загружен?"); return null; }
      Info("Нашел BundleRegistry = " + reg.FullName);

      object bundles = null;
      // instance member on a singleton?
      var inst = FieldOrProp(reg, "Instance") ?? FieldOrProp(reg, "instance") ?? FieldOrProp(reg, "Current");
      object holder = inst != null ? inst : (object)reg;
      bundles = FieldOrProp(holder, "Bundles");
      if (bundles == null) bundles = FieldOrProp(reg, "bundles");
      if (bundles == null) { Err("Свойство Bundles не найдено на BundleRegistry."); return null; }
      Info("Читаю список Bundles (" + bundles.GetType().FullName + ")...");

      IEnumerable en = bundles as IEnumerable;
      if (en == null) { Err("Bundles не IEnumerable."); return null; }

      AssetBundle fallback = null;
      int seen = 0;
      foreach (var lb in en) {
        if (lb == null) continue;
        seen++;
        AssetBundle ab = null;
        // LoadedBundle.AssetBundle / .bundle / implicit field of AB type
        foreach (var cand in new[] { "AssetBundle", "assetBundle", "Bundle", "bundle" }) {
          ab = FieldOrProp(lb, cand) as AssetBundle;
          if (ab != null) break;
        }
        if (ab == null) {
          // any field typed UnityEngine.AssetBundle
          foreach (var f in lb.GetType().GetFields(All)) {
            if (f.FieldType == typeof(AssetBundle)) { try { ab = f.GetValue(lb) as AssetBundle; } catch { } if (ab != null) break; }
          }
        }
        if (ab == null) continue;
        string nm = "";
        try { nm = ab.name ?? ""; } catch { }
        if (nm.Length == 0) { var pn = FieldOrProp(lb, "Name") as string; if (pn != null) nm = pn; }
        Info("Бандл #" + seen + ": \"" + nm + "\"");
        if (nm.IndexOf("multi - missile", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nm.IndexOf("multi-missile", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nm.IndexOf("multimissile", StringComparison.OrdinalIgnoreCase) >= 0) {
          Info("Нашел бандл Multi-Missile: \"" + nm + "\"");
          return ab;
        }
        if (fallback == null && nm.IndexOf("missile", StringComparison.OrdinalIgnoreCase) >= 0) fallback = ab;
      }
      if (fallback != null) { Warn("Точного 'multi - missile' нет, беру бандл с 'missile' в имени."); return fallback; }
      Err("Бандл Multi-Missile среди " + seen + " записей BundleRegistry не найден.");
      return null;
    }

    /// <summary>Instantiate a prefab asset (works for both scene-object and ScriptableObject-ish prefabs).</summary>
    public static GameObject ClonePrefab(GameObject original) {
      if (original == null) return null;
      try { return UnityEngine.Object.Instantiate(original); } catch (Exception e) { Err("Instantiate упал: " + e.Message); return null; }
    }

    /// <summary>ScriptableObject.CreateInstance by type, with fallback to Object.Clone of a template.</summary>
    public static object CreateLike(Type soType, object template) {
      try {
        if (typeof(ScriptableObject).IsAssignableFrom(soType)) {
          var mi = typeof(ScriptableObject).GetMethod("CreateInstance", new[] { typeof(Type) });
          if (mi != null) return mi.Invoke(null, new object[] { soType });
        }
      } catch (Exception e) { Warn("CreateInstance(" + soType.Name + ") не удался: " + e.Message); }
      try {
        if (template is UnityEngine.Object) return UnityEngine.Object.Instantiate((UnityEngine.Object)template);
      } catch (Exception e) { Warn("Instantiate(template) не удался: " + e.Message); }
      try {
        if (template is UnityEngine.Object) { var m = typeof(UnityEngine.Object).GetMethod("InstantiateInternal", All); if (m != null) return m.Invoke(null, new[] { template }); }
      } catch { }
      // last resort: shallow copy via MemberwiseClone-like Activator + field copy
      try {
        var inst = Activator.CreateInstance(soType, true);
        foreach (var f in soType.GetFields(All | BindingFlags.DeclaredOnly)) {
          if (f.IsLiteral || f.IsStatic) continue;
          try { f.SetValue(inst, f.GetValue(template)); } catch { }
        }
        Warn("Сделал shallow-copy через Activator для " + soType.Name);
        return inst;
      } catch (Exception e) { Err("Клонирование " + soType.Name + " невозможно: " + e.Message); return null; }
    }
  }
}
