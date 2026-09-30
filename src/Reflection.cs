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

    // ------------------------------------------------------------------
    // v3 mini-dump: when a candidate member is NOT found, dump ALL public
    // fields/methods of that type into the log so the next fix is final.
    // ------------------------------------------------------------------
    public static void DumpMembers(Type t) {
      if (t == null || Plugin.Log == null) return;
      try {
        var sb = new System.Text.StringBuilder();
        sb.Append("МИНИ-ДАМП ").Append(t.FullName).AppendLine(":");
        sb.Append("  поля: ");
        bool first = true;
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)) {
          if (f.IsLiteral || f.IsStatic && false) continue;
          if (!first) sb.Append(", "); first = false;
          sb.Append(f.FieldType.Name).Append(' ').Append(f.Name);
        }
        sb.AppendLine();
        sb.Append("  методы: ");
        first = true;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
          if (m.IsSpecialName) continue;
          if (!first) sb.Append(", "); first = false;
          sb.Append(m.Name).Append('(');
          var ps = m.GetParameters();
          for (int i = 0; i < ps.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name); }
          sb.Append(')');
        }
        sb.AppendLine();
        Info(sb.ToString());
      } catch (Exception e) { Warn("мини-дамп не удался: " + e.Message); }
    }

    /// <summary>
    /// Reads Blueprinter.BundleRegistry PUBLIC FIELD \"Bundles\" (F List&lt;LoadedBundle&gt; per API dump).
    /// LoadedBundle fields (per dump): bundleName(string), source(string), AssetBundle(UnityEngine.AssetBundle),
    /// Manifest(PatchManifest). Target: bundleName == \"multi - missile\".
    /// </summary>
    public static AssetBundle FindMultiMissileBundle() {
      Info("Ищу Blueprinter.BundleRegistry через GetTypes() по всем сборкам...");
      Type reg = FindType("Blueprinter.BundleRegistry");
      if (reg == null) { Err("BundleRegistry не найден — Blueprinter не загружен?"); return null; }
      Info("Нашел BundleRegistry = " + reg.FullName);

      object bundles = null;
      // v3 FACT: Bundles is a PUBLIC FIELD. Try field FIRST (static, then on singleton instance).
      var bf = reg.GetField("Bundles", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
      if (bf != null) { try { bundles = bf.GetValue(null); Info("Читаю статическое поле F List`1 Bundles..."); } catch (Exception e) { Warn("чтение статического Bundles: " + e.Message); } }
      if (bundles == null) {
        var inst = FieldOrProp(reg, "Instance") ?? FieldOrProp(reg, "instance") ?? FieldOrProp(reg, "Current");
        object holder = inst != null ? inst : (object)reg;
        bf = Field(holder as Type ?? holder.GetType(), "Bundles");
        if (bf != null) { try { bundles = bf.GetValue(holder is Type ? null : holder); Info("Читаю поле Bundles с экземпляра реестра..."); } catch (Exception e) { Warn("чтение инстанс-поля Bundles: " + e.Message); } }
      }
      if (bundles == null) {
        Err("Поле Bundles не найдено/пусто на BundleRegistry. Мини-дамп публичных членов:");
        DumpMembers(reg);
        return null;
      }
      Info("Читаю список Bundles (" + bundles.GetType().FullName + ")...");

      IEnumerable en = bundles as IEnumerable;
      if (en == null) { Err("Bundles не IEnumerable."); return null; }

      AssetBundle fallback = null;
      int seen = 0;
      Type lbType = null;
      foreach (var lb in en) {
        if (lb == null) continue;
        seen++;
        lbType = lb.GetType();
        string bn = FieldOrProp(lb, "bundleName") as string;   // v3 FACT: field bundleName
        AssetBundle ab = FieldOrProp(lb, "AssetBundle") as AssetBundle; // v3 FACT: field AssetBundle
        if (ab == null) {
          foreach (var f in lb.GetType().GetFields(All)) {
            if (f.FieldType == typeof(AssetBundle)) { try { ab = f.GetValue(lb) as AssetBundle; } catch { } if (ab != null) break; }
          }
        }
        if (bn == null) { try { bn = ab != null ? ab.name : null; } catch { } }
        if (bn == null) bn = "";
        Info("Бандл #" + seen + ": bundleName=\"" + bn + "\"" + (ab == null ? " (AssetBundle=null)" : ""));
        if (ab == null) continue;
        if (string.Equals(bn.Trim(), "multi - missile", StringComparison.OrdinalIgnoreCase) ||
            bn.IndexOf("multi - missile", StringComparison.OrdinalIgnoreCase) >= 0 ||
            bn.IndexOf("multi-missile", StringComparison.OrdinalIgnoreCase) >= 0 ||
            bn.IndexOf("multimissile", StringComparison.OrdinalIgnoreCase) >= 0) {
          Info("Нашел бандл Multi-Missile: bundleName=\"" + bn + "\"");
          return ab;
        }
        if (fallback == null && bn.IndexOf("missile", StringComparison.OrdinalIgnoreCase) >= 0) fallback = ab;
      }
      if (fallback != null) { Warn("Точного 'multi - missile' нет, беру бандл с 'missile' в bundleName."); return fallback; }
      Err("Бандл Multi-Missile среди " + seen + " записей BundleRegistry.Bundles не найден. Мини-дамп LoadedBundle:");
      if (lbType != null) DumpMembers(lbType); else Warn("(LoadedBundle: список пуст, элементов нет)");
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
