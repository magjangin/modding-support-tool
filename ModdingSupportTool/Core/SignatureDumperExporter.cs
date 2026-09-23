using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ModdingSupportTool.Core;

public static class SignatureDumperExporter
{
    public static async Task ExportToDirectoryAsync(string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        string csprojContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net472;net8.0</TargetFrameworks>
    <RootNamespace>SignatureDumper</RootNamespace>
    <AssemblyName>SignatureDumper</AssemblyName>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <Compile Remove=""**\*.cs"" />
    <Compile Include=""Program.cs"" />
    <Compile Include=""SignatureDumper.cs"" />
  </ItemGroup>
</Project>
";

        string programContent = @"using System;
using System.Collections.Generic;
using System.IO;

namespace SignatureDumper
{
    internal class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == ""--help"" || args[0] == ""-h""))
            {
                PrintUsage();
                return 0;
            }

            string repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "".."", "".."", "".."", ""..""));

            string gameAssembliesDir = args.Length > 0
                ? args[0]
                : ResolveDefaultAssembliesDirectory();

            string outputDir = args.Length > 1
                ? args[1]
                : Path.Combine(repoRoot, ""Decompiled"");

            string[] targets = args.Length > 2
                ? args[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                : new[] { ""Assembly-CSharp.dll"", ""Assembly-CSharp-firstpass.dll"" };

            if (!Directory.Exists(gameAssembliesDir))
            {
                Console.Error.WriteLine($""[오류] Il2Cpp 시그니처 어셈블리 폴더를 찾을 수 없습니다: {gameAssembliesDir}"");
                return 1;
            }

            Directory.CreateDirectory(outputDir);

            Console.WriteLine($""입력 폴더 : {gameAssembliesDir}"");
            Console.WriteLine($""출력 폴더 : {outputDir}"");
            Console.WriteLine();

            foreach (string targetName in targets)
            {
                string dllPath = Path.Combine(gameAssembliesDir, targetName.Trim());
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($""[건너뜀] 파일 없음: {dllPath}"");
                    continue;
                }

                string moduleOutputDir = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(targetName.Trim()));
                if (Directory.Exists(moduleOutputDir))
                {
                    Directory.Delete(moduleOutputDir, recursive: true);
                }

                Console.WriteLine($""시그니처 덤프 중: {targetName} -> {moduleOutputDir}"");

                try
                {
                    var options = new SignatureDumperOptions
                    {
                        TargetAssemblyPath = dllPath,
                        SearchDirectory = gameAssembliesDir,
                        OutputDirectory = moduleOutputDir
                    };

                    var dumper = new AssemblySignatureDumper(options);
                    dumper.Dump();

                    Console.WriteLine($""  완료: {targetName}"");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($""  [실패] {targetName}: {ex.Message}"");
                }
            }

            Console.WriteLine();
            Console.WriteLine(""모든 작업이 끝났습니다."");
            return 0;
        }

        static string ResolveDefaultAssembliesDirectory()
        {
            var candidates = new List<string>
            {
                Path.Combine(""H:\\muse dash hwa"", ""MelonLoader"", ""Il2CppAssemblies""),
                Path.Combine(""H:\\steam"", ""steamapps"", ""common"", ""MiSide"", ""MelonLoader"", ""Il2CppAssemblies""),
                Path.Combine(""H:\\steam"", ""steamapps"", ""common"", ""Muse Dash"", ""MelonLoader"", ""Il2CppAssemblies"")
            };

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(""H:\\muse dash hwa"", ""MelonLoader"", ""Il2CppAssemblies"");
        }

        static void PrintUsage()
        {
            Console.WriteLine(""Usage:"");
            Console.WriteLine(""  SignatureDumper [inputDir] [outputDir] [target1,target2,...]"");
            Console.WriteLine();
            Console.WriteLine(""Examples:"");
            Console.WriteLine(""  SignatureDumper"");
            Console.WriteLine(""  SignatureDumper H:\\muse dash hwa\\MelonLoader\\Il2CppAssemblies"");
            Console.WriteLine(""  SignatureDumper H:\\muse dash hwa\\MelonLoader\\Il2CppAssemblies H:\\out\\decompiled Assembly-CSharp.dll,Assembly-CSharp-firstpass.dll"");
        }
    }
}
";

        string signatureDumperContent = @"using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SignatureDumper
{
    public class TypeSignatureInfo
    {
        public string Namespace { get; set; }
        public string Name { get; set; }
        public string RawTypeName { get; set; }
        public string FullName { get; set; }
        public string Kind { get; set; } // class, struct, interface, enum, delegate
        public string AccessModifiers { get; set; }
        public string BaseType { get; set; }
        public List<string> Interfaces { get; set; } = new List<string>();
        public List<string> EnumMembers { get; set; } = new List<string>();
        public List<string> Fields { get; set; } = new List<string>();
        public List<string> Properties { get; set; } = new List<string>();
        public List<string> Events { get; set; } = new List<string>();
        public List<string> Constructors { get; set; } = new List<string>();
        public List<string> Methods { get; set; } = new List<string>();
    }

    public class SignatureDumperOptions
    {
        public string TargetAssemblyPath { get; set; }
        public string SearchDirectory { get; set; }
        public string OutputDirectory { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
    }

    public class AssemblySignatureDumper
    {
        private readonly SignatureDumperOptions _options;

        public AssemblySignatureDumper(SignatureDumperOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void Dump()
        {
            string assemblyPath = _options.TargetAssemblyPath;
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($""Assembly not found: {assemblyPath}"");
            }

            Console.WriteLine($""[SignatureDumper] Target Assembly: {assemblyPath}"");
            string searchDir = _options.SearchDirectory ?? Path.GetDirectoryName(assemblyPath);
            string melonLoaderRoot = Directory.GetParent(searchDir)?.FullName;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var asmName = new AssemblyName(args.Name).Name + "".dll"";
                    var candidatePaths = new List<string> { Path.Combine(searchDir, asmName) };

                    if (melonLoaderRoot != null)
                    {
                        candidatePaths.Add(Path.Combine(melonLoaderRoot, ""net6"", asmName));
                        candidatePaths.Add(Path.Combine(melonLoaderRoot, ""net472"", asmName));
                        candidatePaths.Add(Path.Combine(melonLoaderRoot, ""net35"", asmName));
                        candidatePaths.Add(Path.Combine(melonLoaderRoot, ""Dependencies"", ""SupportModules"", asmName));
                        candidatePaths.Add(Path.Combine(melonLoaderRoot, asmName));
                    }

                    foreach (var path in candidatePaths)
                    {
                        if (File.Exists(path))
                        {
                            return Assembly.LoadFrom(path);
                        }
                    }
                }
                catch { }
                return null;
            };

            Assembly asm = Assembly.LoadFrom(assemblyPath);
            Type[] types;

            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
                Console.WriteLine($""[SignatureDumper] Warning: Loaded {types.Length} types with some unresolved dependencies."");
                foreach (var loaderEx in ex.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct().Take(5))
                {
                    Console.WriteLine($""[SignatureDumper]   LoaderException: {loaderEx}"");
                }
            }

            Console.WriteLine($""[SignatureDumper] Analyzing {types.Length} types..."");

            List<TypeSignatureInfo> infos = new List<TypeSignatureInfo>();

            foreach (var type in types.OrderBy(t => t.Namespace).ThenBy(t => t.Name))
            {
                try
                {
                    infos.Add(AnalyzeType(type));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($""[SignatureDumper] Error analyzing type '{type.FullName}': {ex.Message}"");
                }
            }

            Directory.CreateDirectory(_options.OutputDirectory);
            WriteIndividualFiles(_options.OutputDirectory, infos);
            Console.WriteLine($""[SignatureDumper] Generated {infos.Count} individual C# files in folder: {_options.OutputDirectory}"");
        }

        private TypeSignatureInfo AnalyzeType(Type type)
        {
            var info = new TypeSignatureInfo
            {
                Namespace = type.Namespace ?? ""<global>"",
                Name = GetTypeName(type),
                RawTypeName = type.Name,
                FullName = type.FullName ?? type.Name
            };

            if (type.IsPublic || type.IsNestedPublic) info.AccessModifiers = ""public"";
            else if (type.IsNestedPrivate) info.AccessModifiers = ""private"";
            else if (type.IsNestedFamily) info.AccessModifiers = ""protected"";
            else if (type.IsNestedAssembly || type.IsNotPublic) info.AccessModifiers = ""internal"";
            else if (type.IsNestedFamORAssem) info.AccessModifiers = ""protected internal"";
            else if (type.IsNestedFamANDAssem) info.AccessModifiers = ""private protected"";
            else info.AccessModifiers = ""internal"";

            if (type.IsEnum)
            {
                info.Kind = ""enum"";
                foreach (var name in Enum.GetNames(type))
                {
                    try
                    {
                        var val = Convert.ChangeType(Enum.Parse(type, name), Enum.GetUnderlyingType(type));
                        info.EnumMembers.Add($""{name} = {val}"");
                    }
                    catch
                    {
                        info.EnumMembers.Add(name);
                    }
                }
                return info;
            }

            if (type.IsInterface) info.Kind = ""interface"";
            else if (typeof(Delegate).IsAssignableFrom(type)) info.Kind = ""delegate"";
            else if (type.IsValueType) info.Kind = ""struct"";
            else info.Kind = ""class"";

            if (type.IsAbstract && type.IsSealed) info.AccessModifiers += "" static"";
            else if (type.IsAbstract && !type.IsInterface) info.AccessModifiers += "" abstract"";
            else if (type.IsSealed && !type.IsValueType) info.AccessModifiers += "" sealed"";

            if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
            {
                info.BaseType = GetTypeName(type.BaseType);
            }

            foreach (var iface in type.GetInterfaces())
            {
                info.Interfaces.Add(GetTypeName(iface));
            }

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var field in type.GetFields(flags))
            {
                if (field.IsSpecialName) continue;
                string mods = GetFieldModifiers(field);
                string fType = GetTypeName(field.FieldType);
                string val = """";
                if (field.IsLiteral && !field.IsInitOnly)
                {
                    try { val = $"" = {field.GetRawConstantValue()}""; } catch { }
                }
                info.Fields.Add($""{mods} {fType} {field.Name}{val};"");
            }

            foreach (var prop in type.GetProperties(flags))
            {
                string pType = GetTypeName(prop.PropertyType);
                var getMethod = prop.GetGetMethod(true);
                var setMethod = prop.GetSetMethod(true);

                string getStr = getMethod != null ? (getMethod.IsPublic ? ""get; "" : $""{GetMethodAccess(getMethod)} get; "") : """";
                string setStr = setMethod != null ? (setMethod.IsPublic ? ""set; "" : $""{GetMethodAccess(setMethod)} set; "") : """";
                string access = getMethod != null ? GetMethodAccess(getMethod) : (setMethod != null ? GetMethodAccess(setMethod) : ""public"");

                info.Properties.Add($""{access} {pType} {prop.Name} {{ {getStr}{setStr}}}"");
            }

            foreach (var ev in type.GetEvents(flags))
            {
                string eType = GetTypeName(ev.EventHandlerType);
                info.Events.Add($""public event {eType} {ev.Name};"");
            }

            foreach (var ctor in type.GetConstructors(flags))
            {
                string access = GetMethodAccess(ctor);
                string paramsStr = string.Join("", "", ctor.GetParameters().Select(FormatParameter));
                info.Constructors.Add($""{access} {info.Name}({paramsStr});"");
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.IsSpecialName) continue;
                string access = GetMethodAccess(method);
                string mods = GetMethodModifiers(method);
                string retType = GetTypeName(method.ReturnType);
                string paramsStr = string.Join("", "", method.GetParameters().Select(FormatParameter));
                string genArgs = method.IsGenericMethod ? $""<{string.Join("", "", method.GetGenericArguments().Select(t => t.Name))}>"" : """";

                info.Methods.Add($""{access}{mods} {retType} {method.Name}{genArgs}({paramsStr});"");
            }

            return info;
        }

        private string FormatParameter(ParameterInfo p)
        {
            string prefix = """";
            if (p.IsOut) prefix = ""out "";
            else if (p.ParameterType.IsByRef) prefix = ""ref "";
            else if (p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Any()) prefix = ""params "";

            string typeName = GetTypeName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType);
            string def = """";

            if (p.HasDefaultValue)
            {
                if (p.DefaultValue == null) def = "" = null"";
                else if (p.DefaultValue is string s) def = $"" = \""{s}\"""";
                else if (p.DefaultValue is bool b) def = $"" = {(b ? ""true"" : ""false"")}"";
                else def = $"" = {p.DefaultValue}"";
            }

            return $""{prefix}{typeName} {p.Name}{def}"";
        }

        private string GetFieldModifiers(FieldInfo f)
        {
            string access = f.IsPublic ? ""public"" : (f.IsPrivate ? ""private"" : (f.IsFamily ? ""protected"" : ""internal""));
            if (f.IsLiteral) return $""public const"";
            if (f.IsStatic && f.IsInitOnly) return $""{access} static readonly"";
            if (f.IsStatic) return $""{access} static"";
            if (f.IsInitOnly) return $""{access} readonly"";
            return access;
        }

        private string GetMethodAccess(MethodBase m)
        {
            if (m.IsPublic) return ""public"";
            if (m.IsPrivate) return ""private"";
            if (m.IsFamily) return ""protected"";
            if (m.IsAssembly) return ""internal"";
            if (m.IsFamilyOrAssembly) return ""protected internal"";
            return ""internal"";
        }

        private string GetMethodModifiers(MethodBase m)
        {
            var parts = new List<string>();
            if (m.IsStatic) parts.Add(""static"");
            if (m.IsAbstract) parts.Add(""abstract"");
            else if (m.IsVirtual && !m.IsFinal) parts.Add(""virtual"");
            return parts.Count > 0 ? "" "" + string.Join("" "", parts) : """";
        }

        private string GetTypeName(Type t)
        {
            if (t == null) return ""void"";
            if (t == typeof(void)) return ""void"";
            if (t == typeof(int)) return ""int"";
            if (t == typeof(long)) return ""long"";
            if (t == typeof(short)) return ""short"";
            if (t == typeof(byte)) return ""byte"";
            if (t == typeof(bool)) return ""bool"";
            if (t == typeof(float)) return ""float"";
            if (t == typeof(double)) return ""double"";
            if (t == typeof(string)) return ""string"";
            if (t == typeof(object)) return ""object"";

            if (t.IsGenericType)
            {
                string genericName = t.Name.Split('`')[0];
                var typeArgs = string.Join("", "", t.GetGenericArguments().Select(GetTypeName));
                return $""{genericName}<{typeArgs}>"";
            }

            return t.Name;
        }

        private void WriteIndividualFiles(string decompileDir, List<TypeSignatureInfo> infos)
        {
            foreach (var info in infos)
            {
                string nsPath = info.Namespace == ""<global>"" || string.IsNullOrEmpty(info.Namespace) ? ""Global"" : info.Namespace.Replace('.', Path.DirectorySeparatorChar);
                string targetDir = Path.Combine(decompileDir, nsPath);
                Directory.CreateDirectory(targetDir);

                string safeFileName = SanitizeFileName(info.RawTypeName ?? info.Name) + "".cs"";
                string filePath = Path.Combine(targetDir, safeFileName);

                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    writer.WriteLine(""using System;"");
                    writer.WriteLine(""using System.Collections.Generic;"");
                    writer.WriteLine(""using UnityEngine;"");
                    writer.WriteLine();

                    string nsHeader = info.Namespace == ""<global>"" || string.IsNullOrEmpty(info.Namespace) ? ""Global"" : info.Namespace;
                    writer.WriteLine($""namespace {nsHeader}"");
                    writer.WriteLine(""{"");

                    string baseClause = """";
                    var bases = new List<string>();
                    if (!string.IsNullOrEmpty(info.BaseType)) bases.Add(info.BaseType);
                    if (info.Interfaces != null && info.Interfaces.Count > 0) bases.AddRange(info.Interfaces);
                    if (bases.Count > 0) baseClause = $"" : {string.Join("", "", bases)}"";

                    writer.WriteLine($""    {info.AccessModifiers} {info.Kind} {info.Name}{baseClause}"");
                    writer.WriteLine(""    {"");

                    if (info.Kind == ""enum"")
                    {
                        foreach (var member in info.EnumMembers)
                        {
                            writer.WriteLine($""        {member},"");
                        }
                    }
                    else
                    {
                        if (info.Fields.Count > 0)
                        {
                            writer.WriteLine(""        // Fields"");
                            foreach (var field in info.Fields) writer.WriteLine($""        {field}"");
                            writer.WriteLine();
                        }

                        if (info.Properties.Count > 0)
                        {
                            writer.WriteLine(""        // Properties"");
                            foreach (var prop in info.Properties) writer.WriteLine($""        {prop}"");
                            writer.WriteLine();
                        }

                        if (info.Events.Count > 0)
                        {
                            writer.WriteLine(""        // Events"");
                            foreach (var ev in info.Events) writer.WriteLine($""        {ev}"");
                            writer.WriteLine();
                        }

                        if (info.Constructors.Count > 0)
                        {
                            writer.WriteLine(""        // Constructors"");
                            foreach (var ctor in info.Constructors) writer.WriteLine($""        {ctor}"");
                            writer.WriteLine();
                        }

                        if (info.Methods.Count > 0)
                        {
                            writer.WriteLine(""        // Methods"");
                            foreach (var method in info.Methods) writer.WriteLine($""        {method}"");
                        }
                    }

                    writer.WriteLine(""    }"");
                    writer.WriteLine(""}"");
                }
            }
        }

        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in fileName)
            {
                if (invalidChars.Contains(c) || c == '<' || c == '>' || c == ':' || c == '*' || c == '?' || c == '""' || c == '|' || c == '/')
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
";

        string readmeContent = @"# SignatureDumper

독립 실행형 Il2Cpp 어셈블리 시그니처 덤프 도구 (Console CLI)

## 빌드 및 실행

```bash
# 빌드
dotnet build

# 실행 (기본 경로)
dotnet run

# 실행 인자 전달: [inputDir] [outputDir] [target1,target2,...]
dotnet run -- ""H:\muse dash hwa\MelonLoader\Il2CppAssemblies"" ""./Decompiled"" ""Assembly-CSharp.dll,Assembly-CSharp-firstpass.dll""
```
";

        await File.WriteAllTextAsync(Path.Combine(destinationDir, "SignatureDumper.csproj"), csprojContent, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, "Program.cs"), programContent, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, "SignatureDumper.cs"), signatureDumperContent, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, "README.md"), readmeContent, Encoding.UTF8);
    }
}
