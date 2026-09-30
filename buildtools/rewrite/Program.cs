using Mono.Cecil;

// Normalizes a game-dump assembly so MSBuild's net35 RAR can consume it:
// 1) rewrites the assembly's OWN identity version to 4.0.0.0 (if lower);
// 2) rewrites every BCL reference (mscorlib/System*/netstandard) to v4;
// 3) rewrites UnityEngine module references (Version=0.0.0.0) to 4.0.0.0,
//    because our normalized copies of those modules now carry identity 4.0.0.0.
class P {
    class LooseResolver : IAssemblyResolver {
        readonly string dir;
        public LooseResolver(string d) { dir = d; }
        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters p) {
            var f = Path.Combine(dir, name.Name + ".dll");
            if (!File.Exists(f)) return null;                 // tolerate unresolvable refs
            try { return AssemblyDefinition.ReadAssembly(f, p); } catch { return null; }
        }
        public void Dispose() { }
    }
    static bool ShouldBump(string n, Version v) {
        if (n == "mscorlib" || n == "netstandard") return true;
        if (n.StartsWith("System")) return true;              // .NET System.* assemblies
        if (n.StartsWith("UnityEngine") && v.Major == 0) return true; // dumped modules: 0.0.0.0 -> 4.0.0.0
        return false;
    }
    static void Main(string[] args) {
        string src = args[0], dstDir = args[1];
        Directory.CreateDirectory(dstDir);
        string dir = Path.GetDirectoryName(Path.GetFullPath(src));
        var asm = AssemblyDefinition.ReadAssembly(src, new ReaderParameters {
            ReadingMode = ReadingMode.Immediate, AssemblyResolver = new LooseResolver(dir) });
        if (asm.Name.Version.Major < 4) asm.Name.Version = new Version(4,0,0,0);
        foreach (var ar in asm.MainModule.AssemblyReferences)
            if (ShouldBump(ar.Name, ar.Version)) ar.Version = new Version(4,0,0,0);
        asm.Write(Path.Combine(dstDir, Path.GetFileName(src)));
    }
}
