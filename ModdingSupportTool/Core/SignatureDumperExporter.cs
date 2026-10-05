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

            int succeeded = 0;
            int failed = 0;

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
                    try
                    {
                        Directory.Delete(moduleOutputDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($""[경고] 이전 출력 폴더를 지우지 못해 예전 파일이 남을 수 있습니다: {moduleOutputDir} ({ex.Message})"");
                    }
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
                    succeeded++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($""  [실패] {targetName}: {ex.Message}"");
                    failed++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($""결과: 성공 {succeeded}개, 실패 {failed}개"");

            // 하나도 덤프하지 못했거나 실패가 있으면 종료 코드 3 (앱 CLI와 같은 규칙)
            if (succeeded == 0 || failed > 0)
            {
                Console.Error.WriteLine(""[실패] 덤프된 어셈블리가 없거나 일부가 실패했습니다."");
                return 3;
            }

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

        string signatureDumperContent = """
using System;
using System.Collections.Generic;
using System.Globalization;
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
        public string FileStem { get; set; } // 중첩 타입은 Outer.Inner 형태
        public string DeclaringTypeName { get; set; }
        public string Kind { get; set; } // class, struct, interface, enum, delegate
        public string AccessModifiers { get; set; }
        public string BaseType { get; set; }
        public List<string> Usings { get; set; } = new List<string>();
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
        private static readonly Dictionary<Type, string> TypeKeywords = new Dictionary<Type, string>
        {
            [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte",
            [typeof(char)] = "char", [typeof(decimal)] = "decimal", [typeof(double)] = "double", [typeof(float)] = "float",
            [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long", [typeof(ulong)] = "ulong",
            [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(string)] = "string", [typeof(object)] = "object"
        };

        private readonly SignatureDumperOptions _options;
        private readonly Dictionary<Assembly, Dictionary<string, HashSet<string>>> _namespaceIndex = new Dictionary<Assembly, Dictionary<string, HashSet<string>>>();
        private Assembly _targetAssembly;

        public AssemblySignatureDumper(SignatureDumperOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void Dump()
        {
            string assemblyPath = _options.TargetAssemblyPath;
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Assembly not found: {assemblyPath}");
            }

            Console.WriteLine($"[SignatureDumper] Target Assembly: {assemblyPath}");
            string searchDir = _options.SearchDirectory ?? Path.GetDirectoryName(assemblyPath);
            string melonLoaderRoot = Directory.GetParent(searchDir)?.FullName;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var asmName = new AssemblyName(args.Name).Name + ".dll";
                    var candidatePaths = new List<string> { Path.Combine(searchDir, asmName) };

                    if (melonLoaderRoot != null)
                    {
                        candidatePaths.Add(Path.Combine(melonLoaderRoot, "net6", asmName));
                        candidatePaths.Add(Path.Combine(melonLoaderRoot, "net472", asmName));
                        candidatePaths.Add(Path.Combine(melonLoaderRoot, "net35", asmName));
                        candidatePaths.Add(Path.Combine(melonLoaderRoot, "Dependencies", "SupportModules", asmName));
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
            _targetAssembly = asm;
            Type[] types;

            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
                Console.WriteLine($"[SignatureDumper] Warning: Loaded {types.Length} types with some unresolved dependencies.");
                foreach (var loaderEx in ex.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct().Take(5))
                {
                    Console.WriteLine($"[SignatureDumper]   LoaderException: {loaderEx}");
                }
            }

            Console.WriteLine($"[SignatureDumper] Analyzing {types.Length} types...");

            List<TypeSignatureInfo> infos = new List<TypeSignatureInfo>();

            foreach (var type in types.OrderBy(t => t.Namespace).ThenBy(t => t.Name).ThenBy(t => t.FullName, StringComparer.Ordinal))
            {
                try
                {
                    infos.Add(AnalyzeType(type));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SignatureDumper] Error analyzing type '{type.FullName}': {ex.Message}");
                }
            }

            Directory.CreateDirectory(_options.OutputDirectory);
            WriteIndividualFiles(_options.OutputDirectory, infos);
            Console.WriteLine($"[SignatureDumper] Generated {infos.Count} individual C# files in folder: {_options.OutputDirectory}");
        }

        private TypeSignatureInfo AnalyzeType(Type type)
        {
            string ns = GetNamespace(type);
            var info = new TypeSignatureInfo
            {
                Namespace = ns.Length > 0 ? ns : "<global>",
                Name = GetDeclarationName(type),
                RawTypeName = type.Name,
                FullName = type.FullName ?? type.Name,
                FileStem = GetFileStem(type),
                DeclaringTypeName = type.DeclaringType != null ? GetNestedName(type.DeclaringType) : null
            };

            if (type.IsPublic || type.IsNestedPublic) info.AccessModifiers = "public";
            else if (type.IsNestedPrivate) info.AccessModifiers = "private";
            else if (type.IsNestedFamily) info.AccessModifiers = "protected";
            else if (type.IsNestedAssembly || type.IsNotPublic) info.AccessModifiers = "internal";
            else if (type.IsNestedFamORAssem) info.AccessModifiers = "protected internal";
            else if (type.IsNestedFamANDAssem) info.AccessModifiers = "private protected";
            else info.AccessModifiers = "internal";

            if (type.IsEnum)
            {
                info.Kind = "enum";
                foreach (var name in Enum.GetNames(type))
                {
                    try
                    {
                        var val = Convert.ChangeType(Enum.Parse(type, name), Enum.GetUnderlyingType(type));
                        info.EnumMembers.Add($"{name} = {val}");
                    }
                    catch
                    {
                        info.EnumMembers.Add(name);
                    }
                }
                return info;
            }

            if (type.IsInterface) info.Kind = "interface";
            else if (typeof(Delegate).IsAssignableFrom(type)) info.Kind = "delegate";
            else if (type.IsValueType) info.Kind = "struct";
            else info.Kind = "class";

            if (type.IsAbstract && type.IsSealed) info.AccessModifiers += " static";
            else if (type.IsAbstract && !type.IsInterface) info.AccessModifiers += " abstract";
            else if (type.IsSealed && !type.IsValueType) info.AccessModifiers += " sealed";

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            Type baseType = type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType) ? type.BaseType : null;
            Type[] interfaces = type.GetInterfaces();
            FieldInfo[] fields = type.GetFields(flags).Where(f => !f.IsSpecialName).ToArray();
            PropertyInfo[] properties = type.GetProperties(flags);
            EventInfo[] events = type.GetEvents(flags);
            ConstructorInfo[] constructors = type.GetConstructors(flags);
            MethodInfo[] methods = type.GetMethods(flags).Where(m => !m.IsSpecialName).ToArray();

            // 파일에 실제로 등장하는 타입만으로 using을 만들고, 이름이 겹치는 타입은 네임스페이스를 붙인다.
            // (예: Il2CppSystem.Collections.Generic.List 를 System List 로 오인하지 않도록)
            var referenced = new List<Type> { baseType };
            referenced.AddRange(interfaces);
            referenced.AddRange(fields.Select(f => f.FieldType));
            referenced.AddRange(properties.Select(p => p.PropertyType));
            referenced.AddRange(events.Select(e => e.EventHandlerType));
            referenced.AddRange(constructors.SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));
            referenced.AddRange(methods.Select(m => m.ReturnType));
            referenced.AddRange(methods.SelectMany(m => m.GetParameters()).Select(p => p.ParameterType));

            var names = new TypeNameFormatter(ns, referenced, GetNamespaceIndex);
            info.Usings = names.Usings;

            if (baseType != null)
            {
                info.BaseType = names.Format(baseType);
            }

            foreach (var iface in interfaces)
            {
                info.Interfaces.Add(names.Format(iface));
            }

            foreach (var field in fields)
            {
                string mods = GetFieldModifiers(field);
                string fType = names.Format(field.FieldType);
                string val = "";
                if (field.IsLiteral && !field.IsInitOnly)
                {
                    try { val = $" = {FormatLiteral(field.GetRawConstantValue())}"; } catch { }
                }
                info.Fields.Add($"{mods} {fType} {field.Name}{val};");
            }

            foreach (var prop in properties)
            {
                string pType = names.Format(prop.PropertyType);
                var getMethod = prop.GetGetMethod(true);
                var setMethod = prop.GetSetMethod(true);

                string getStr = getMethod != null ? (getMethod.IsPublic ? "get; " : $"{GetMethodAccess(getMethod)} get; ") : "";
                string setStr = setMethod != null ? (setMethod.IsPublic ? "set; " : $"{GetMethodAccess(setMethod)} set; ") : "";
                string access = getMethod != null ? GetMethodAccess(getMethod) : (setMethod != null ? GetMethodAccess(setMethod) : "public");

                info.Properties.Add($"{access} {pType} {prop.Name} {{ {getStr}{setStr}}}");
            }

            foreach (var ev in events)
            {
                string eType = names.Format(ev.EventHandlerType);
                info.Events.Add($"public event {eType} {ev.Name};");
            }

            string ctorName = StripArity(type.Name);
            foreach (var ctor in constructors)
            {
                string access = ctor.IsStatic ? "static" : GetMethodAccess(ctor);
                string paramsStr = string.Join(", ", ctor.GetParameters().Select(p => FormatParameter(p, names)));
                info.Constructors.Add($"{access} {ctorName}({paramsStr});");
            }

            foreach (var method in methods)
            {
                string access = GetMethodAccess(method);
                string mods = GetMethodModifiers(method);
                string retType = (method.ReturnType.IsByRef ? "ref " : "") + names.Format(method.ReturnType);
                string paramsStr = string.Join(", ", method.GetParameters().Select(p => FormatParameter(p, names)));
                string genArgs = method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(t => t.Name))}>" : "";

                info.Methods.Add($"{access}{mods} {retType} {method.Name}{genArgs}({paramsStr});");
            }

            return info;
        }

        private string FormatParameter(ParameterInfo p, TypeNameFormatter names)
        {
            string prefix = "";
            if (p.IsOut) prefix = "out ";
            else if (p.ParameterType.IsByRef) prefix = "ref ";
            else if (p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Any()) prefix = "params ";

            string typeName = names.Format(p.ParameterType);
            string def = "";

            if (p.HasDefaultValue)
            {
                def = $" = {FormatLiteral(p.DefaultValue)}";
            }

            return $"{prefix}{typeName} {p.Name}{def}";
        }

        // 상수/기본값을 C# 리터럴 형태로 표기한다. (문자열 따옴표·이스케이프, true/false, 문화권과 무관한 숫자)
        private static string FormatLiteral(object value)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return "\"" + EscapeLiteral(s, '"') + "\"";
                case char c: return "'" + EscapeLiteral(c.ToString(), '\'') + "'";
                case bool b: return b ? "true" : "false";
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString() ?? string.Empty;
            }
        }

        private static string EscapeLiteral(string text, char quote)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '\0': sb.Append(@"\0"); break;
                    case '\n': sb.Append(@"\n"); break;
                    case '\r': sb.Append(@"\r"); break;
                    case '\t': sb.Append(@"\t"); break;
                    default:
                        if (c == quote) sb.Append('\\').Append(c);
                        else if (char.IsControl(c)) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private string GetFieldModifiers(FieldInfo f)
        {
            string access = f.IsPublic ? "public"
                : f.IsPrivate ? "private"
                : f.IsFamily ? "protected"
                : f.IsFamilyOrAssembly ? "protected internal"
                : f.IsFamilyAndAssembly ? "private protected"
                : "internal";
            if (f.IsLiteral) return $"{access} const";
            if (f.IsStatic && f.IsInitOnly) return $"{access} static readonly";
            if (f.IsStatic) return $"{access} static";
            if (f.IsInitOnly) return $"{access} readonly";
            return access;
        }

        private string GetMethodAccess(MethodBase m)
        {
            if (m.IsPublic) return "public";
            if (m.IsPrivate) return "private";
            if (m.IsFamily) return "protected";
            if (m.IsAssembly) return "internal";
            if (m.IsFamilyOrAssembly) return "protected internal";
            return "internal";
        }

        private string GetMethodModifiers(MethodBase m)
        {
            var parts = new List<string>();
            if (m.IsStatic) parts.Add("static");

            // C# 컴파일러는 virtual 선언에는 NewSlot을 붙이고 override에는 붙이지 않는다.
            bool isOverride = m.IsVirtual && (m.Attributes & MethodAttributes.NewSlot) == 0 && OverridesBaseMethod(m);
            if (m.IsAbstract) parts.Add(isOverride ? "abstract override" : "abstract");
            else if (isOverride) parts.Add(m.IsFinal ? "sealed override" : "override");
            else if (m.IsVirtual && !m.IsFinal) parts.Add("virtual");
            return parts.Count > 0 ? " " + string.Join(" ", parts) : "";
        }

        private static bool OverridesBaseMethod(MethodBase m)
        {
            if (!(m is MethodInfo mi)) return false;
            try
            {
                return mi.GetBaseDefinition().DeclaringType != mi.DeclaringType;
            }
            catch
            {
                // 베이스 타입을 읽지 못하면 NewSlot 플래그만으로 판단한다.
                return true;
            }
        }

        private Dictionary<string, HashSet<string>> GetNamespaceIndex(Assembly asm)
        {
            if (_namespaceIndex.TryGetValue(asm, out var index)) return index;

            index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { types = Array.Empty<Type>(); }

            foreach (var t in types)
            {
                // 다른 어셈블리의 non-public 타입은 이름 충돌을 일으키지 않는다.
                if (t == null || t.IsNested || (!t.IsPublic && asm != _targetAssembly)) continue;
                string ns = t.Namespace ?? string.Empty;
                if (!index.TryGetValue(ns, out var typeNames))
                {
                    typeNames = new HashSet<string>(StringComparer.Ordinal);
                    index[ns] = typeNames;
                }
                typeNames.Add(t.Name);
            }

            _namespaceIndex[asm] = index;
            return index;
        }

        private static Type GetOutermostType(Type t)
        {
            while (t.DeclaringType != null) t = t.DeclaringType;
            return t;
        }

        private static string GetNamespace(Type t) => GetOutermostType(t).Namespace ?? string.Empty;

        private static string StripArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick >= 0 ? name.Substring(0, tick) : name;
        }

        // 다른 타입에서 참조할 때 쓰는 이름 (예: ScreenEffectCollection.FlashType)
        private static string GetNestedName(Type t) =>
            t.DeclaringType == null ? StripArity(t.Name) : GetNestedName(t.DeclaringType) + "." + StripArity(t.Name);

        // 파일 이름: 중첩 타입은 바깥 타입 이름을 앞에 붙여 같은 이름끼리 덮어쓰지 않게 한다.
        private static string GetFileStem(Type t) =>
            t.DeclaringType == null ? t.Name : GetFileStem(t.DeclaringType) + "." + t.Name;

        // 선언부 이름: 자기 자신이 선언한 제네릭 파라미터만 붙인다.
        private static string GetDeclarationName(Type t)
        {
            string name = StripArity(t.Name);
            int inherited = t.DeclaringType?.GetGenericArguments().Length ?? 0;
            var own = t.GetGenericArguments().Skip(inherited).Select(a => a.Name).ToArray();
            return own.Length > 0 ? $"{name}<{string.Join(", ", own)}>" : name;
        }

        private sealed class TypeNameFormatter
        {
            private readonly HashSet<string> _ambiguousNames = new HashSet<string>(StringComparer.Ordinal);

            public List<string> Usings { get; }

            public TypeNameFormatter(string currentNamespace, IEnumerable<Type> referencedTypes, Func<Assembly, Dictionary<string, HashSet<string>>> getNamespaceIndex)
            {
                var namedTypes = new List<Type>();
                foreach (var t in referencedTypes) CollectNamedTypes(t, namedTypes);

                var usings = new HashSet<string>(StringComparer.Ordinal);
                var assemblies = new HashSet<Assembly>();
                foreach (var t in namedTypes)
                {
                    string ns = GetNamespace(t);
                    if (ns.Length > 0 && ns != currentNamespace) usings.Add(ns);
                    assemblies.Add(t.Assembly);
                }
                Usings = usings.OrderBy(n => n, StringComparer.Ordinal).ToList();

                // 현재 네임스페이스·using 네임스페이스·전역 중 두 곳 이상에 같은 이름의 타입이 있으면 모호하다.
                var visible = new HashSet<string>(usings, StringComparer.Ordinal) { currentNamespace, string.Empty };
                var indexes = assemblies.Select(getNamespaceIndex).ToList();
                foreach (var t in namedTypes)
                {
                    string name = GetOutermostType(t).Name;
                    if (_ambiguousNames.Contains(name)) continue;
                    int owners = visible.Count(ns => indexes.Any(index => index.TryGetValue(ns, out var typeNames) && typeNames.Contains(name)));
                    if (owners > 1) _ambiguousNames.Add(name);
                }
            }

            public string Format(Type t)
            {
                if (t == null) return "void";
                if (t.IsByRef) return Format(t.GetElementType());
                if (t.IsPointer) return Format(t.GetElementType()) + "*";
                if (t.IsArray) return Format(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
                if (t.IsGenericParameter) return t.Name;
                if (TypeKeywords.TryGetValue(t, out var keyword)) return keyword;

                string name = GetNestedName(t);
                if (_ambiguousNames.Contains(GetOutermostType(t).Name))
                {
                    string ns = GetNamespace(t);
                    name = ns.Length > 0 ? $"{ns}.{name}" : $"global::{name}";
                }

                if (t.IsGenericType)
                {
                    name += $"<{string.Join(", ", t.GetGenericArguments().Select(Format))}>";
                }

                return name;
            }

            private static void CollectNamedTypes(Type t, List<Type> output)
            {
                if (t == null || t.IsGenericParameter) return;
                if (t.HasElementType)
                {
                    CollectNamedTypes(t.GetElementType(), output);
                    return;
                }
                if (t.IsGenericType)
                {
                    foreach (var arg in t.GetGenericArguments()) CollectNamedTypes(arg, output);
                }
                if (!TypeKeywords.ContainsKey(t)) output.Add(t);
            }
        }

        private void WriteIndividualFiles(string decompileDir, List<TypeSignatureInfo> infos)
        {
            // Windows 파일 시스템은 대소문자를 구분하지 않으므로, 같은 경로가 또 나오면 번호를 붙여 덮어쓰기를 막는다.
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var info in infos)
            {
                string nsPath = info.Namespace == "<global>" || string.IsNullOrEmpty(info.Namespace) ? "Global" : info.Namespace.Replace('.', Path.DirectorySeparatorChar);
                string targetDir = Path.Combine(decompileDir, nsPath);
                Directory.CreateDirectory(targetDir);

                string safeFileName = SanitizeFileName(info.FileStem);
                string filePath = Path.Combine(targetDir, safeFileName + ".cs");
                for (int suffix = 2; !usedPaths.Add(filePath); suffix++)
                {
                    filePath = Path.Combine(targetDir, $"{safeFileName}_{suffix}.cs");
                }

                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    foreach (var ns in info.Usings) writer.WriteLine($"using {ns};");
                    if (info.Usings.Count > 0) writer.WriteLine();

                    string nsHeader = info.Namespace == "<global>" || string.IsNullOrEmpty(info.Namespace) ? "Global" : info.Namespace;
                    writer.WriteLine($"namespace {nsHeader}");
                    writer.WriteLine("{");

                    string baseClause = "";
                    var bases = new List<string>();
                    if (!string.IsNullOrEmpty(info.BaseType)) bases.Add(info.BaseType);
                    if (info.Interfaces != null && info.Interfaces.Count > 0) bases.AddRange(info.Interfaces);
                    if (bases.Count > 0) baseClause = $" : {string.Join(", ", bases)}";

                    if (info.DeclaringTypeName != null) writer.WriteLine($"    // Nested in: {info.DeclaringTypeName}");
                    writer.WriteLine($"    {info.AccessModifiers} {info.Kind} {info.Name}{baseClause}");
                    writer.WriteLine("    {");

                    if (info.Kind == "enum")
                    {
                        foreach (var member in info.EnumMembers)
                        {
                            writer.WriteLine($"        {member},");
                        }
                    }
                    else
                    {
                        if (info.Fields.Count > 0)
                        {
                            writer.WriteLine("        // Fields");
                            foreach (var field in info.Fields) writer.WriteLine($"        {field}");
                            writer.WriteLine();
                        }

                        if (info.Properties.Count > 0)
                        {
                            writer.WriteLine("        // Properties");
                            foreach (var prop in info.Properties) writer.WriteLine($"        {prop}");
                            writer.WriteLine();
                        }

                        if (info.Events.Count > 0)
                        {
                            writer.WriteLine("        // Events");
                            foreach (var ev in info.Events) writer.WriteLine($"        {ev}");
                            writer.WriteLine();
                        }

                        if (info.Constructors.Count > 0)
                        {
                            writer.WriteLine("        // Constructors");
                            foreach (var ctor in info.Constructors) writer.WriteLine($"        {ctor}");
                            writer.WriteLine();
                        }

                        if (info.Methods.Count > 0)
                        {
                            writer.WriteLine("        // Methods");
                            foreach (var method in info.Methods) writer.WriteLine($"        {method}");
                        }
                    }

                    writer.WriteLine("    }");
                    writer.WriteLine("}");
                }
            }
        }

        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in fileName)
            {
                if (invalidChars.Contains(c) || c == '<' || c == '>' || c == ':' || c == '*' || c == '?' || c == '"' || c == '|' || c == '/')
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

""";

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

## 종료 코드

- `0`: 성공 (어셈블리를 하나 이상 덤프했고 실패가 없음)
- `1`: 입력 폴더를 찾을 수 없음
- `3`: 덤프 실패 (대상 어셈블리를 하나도 덤프하지 못했거나 일부가 실패)
";

        await File.WriteAllTextAsync(Path.Combine(destinationDir, "SignatureDumper.csproj"), csprojContent, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, "Program.cs"), programContent, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, "SignatureDumper.cs"), signatureDumperContent, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, "README.md"), readmeContent, Encoding.UTF8);
    }
}
