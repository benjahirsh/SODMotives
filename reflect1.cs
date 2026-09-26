using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

class P {
  static void Main() {
    string interop = @"C:\Program Files (x86)\Steam\steamapps\common\Shadows of Doubt\BepInEx\interop";
    string core = @"C:\Program Files (x86)\Steam\steamapps\common\Shadows of Doubt\BepInEx\core";
    var paths = new List<string>();
    paths.AddRange(Directory.GetFiles(interop, "*.dll"));
    paths.AddRange(Directory.GetFiles(core, "*.dll"));
    // add runtime dir for System.Private.CoreLib etc
    var rt = Path.GetDirectoryName(typeof(object).Assembly.Location);
    paths.AddRange(Directory.GetFiles(rt, "*.dll"));
    var uniq = paths.GroupBy(x=>Path.GetFileName(x).ToLower()).Select(g=>g.First()).ToArray();
    var res = new System.Reflection.PathAssemblyResolver(uniq);
    var mlc = new MetadataLoadContext(res);
    var phys = mlc.LoadFromAssemblyPath(Path.Combine(interop,"UnityEngine.PhysicsModule.dll"));
    var coreAsm = mlc.LoadFromAssemblyPath(Path.Combine(interop,"UnityEngine.CoreModule.dll"));
    foreach (var tn in new[]{"UnityEngine.Physics","UnityEngine.RaycastHit","UnityEngine.QueryTriggerInteraction"}) {
      var t = phys.GetType(tn);
      if (t==null){ Console.WriteLine("MISSING "+tn); continue; }
      Console.WriteLine("==== "+t.FullName+" (base="+ (t.BaseType!=null?t.BaseType.FullName:"?") +") isValueType="+t.IsValueType);
      foreach (var m in t.GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly).OrderBy(x=>x.Name)) {
        if (m.Name.Contains("Raycast") || m.Name.Contains("Linecast") || m.Name=="get_collider" || m.Name=="get_point" || m.Name=="get_distance" || m.Name=="get_normal" || m.Name=="get_transform") {
          var ps = string.Join(", ", m.GetParameters().Select(p=> (p.IsOut?"out ":(p.ParameterType.IsByRef?"ref ":"")) + p.ParameterType.Name + " " + p.Name + (p.HasDefaultValue?" = "+(p.RawDefaultValue??"null"):"")));
          Console.WriteLine("  "+m.ReturnType.Name+" "+m.Name+"("+ps+")");
        }
      }
    }
  }
}
