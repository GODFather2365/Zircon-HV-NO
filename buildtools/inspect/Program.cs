using Mono.Cecil;
class I2 {
    static void Main(string[] a) {
        foreach (var f in a) {
            var def = AssemblyDefinition.ReadAssembly(f);
            Console.WriteLine(def.Name.Name + " v" + def.Name.Version);
            foreach (var r in def.MainModule.AssemblyReferences)
                if (r.Name.Contains("UnityEngine") || r.Name=="mscorlib" || r.Name=="netstandard" || r.Name=="System")
                    Console.WriteLine("   ref: " + r.Name + " v" + r.Version);
        }
    }
}
