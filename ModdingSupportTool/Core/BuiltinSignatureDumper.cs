#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// 이 파일은 앱(.NET 10)과 "SignatureDumper 소스 내보내기"로 만드는 독립 프로젝트(net472;net8.0)가 함께 쓰는 단일 소스다.
// (SignatureDumperExporter가 이 파일을 임베디드 리소스로 그대로 내보낸다.)
// 그래서 net472에서도 컴파일되도록 최신 BCL 전용 API(string.Contains(char), Path.GetRelativePath, record 등)는 쓰지 않는다.
namespace ModdingSupportTool.Core;

public sealed class SignatureDumperOptions
{
    public string TargetAssemblyPath { get; set; } = string.Empty;
    public string? SearchDirectory { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;

    // Il2CppInterop가 만든 내부 배관 멤버(NativeFieldInfoPtr_*/NativeMethodInfoPtr_* 필드, IntPtr 생성자,
    // 정적 생성자, MethodInfoStoreGeneric_* 중첩 타입)를 출력에 넣을지. 모드 작성엔 쓰이지 않고 출력의 절반가량을 차지한다.
    public bool IncludeInteropMembers { get; set; }

    // 파일을 쓸 때마다 (전체 경로, 내용)으로 호출된다. 병렬로 불리므로 받는 쪽이 스레드 안전해야 한다.
    // 덤프 직후 레퍼런스 색인을 디스크에서 다시 읽지 않고 만들 때 쓴다.
    public Action<string, string>? FileWritten { get; set; }
}

public sealed class SignatureDumpResult
{
    public int TypeCount { get; set; }
    public int FileCount { get; set; }
    public bool IsIl2CppInterop { get; set; }
    public int OmittedInteropMembers { get; set; }
    public long AnalyzeMilliseconds { get; set; }
    public long WriteMilliseconds { get; set; }
}

internal sealed class TypeSignatureInfo
{
    public string Namespace = string.Empty; // 전역 타입은 빈 문자열
    public string Name = string.Empty;
    public string FileStem = string.Empty; // 중첩 타입은 Outer.Inner 형태
    public string? DeclaringTypeName;
    public string Kind = "class"; // class, struct, interface, enum, delegate
    public string AccessModifiers = "public";
    public string? BaseType;
    public List<string> Usings = new List<string>();
    public List<string> Interfaces = new List<string>();
    public List<string> EnumMembers = new List<string>();
    public List<string> Fields = new List<string>();
    public List<string> Properties = new List<string>();
    public List<string> Events = new List<string>();
    public List<string> Constructors = new List<string>();
    public List<string> Methods = new List<string>();
}

public sealed class BuiltinSignatureDumper
{
    private static readonly Dictionary<Type, string> TypeKeywords = new Dictionary<Type, string>
    {
        [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte",
        [typeof(char)] = "char", [typeof(decimal)] = "decimal", [typeof(double)] = "double", [typeof(float)] = "float",
        [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long", [typeof(ulong)] = "ulong",
        [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(string)] = "string", [typeof(object)] = "object"
    };

    private static readonly HashSet<char> InvalidFileNameChars = new HashSet<char>(Path.GetInvalidFileNameChars().Concat("<>:*?\"|/"));

    private static readonly string[] InteropRuntimeAssemblies = { "Il2CppInterop.Runtime", "UnhollowerBaseLib" };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly SignatureDumperOptions _options;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<Assembly, Dictionary<string, HashSet<string>>> _namespaceIndex = new ConcurrentDictionary<Assembly, Dictionary<string, HashSet<string>>>();
    private Assembly? _targetAssembly;
    private bool _stripInterop;
    private int _omittedInteropMembers;

    public BuiltinSignatureDumper(SignatureDumperOptions options, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? (_ => { });
    }

    public SignatureDumpResult Dump(CancellationToken ct = default)
    {
        string assemblyPath = _options.TargetAssemblyPath;
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"어셈블리를 찾을 수 없습니다: {assemblyPath}");
        }

        _log($"[내장 SignatureDumper] 대상 어셈블리: {assemblyPath}");
        string searchDir = _options.SearchDirectory ?? Path.GetDirectoryName(assemblyPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        string? melonLoaderRoot = Directory.GetParent(searchDir)?.FullName;
        string[] dependencyDirs = GetDependencyDirectories(searchDir, melonLoaderRoot);
        // 바이트/스트림으로 올린 어셈블리는 같은 의존성을 두 번 로드하면 타입 정체성이 갈라지므로 이름별로 한 번만 로드한다.
        var resolved = new Dictionary<string, Assembly?>(StringComparer.OrdinalIgnoreCase);

#if NETFRAMEWORK
        ResolveEventHandler handler = (_, e) => ResolveDependency(new AssemblyName(e.Name).Name, dependencyDirs, resolved, LoadAssemblyBytes);
        AppDomain.CurrentDomain.AssemblyResolve += handler;
        try
        {
            return DumpLoaded(LoadAssemblyBytes(assemblyPath), ct);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= handler;
            Reset();
        }
#else
        // 수집 가능한 AssemblyLoadContext에 스트림으로 올려서 분석 대상 DLL 파일을 잠그지 않고, 끝나면 통째로 언로드한다.
        var alc = new AssemblyLoadContext($"SignatureDumper_{Guid.NewGuid()}", isCollectible: true);
        alc.Resolving += (context, name) => ResolveDependency(name.Name, dependencyDirs, resolved, path => LoadFromStream(context, path));
        try
        {
            return DumpLoaded(LoadFromStream(alc, assemblyPath), ct);
        }
        finally
        {
            Reset();
            alc.Unload();
        }
#endif
    }

#if NETFRAMEWORK
    private static Assembly LoadAssemblyBytes(string path) => Assembly.Load(File.ReadAllBytes(path));
#else
    private static Assembly LoadFromStream(AssemblyLoadContext context, string path)
    {
        using var stream = File.OpenRead(path);
        return context.LoadFromStream(stream);
    }
#endif

    private void Reset()
    {
        _namespaceIndex.Clear();
        _targetAssembly = null;
    }

    private static string[] GetDependencyDirectories(string searchDir, string? melonLoaderRoot)
    {
        var dirs = new List<string> { searchDir };
        if (melonLoaderRoot != null)
        {
            // MelonLoader 0.6+ 폴더 구조 (Il2CppAssemblies의 부모 = MelonLoader)
            dirs.Add(Path.Combine(melonLoaderRoot, "net6"));
            dirs.Add(Path.Combine(melonLoaderRoot, "net472"));
            dirs.Add(Path.Combine(melonLoaderRoot, "net35"));
            dirs.Add(Path.Combine(melonLoaderRoot, "Dependencies", "SupportModules"));
            dirs.Add(melonLoaderRoot);
        }
        return dirs.ToArray();
    }

    private static Assembly? ResolveDependency(string? simpleName, string[] dirs, Dictionary<string, Assembly?> resolved, Func<string, Assembly> load)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;

        // 병렬 분석 중에도 해석 요청이 올 수 있다.
        lock (resolved)
        {
            if (resolved.TryGetValue(simpleName!, out var cached)) return cached;

            Assembly? result = null;
            try
            {
                foreach (string dir in dirs)
                {
                    string path = Path.Combine(dir, simpleName + ".dll");
                    if (File.Exists(path))
                    {
                        result = load(path);
                        break;
                    }
                }
            }
            catch
            {
                // 의존성을 못 읽으면 해당 타입만 빠지고(ReflectionTypeLoadException) 나머지는 계속 덤프한다.
            }

            resolved[simpleName!] = result;
            return result;
        }
    }

