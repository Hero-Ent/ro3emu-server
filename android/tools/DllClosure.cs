#:package Mono.Cecil@0.11.6
// Prints the game DLLs (file names) that the given roots need, roots included: the set setup-project.ps1 lets the Unity
// editor load, so it can import the assets that use their scripts. Engine and base-class assemblies are left out (Unity
// provides them).
// Usage: dotnet run DllClosure.cs -- <Managed dir> <root.dll> [<root.dll>...]
using Mono.Cecil;
using System.Text.RegularExpressions;

var dir = args[0];
var engine = new Regex(@"^(UnityEngine(\.[A-Za-z0-9]+Module)?|UnityEditor.*|mscorlib|netstandard|System|System\.(Configuration|Core|Data|Drawing|IO\.Compression(\.FileSystem)?|Net\.Http|Numerics|Runtime\.Serialization|Transactions|Xml(\.Linq)?)|Mono\.Security)$");
var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
var pending = new Stack<string>(args.Skip(1).Select(Path.GetFileNameWithoutExtension)!);
while (pending.Count > 0)
{
    var name = pending.Pop();
    if (engine.IsMatch(name) || !result.Add(name)) continue;
    var path = Path.Combine(dir, name + ".dll");
    if (!File.Exists(path)) { result.Remove(name); continue; }
    using var asm = AssemblyDefinition.ReadAssembly(path);
    foreach (var r in asm.MainModule.AssemblyReferences) pending.Push(r.Name);
}
foreach (var n in result) Console.WriteLine(n + ".dll");
