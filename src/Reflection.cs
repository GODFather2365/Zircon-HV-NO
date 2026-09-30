using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ZirconHV {
  /// <summary>
  /// Safe reflection helpers. NO compile-time references to Blueprinter / Multi-Missile types
  /// (lesson of Kestrel-40 TypeLoadException: R3 mitigation).
  /// </summary>
  public static class Reflection {
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Static | BindingFlags.Instance
                             | BindingFlags.FlattenHierarchy;

    /// <summary>Find a type by full or partial name across all loaded assemblies.</summary>
    public static Type FindType(params string[] names) {
      foreach (var asmb in AppDomain.CurrentDomain.GetAssemblies()) {
        Type t = null;
        try { t = asmb.GetType(names[0]); } catch { }
        if ((object)t != null) return t;
        Type[] types;
        try { types = asmb.GetTypes(); } catch (ReflectionTypeLoadException e) { types = SafeTypes(e); }
        catch { continue; }
        foreach (var tt in types) {
          if ((object)tt == null) continue;
          foreach (var n in names) {
            if (string.Equals(tt.FullName, n, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tt.Name, n, StringComparison.OrdinalIgnoreCase))
              return tt;
          }
        }
      }
      return null;
    }

    static Type[] SafeTypes(ReflectionTypeLoadException e) {
      var list = new List<Type>();
      try { foreach (var t in e.Types) if ((object)t != null) list.Add(t); } catch { }
      return list.ToArray();
    }

    public static FieldInfo Field(Type t, string name) {
      for (; (object)t != null; t = t.BaseType) {
        var f = t.GetField(name, All);
        if ((object)f != null) return f;
      }
      return null;
    }

    public static PropertyInfo Prop(Type t, string name) {
      for (; (object)t != null; t = t.BaseType) {
        var p = t.GetProperty(name, All);
        if ((object)p != null) return p;
      }
      return null;
    }

    /// <summary>Read a field or property value by name (instance), returns null if absent.</summary>
    public static object FieldOrProp(object target, string name) {
      if (target == null) return null;
      var t = target is Type ? (Type)target : target.GetType();
      var f = Field(t, name);
      if ((object)f != null) { try { return f.GetValue(target is Type ? null : target); } catch { } }
      var p = Prop(t, name);
      if ((object)p != null && p.CanRead) { try { return p.GetValue(target, null); } catch { } }
      return null;
    }

    /// <summary>Write a field or property by name anywhere in the hierarchy.</summary>
    public static bool SetDeep(object target, string name, object value) {
      if (target == null) return false;
      var t = target is Type ? (Type)target : target.GetType();
      var f = Field(t, name);
      if ((object)f != null) {
        try { f.SetValue(target is Type ? null : target, value); return true; } catch { }
      }
      var p = Prop(t, name);
      if ((object)p != null && p.CanWrite) {
        try { p.SetValue(target, value, null); return true; } catch { }
      }
      return false;
    }

    /// <summary>Invoke a static method by name with loose parameter matching.</summary>
    public static object CallStatic(Type t, string methodName, params object[] args) {
      if ((object)t == null) return null;
      foreach (var m in t.GetMethods(All)) {
        if (!m.IsStatic) continue;
        if (!string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)) continue;
        var ps = m.GetParameters();
        if (ps.Length != (args == null ? 0 : args.Length)) continue;
        try { return m.Invoke(null, args); } catch { /* try next overload */ }
      }
      return null;
    }

    /// <summary>
    /// Recursively rewrite every string field/property equal to oldValue into newValue
    /// (unique-id enforcement, R4). Depth-limited to avoid cycles.
    /// Returns number of rewrites performed.
    /// </summary>
    public static int RewriteStringIds(object obj, string oldValue, string newValue, int maxDepth) {
      return RewriteInternal(obj, oldValue, newValue, 0, maxDepth, new HashSet<object>(new ReferenceEqualityComparer()));
    }

    static int RewriteInternal(object obj, string oldV, string newV, int depth, int maxDepth, HashSet<object> seen) {
      if (obj == null || depth > maxDepth) return 0;
      if (obj is ValueType && !(obj is IEnumerable)) return 0;
      if (!(obj is string) && !seen.Add(obj)) return 0;

      int count = 0;
      var t = obj.GetType();

      // direct fields
      foreach (var f in t.GetFields(All)) {
        try {
          if (f.FieldType == typeof(string)) {
            var s = (string)f.GetValue(obj is Type ? null : obj);
            if (s == oldV) { f.SetValue(obj is Type ? null : obj, newV); count++; }
          } else if (IsTraversable(f.FieldType)) {
            count += RewriteInternal(f.GetValue(obj is Type ? null : obj), oldV, newV, depth + 1, maxDepth, seen);
          }
        } catch { }
      }
      foreach (var p in t.GetProperties(All)) {
        try {
          if (!p.CanRead) continue;
          var idx = p.GetIndexParameters();
          if (idx != null && idx.Length > 0) continue;
          var v = p.GetValue(obj, null);
          if (p.PropertyType == typeof(string) && p.CanWrite && (string)v == oldV) {
            p.SetValue(obj, newV, null); count++;
          } else if (IsTraversable(p.PropertyType)) {
            count += RewriteInternal(v, oldV, newV, depth + 1, maxDepth, seen);
          }
        } catch { }
      }
      // collections / dictionaries
      if (obj is IDictionary) {
        foreach (DictionaryEntry de in (IDictionary)obj) {
          count += RewriteInternal(de.Value, oldV, newV, depth + 1, maxDepth, seen);
        }
      } else if (obj is IEnumerable && !(obj is string)) {
        foreach (var item in (IEnumerable)obj) {
          count += RewriteInternal(item, oldV, newV, depth + 1, maxDepth, seen);
        }
      }
      return count;
    }

    static bool IsTraversable(Type t) {
      if (t == null) return false;
      if (t.IsPrimitive || t == typeof(decimal) || t == typeof(DateTime)) return false;
      return t.IsClass || typeof(IEnumerable).IsAssignableFrom(t);
    }

    sealed class ReferenceEqualityComparer : IEqualityComparer<object> {
      public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
      public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
    }
    public static readonly IEqualityComparer<object> ReferenceComparerInstance = new ReferenceEqualityComparer();
  }
}