    private SignatureDumpResult DumpLoaded(Assembly asm, CancellationToken ct)
    {
        _targetAssembly = asm;
        bool isInterop = IsIl2CppInteropAssembly(asm);
        _stripInterop = isInterop && !_options.IncludeInteropMembers;
        _omittedInteropMembers = 0;

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
            var missing = ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct().ToList();
            _log($"[내장 SignatureDumper] 경고: 일부 참조 누락 상태에서 {types.Length}개 타입을 로드했습니다.");
            foreach (var loaderEx in missing.Take(5))
            {
                _log($"[내장 SignatureDumper]   누락 참조: {loaderEx}");
            }

            // 타입을 하나도 못 읽었으면 빈 결과를 성공으로 보고하지 않는다.
            if (types.Length == 0)
            {
                bool newerRuntime = missing.Any(m => m.IndexOf("System.Runtime", StringComparison.Ordinal) >= 0);
                throw new InvalidOperationException(newerRuntime
                    ? "이 어셈블리는 .NET 6 이상을 대상으로 합니다(Il2CppInterop). .NET Framework(net472) 빌드로는 읽을 수 없으니 net8.0 이상으로 실행하세요."
                    : "어셈블리에서 타입을 하나도 읽지 못했습니다. 의존성 DLL이 같은 폴더나 MelonLoader 폴더에 있는지 확인하세요.");
            }
        }

        if (_stripInterop)
        {
            types = types.Where(t => !IsInteropPlumbingType(t)).ToArray();
            _log("[내장 SignatureDumper] Il2CppInterop 어셈블리 감지: 내부 포인터 멤버(NativeFieldInfoPtr_ 등)를 생략합니다.");
        }

        ct.ThrowIfCancellationRequested();
        _log($"[내장 SignatureDumper] {types.Length}개 타입 정적 분석 시작...");

        var sorted = types
            .OrderBy(t => t.Namespace, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ThenBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();

        // 타입마다 독립적으로 분석할 수 있으므로 병렬로 처리하고, 결과 순서는 정렬 순서대로 유지한다.
        var watch = Stopwatch.StartNew();
        var analyzed = new TypeSignatureInfo?[sorted.Length];
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.For(0, sorted.Length, parallel, i =>
        {
            try
            {
                analyzed[i] = AnalyzeType(sorted[i]);
            }
            catch (Exception ex)
            {
                _log($"[내장 SignatureDumper] 타입 분석 경고 '{sorted[i].FullName}': {ex.Message}");
            }
        });
        var infos = analyzed.Where(i => i != null).Select(i => i!).ToList();
        long analyzeMs = watch.ElapsedMilliseconds;

        ct.ThrowIfCancellationRequested();
        watch.Restart();
        Directory.CreateDirectory(_options.OutputDirectory);
        int fileCount = WriteIndividualFiles(_options.OutputDirectory, infos, parallel, _options.FileWritten);
        long writeMs = watch.ElapsedMilliseconds;

        _log($"[내장 SignatureDumper] 완료: 총 {fileCount}개 C# 스켈레톤 소스 파일 생성 -> {_options.OutputDirectory} (분석 {analyzeMs}ms, 쓰기 {writeMs}ms)");
        if (_stripInterop)
        {
            _log($"[내장 SignatureDumper]   생략한 Il2CppInterop 내부 멤버: {_omittedInteropMembers:N0}개");
        }

        return new SignatureDumpResult
        {
            TypeCount = infos.Count,
            FileCount = fileCount,
            IsIl2CppInterop = isInterop,
            OmittedInteropMembers = _omittedInteropMembers,
            AnalyzeMilliseconds = analyzeMs,
            WriteMilliseconds = writeMs
        };
    }

    private static bool IsIl2CppInteropAssembly(Assembly asm)
    {
        try
        {
            return asm.GetReferencedAssemblies().Any(r => InteropRuntimeAssemblies.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInteropPlumbingType(Type t) =>
        t.IsNested && t.Name.StartsWith("MethodInfoStoreGeneric_", StringComparison.Ordinal);

    private static bool IsInteropPlumbingField(FieldInfo f) =>
        f.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal) ||
        f.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal);

    // Il2CppInterop 프록시의 (IntPtr pointer) 생성자와 정적 생성자는 런타임 연결용이다.
    private static bool IsInteropPlumbingConstructor(ConstructorInfo c)
    {
        if (c.IsStatic) return true;
        var parameters = c.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == typeof(IntPtr);
    }

    private TypeSignatureInfo AnalyzeType(Type type)
    {
        var info = new TypeSignatureInfo
        {
            Namespace = GetNamespace(type),
            Name = GetDeclarationName(type),
            FileStem = GetFileStem(type),
            DeclaringTypeName = type.DeclaringType != null ? GetNestedName(type.DeclaringType) : null,
            AccessModifiers = GetTypeAccess(type)
        };

        if (type.IsEnum)
        {
            info.Kind = "enum";
            foreach (var name in Enum.GetNames(type))
            {
                try
                {
                    var val = Convert.ChangeType(Enum.Parse(type, name), Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
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

        Type? baseType = type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType) ? type.BaseType : null;
        Type[] interfaces = type.GetInterfaces();
        FieldInfo[] fields = type.GetFields(flags).Where(f => !f.IsSpecialName).ToArray();
        PropertyInfo[] properties = type.GetProperties(flags);
        EventInfo[] events = type.GetEvents(flags);
        ConstructorInfo[] constructors = type.GetConstructors(flags);
        MethodInfo[] methods = type.GetMethods(flags).Where(m => !m.IsSpecialName).ToArray();

        if (_stripInterop)
        {
            int before = fields.Length + constructors.Length;
            fields = fields.Where(f => !IsInteropPlumbingField(f)).ToArray();
            constructors = constructors.Where(c => !IsInteropPlumbingConstructor(c)).ToArray();
            Interlocked.Add(ref _omittedInteropMembers, before - fields.Length - constructors.Length);
        }

        // 파일에 실제로 등장하는 타입만으로 using을 만들고, 이름이 겹치는 타입은 네임스페이스를 붙인다.
        // (예: Il2CppSystem.Collections.Generic.List 를 System List 로 오인하지 않도록)
        var referenced = new List<Type?> { baseType };
        referenced.AddRange(interfaces);
        referenced.AddRange(fields.Select(f => f.FieldType));
        referenced.AddRange(properties.Select(p => p.PropertyType));
        referenced.AddRange(events.Select(e => e.EventHandlerType));
        referenced.AddRange(constructors.SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));
        referenced.AddRange(methods.Select(m => m.ReturnType));
        referenced.AddRange(methods.SelectMany(m => m.GetParameters()).Select(p => p.ParameterType));

        var names = new TypeNameFormatter(info.Namespace, referenced, GetNamespaceIndex);
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
            string val = "";
            if (field.IsLiteral && !field.IsInitOnly)
            {
                try { val = $" = {FormatLiteral(field.GetRawConstantValue())}"; } catch { }
            }
            info.Fields.Add($"{GetFieldModifiers(field)} {names.Format(field.FieldType)} {field.Name}{val};");
        }

        foreach (var prop in properties)
        {
            var getMethod = prop.GetGetMethod(true);
            var setMethod = prop.GetSetMethod(true);

            string getStr = getMethod != null ? (getMethod.IsPublic ? "get; " : $"{GetMethodAccess(getMethod)} get; ") : "";
            string setStr = setMethod != null ? (setMethod.IsPublic ? "set; " : $"{GetMethodAccess(setMethod)} set; ") : "";
            string access = getMethod != null ? GetMethodAccess(getMethod) : (setMethod != null ? GetMethodAccess(setMethod) : "public");
            var accessor = getMethod ?? setMethod;
            string mods = accessor != null ? GetMethodModifiers(accessor) : "";

            info.Properties.Add($"{access}{mods} {names.Format(prop.PropertyType)} {prop.Name} {{ {getStr}{setStr}}}");
        }

        foreach (var ev in events)
        {
            info.Events.Add($"public event {names.Format(ev.EventHandlerType)} {ev.Name};");
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
            string retType = (method.ReturnType.IsByRef ? "ref " : "") + names.Format(method.ReturnType);
            string paramsStr = string.Join(", ", method.GetParameters().Select(p => FormatParameter(p, names)));
            string genArgs = method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(t => t.Name))}>" : "";

            info.Methods.Add($"{GetMethodAccess(method)}{GetMethodModifiers(method)} {retType} {method.Name}{genArgs}({paramsStr});");
        }

        return info;
    }

    private static string GetTypeAccess(Type type)
    {
        if (type.IsPublic || type.IsNestedPublic) return "public";
        if (type.IsNestedPrivate) return "private";
        if (type.IsNestedFamily) return "protected";
        if (type.IsNestedAssembly || type.IsNotPublic) return "internal";
        if (type.IsNestedFamORAssem) return "protected internal";
        if (type.IsNestedFamANDAssem) return "private protected";
        return "internal";
    }

    private static string FormatParameter(ParameterInfo p, TypeNameFormatter names)
    {
        string prefix = "";
        if (p.IsOut) prefix = "out ";
        else if (p.ParameterType.IsByRef) prefix = "ref ";
        else if (p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Any()) prefix = "params ";

        string def = p.HasDefaultValue ? $" = {FormatLiteral(p.DefaultValue)}" : "";
        return $"{prefix}{names.Format(p.ParameterType)} {p.Name}{def}";
    }

    // 상수/기본값을 C# 리터럴 형태로 표기한다. (문자열 따옴표·이스케이프, true/false, 문화권과 무관한 숫자)
    private static string FormatLiteral(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case string s: return "\"" + EscapeLiteral(s, '"') + "\"";
            case char c: return "'" + EscapeLiteral(c.ToString(), '\'') + "'";
            case bool b: return b ? "true" : "false";
            // float/decimal은 접미사가 없으면 C# 상수로 컴파일되지 않는다. "R"은 런타임과 무관하게 값을 정확히 되살리는 표기다.
            case float f:
                if (float.IsNaN(f)) return "float.NaN";
                if (float.IsInfinity(f)) return f > 0 ? "float.PositiveInfinity" : "float.NegativeInfinity";
                return f.ToString("R", CultureInfo.InvariantCulture) + "F";
            case double d:
                if (double.IsNaN(d)) return "double.NaN";
                if (double.IsInfinity(d)) return d > 0 ? "double.PositiveInfinity" : "double.NegativeInfinity";
                return d.ToString("R", CultureInfo.InvariantCulture);
            case decimal m: return m.ToString(CultureInfo.InvariantCulture) + "M";
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

    private static string GetFieldModifiers(FieldInfo f)
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

    private static string GetMethodAccess(MethodBase m)
    {
        if (m.IsPublic) return "public";
        if (m.IsPrivate) return "private";
        if (m.IsFamily) return "protected";
        if (m.IsAssembly) return "internal";
        if (m.IsFamilyOrAssembly) return "protected internal";
        return "internal";
    }

    private static string GetMethodModifiers(MethodBase m)
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

    private Dictionary<string, HashSet<string>> GetNamespaceIndex(Assembly asm) => _namespaceIndex.GetOrAdd(asm, BuildNamespaceIndex);

    private Dictionary<string, HashSet<string>> BuildNamespaceIndex(Assembly asm)
    {
        var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        Type?[] types;
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

        public TypeNameFormatter(string currentNamespace, IEnumerable<Type?> referencedTypes, Func<Assembly, Dictionary<string, HashSet<string>>> getNamespaceIndex)
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

        public string Format(Type? t)
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

        private static void CollectNamedTypes(Type? t, List<Type> output)
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

    private static int WriteIndividualFiles(string decompileDir, List<TypeSignatureInfo> infos, ParallelOptions parallel, Action<string, string>? fileWritten)
    {
        // 경로 결정은 순서에 의존하므로(대소문자만 다른 이름엔 _2 접미사) 순차로 하고, 실제 쓰기만 병렬로 한다.
        // Windows 파일 시스템은 대소문자를 구분하지 않으므로, 같은 경로가 또 나오면 번호를 붙여 덮어쓰기를 막는다.
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new string[infos.Count];

        for (int i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            string nsPath = info.Namespace.Length == 0 ? "Global" : info.Namespace.Replace('.', Path.DirectorySeparatorChar);
            string targetDir = Path.Combine(decompileDir, nsPath);

            string safeFileName = SanitizeFileName(info.FileStem);
            string filePath = Path.Combine(targetDir, safeFileName + ".cs");
            for (int suffix = 2; !usedPaths.Add(filePath); suffix++)
            {
                filePath = Path.Combine(targetDir, $"{safeFileName}_{suffix}.cs");
            }
            filePaths[i] = filePath;
        }

        foreach (var dir in filePaths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(dir!);
        }

        Parallel.For(0, infos.Count, parallel, i =>
        {
            string content = RenderType(infos[i]);
            File.WriteAllText(filePaths[i], content, Utf8NoBom);
            fileWritten?.Invoke(filePaths[i], content);
        });
        return infos.Count;
    }

    private static string RenderType(TypeSignatureInfo info)
    {
        var sb = new StringBuilder(1024);
        foreach (var ns in info.Usings) sb.Append("using ").Append(ns).AppendLine(";");
        if (info.Usings.Count > 0) sb.AppendLine();

        // 전역 타입은 namespace 블록 없이 쓴다. (가짜 "Global" 네임스페이스를 쓰면 모드 코드에서 using 오류가 난다)
        bool hasNamespace = info.Namespace.Length > 0;
        string indent = hasNamespace ? "    " : "";
        if (hasNamespace)
        {
            sb.Append("namespace ").AppendLine(info.Namespace);
            sb.AppendLine("{");
        }

        var bases = new List<string>();
        if (!string.IsNullOrEmpty(info.BaseType)) bases.Add(info.BaseType!);
        bases.AddRange(info.Interfaces);
        string baseClause = bases.Count > 0 ? $" : {string.Join(", ", bases)}" : "";

        if (info.DeclaringTypeName != null) sb.Append(indent).Append("// Nested in: ").AppendLine(info.DeclaringTypeName);
        sb.Append(indent).Append(info.AccessModifiers).Append(' ').Append(info.Kind).Append(' ').Append(info.Name).AppendLine(baseClause);
        sb.Append(indent).AppendLine("{");

        string memberIndent = indent + "    ";
        if (info.Kind == "enum")
        {
            foreach (var member in info.EnumMembers) sb.Append(memberIndent).Append(member).AppendLine(",");
        }
        else
        {
            AppendSection(sb, memberIndent, "Fields", info.Fields);
            AppendSection(sb, memberIndent, "Properties", info.Properties);
            AppendSection(sb, memberIndent, "Events", info.Events);
            AppendSection(sb, memberIndent, "Constructors", info.Constructors);
            AppendSection(sb, memberIndent, "Methods", info.Methods);
        }

        sb.Append(indent).AppendLine("}");
        if (hasNamespace) sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string indent, string title, List<string> lines)
    {
        if (lines.Count == 0) return;
        if (sb[sb.Length - 1 - Environment.NewLine.Length] != '{') sb.AppendLine();
        sb.Append(indent).Append("// ").AppendLine(title);
        foreach (var line in lines) sb.Append(indent).AppendLine(line);
    }

    private static string SanitizeFileName(string fileName)
    {
        var sb = new StringBuilder(fileName.Length);
        foreach (char c in fileName)
        {
            sb.Append(InvalidFileNameChars.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }
}
