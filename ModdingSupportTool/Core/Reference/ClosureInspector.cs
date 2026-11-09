using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModdingSupportTool.Core.Detection;

namespace ModdingSupportTool.Core.Reference;

public sealed record ClosureReport(string Text, bool IsError = false);

/// <summary>Il2CppInterop 레퍼런스에서 이름을 찾은 결과. TypeResolved인데 Name이 없으면 Interop 어셈블리에 그 멤버가 없다는 뜻이다.</summary>
public readonly record struct InteropLookup(string? Name, bool TypeResolved);

/// <summary>
/// Il2Cpp 게임에서 기본 덤프(Il2CppInterop 시그니처)만으로 훅을 잡다 막혔을 때 쓰는 조회 도구.
/// Cpp2IL/Il2CppDumper DummyDll에서 메서드 하나(또는 타입 하나)에 딸린 람다·클로저·코루틴 상태 머신·로컬 함수를 찾아
/// RVA, 필드 오프셋, 같은 기계어를 공유하는(ICF) 다른 메서드, Il2CppInterop에서 쓰는 이름을 보여 준다.
/// 전체 덤프에 주소를 늘 붙이면 공용 코드 경고가 수천 줄 생기므로, 필요한 순간에 필요한 범위만 본다.
/// </summary>
public sealed class ClosureInspector : IDisposable
{
    // MelonLoader가 Il2CppAssemblies를 만들 때 Cpp2IL로 함께 생성하는 더미 어셈블리 폴더
    public static readonly string MelonLoaderCpp2IlOut = Path.Combine("Dependencies", "Il2CppAssemblyGenerator", "Cpp2IL", "cpp2il_out");

    // Il2CppDumper는 Il2CppDummyDll 어셈블리에, Cpp2IL은 각 어셈블리 안 Cpp2ILInjected 네임스페이스에 주소 속성을 정의한다.
    private static readonly string[] DummyAttributeNamespaces = { "Il2CppDummyDll", "Cpp2ILInjected" };

    // 이 횟수 이상 공유되는 주소는 빈 메서드·단순 반환 같은 공용 코드라 공유 상대를 나열하지 않는다.
    private const int CommonCodeThreshold = 20;
    private const int PeerListLimit = 6;

    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["System.Void"] = "void", ["System.Boolean"] = "bool", ["System.Byte"] = "byte", ["System.SByte"] = "sbyte",
        ["System.Char"] = "char", ["System.Decimal"] = "decimal", ["System.Double"] = "double", ["System.Single"] = "float",
        ["System.Int32"] = "int", ["System.UInt32"] = "uint", ["System.Int64"] = "long", ["System.UInt64"] = "ulong",
        ["System.Int16"] = "short", ["System.UInt16"] = "ushort", ["System.String"] = "string", ["System.Object"] = "object"
    };

    private static readonly Regex InvalidIdentifierChars = new(@"[^A-Za-z0-9_]", RegexOptions.Compiled);

    private static readonly object CacheGate = new();
    private static ClosureInspector? _cached;

    private readonly MetadataLoadContext _mlc;
    private readonly List<Assembly> _assemblies = new();
    private readonly Dictionary<Assembly, Type[]> _types = new();
    private Dictionary<string, List<AddressEntry>>? _addressMap;
    private readonly DateTime _stamp;

    public string Directory { get; }
    public string Flavor { get; }

    private ClosureInspector(string directory, DateTime stamp)
    {
        Directory = directory;
        _stamp = stamp;

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in System.IO.Directory.GetFiles(directory, "*.dll"))
        {
            paths[Path.GetFileNameWithoutExtension(path)] = path;
        }

        _mlc = new MetadataLoadContext(new SimpleNameResolver(paths), "mscorlib");
        foreach (string path in paths.Values.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try { _assemblies.Add(_mlc.LoadFromAssemblyPath(path)); }
            catch (Exception) { /* 메타데이터가 깨진 DLL은 건너뛴다 */ }
        }

        Flavor = paths.ContainsKey("Il2CppDummyDll") ? "Il2CppDumper" : "Cpp2IL";
    }

    public void Dispose() => _mlc.Dispose();

    // ───────────────────────────── 폴더 찾기 ─────────────────────────────

    /// <summary>
    /// DummyDll 폴더 자체, Il2CppDumper 출력 폴더(DummyDll 하위 폴더), 게임 폴더(MelonLoader의 cpp2il_out) 중 무엇을 넣어도 더미 폴더를 찾는다.
    /// </summary>
    public static string? FindDummyDirectory(string? path, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            problem = "DummyDll 폴더나 게임 폴더가 필요합니다.";
            return null;
        }

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            problem = $"경로 형식이 잘못됐습니다: {path} ({ex.Message})";
            return null;
        }

        string? dir = File.Exists(full) ? Path.GetDirectoryName(full) : full;
        if (dir == null || !System.IO.Directory.Exists(dir))
        {
            problem = $"경로가 없습니다: {path}";
            return null;
        }

        if (LooksLikeDummyFolder(dir)) return dir;
        string dumperOut = Path.Combine(dir, "DummyDll");
        if (LooksLikeDummyFolder(dumperOut)) return dumperOut;

        var game = GameLocator.Find(dir);
        if (game?.MelonLoaderDirectory != null)
        {
            string cpp2il = Path.Combine(game.MelonLoaderDirectory, MelonLoaderCpp2IlOut);
            if (LooksLikeDummyFolder(cpp2il)) return cpp2il;
        }

        problem = $"DummyDll을 찾지 못했습니다: {path}\n" +
                  $"- MelonLoader로 게임을 한 번 실행했다면 <게임>/MelonLoader/{MelonLoaderCpp2IlOut.Replace('\\', '/')} 에 있습니다.\n" +
                  "- 아니면 Il2CppDumper(GameAssembly.dll + global-metadata.dat)의 출력 폴더나 그 안의 DummyDll 폴더를 지정하세요.";
        return null;
    }

    private static bool LooksLikeDummyFolder(string dir) =>
        System.IO.Directory.Exists(dir) && File.Exists(Path.Combine(dir, "mscorlib.dll")) &&
        System.IO.Directory.EnumerateFiles(dir, "Assembly-CSharp*.dll").Any();

    /// <summary>같은 폴더를 연달아 조회할 때(MCP) 어셈블리와 주소 색인을 다시 만들지 않는다.</summary>
    public static T Use<T>(string directory, Func<ClosureInspector, T> action)
    {
        string key = Path.GetFullPath(directory).TrimEnd('\\', '/');
        DateTime stamp = System.IO.Directory.GetLastWriteTimeUtc(key);
        lock (CacheGate)
        {
            if (_cached == null || !string.Equals(_cached.Directory, key, StringComparison.OrdinalIgnoreCase) || _cached._stamp != stamp)
            {
                _cached?.Dispose();
                _cached = null;
                _cached = new ClosureInspector(key, stamp);
            }
            return action(_cached);
        }
    }

    // ───────────────────────────── 조회 ─────────────────────────────

    /// <param name="typeQuery">타입 이름 (Il2CppInterop 이름 Il2Cpp.Foo, __c__DisplayClass2_0 형태도 받음). 메서드 없이 "Type.Method"로 줘도 된다.</param>
    /// <param name="method">원래 메서드 이름. 주면 그 메서드에서 생긴 람다·코루틴만 보여 준다.</param>
    /// <param name="interopLookup">기본 레퍼런스(Il2CppInterop 덤프)에서 (중첩 타입 표시 이름, 멤버 이름)으로 실제 Interop 전체 이름을 찾는 함수. 없으면 추정 이름만 낸다.</param>
    public ClosureReport Inspect(string typeQuery, string? method, string? assemblyName, Func<string, string?, InteropLookup>? interopLookup, int maxChars, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(typeQuery)) return new ClosureReport("type이 비어 있습니다.", true);
        if (!_assemblies.Any(IsDummyAssembly))
        {
            return new ClosureReport($"주소 속성이 있는 DummyDll이 아닙니다: {Directory}\n" +
                                     "Mono 게임의 Managed 폴더라면 메서드 본문이 그대로 있으니 ilspy 덤프에서 람다를 직접 보세요.", true);
        }

        string query = typeQuery.Trim();
        string? methodFilter = string.IsNullOrWhiteSpace(method) ? null : method.Trim();

        var matches = FindTypes(query, assemblyName);
        if (matches.Count == 0 && methodFilter == null)
        {
            int dot = query.LastIndexOf('.');
            if (dot > 0)
            {
                var split = FindTypes(query[..dot], assemblyName);
                if (split.Count > 0)
                {
                    matches = split;
                    methodFilter = query[(dot + 1)..];
                }
            }
        }

        if (matches.Count == 0)
        {
            return new ClosureReport($"'{query}' 타입을 DummyDll에서 찾지 못했습니다. ({Directory})\n" +
                                     "네임스페이스 포함 이름이나 단순 이름을 써 보세요. 다른 어셈블리라면 assembly를 지정하세요.", true);
        }
        if (matches.Count > 1)
        {
            return new ClosureReport($"'{query}'에 해당하는 타입이 {matches.Count}개입니다. 네임스페이스 포함 이름으로 지정하세요:\n" +
                                     string.Join("\n", matches.Take(20).Select(t => $"  {DisplayFullName(t)}  ({t.Assembly.GetName().Name})")), true);
        }

        var type = matches[0];
        ct.ThrowIfCancellationRequested();
        var map = GetAddressMap(ct);
        var writer = new ReportWriter(this, map, interopLookup);

        if (IsCompilerGeneratedName(type.Name))
        {
            writer.WriteGeneratedType(type, methodFilter, header: true);
        }
        else
        {
            writer.WriteOwner(type, methodFilter);
        }

        string text = writer.Finish();
        if (maxChars > 0 && text.Length > maxChars)
        {
            text = text[..maxChars] + $"\n(… {maxChars:N0}자에서 잘림. method로 범위를 좁히세요)";
        }
        return new ClosureReport(text);
    }

    private List<Type> FindTypes(string query, string? assemblyName)
    {
        // Il2CppInterop 이름(Il2Cpp.Foo, Il2CppRhythmGame.Foo, Foo.__c__DisplayClass2_0)도 원래 이름과 맞도록 식별자 밖 문자를 _로 바꿔 비교한다.
        // 제네릭 표기(ListViewerBase<T>, ListViewerBase`1)는 떼고 비교한다. 비교 키도 arity를 뗀 이름이다.
        string plain = Regex.Replace(query, @"<[^<>]*>|`\d+", "");
        while (plain != (plain = Regex.Replace(plain, @"<[^<>]*>", ""))) { }
        var variants = new List<string> { query, plain };
        foreach (string q in new[] { query, plain })
        {
            if (q.StartsWith("Il2Cpp.", StringComparison.Ordinal)) variants.Add(q["Il2Cpp.".Length..]);
            else if (q.StartsWith("Il2Cpp", StringComparison.Ordinal) && q.Contains('.')) variants.Add(q["Il2Cpp".Length..]);
        }
        var wanted = variants.Select(Mangle).Distinct().ToList();

        IEnumerable<Assembly> scope = _assemblies;
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            string name = Path.GetFileNameWithoutExtension(assemblyName.Trim());
            scope = _assemblies.Where(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // 게임 코드를 먼저 본다. 거기 없을 때만 전체 어셈블리를 본다.
            var game = _assemblies.Where(a => a.GetName().Name?.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) == true).ToList();
            var inGame = MatchTypes(game, wanted);
            if (inGame.Count > 0) return inGame;
        }
        return MatchTypes(scope, wanted);
    }

    private List<Type> MatchTypes(IEnumerable<Assembly> assemblies, List<string> wanted)
    {
        var all = assemblies.SelectMany(GetTypes).ToList();
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            foreach (Func<Type, string> key in new Func<Type, string>[] { DisplayFullName, DisplayName, t => StripArity(t.Name) })
            {
                var hits = all.Where(t => wanted.Any(w => string.Equals(Mangle(key(t)), w, comparison))).ToList();
                if (hits.Count > 0) return hits;
            }
        }
        return new List<Type>();
    }

    // ───────────────────────────── 주소 색인 (ICF) ─────────────────────────────

    // 모든 어셈블리의 메서드를 RVA로 묶는다. ICF는 GameAssembly.dll 전체에서 일어나므로 UnityEngine·mscorlib 메서드와도 겹친다.
    // MetadataLoadContext로 메서드마다 속성을 읽으면 큰 게임에서 15초가 넘게 걸려서, 메타데이터 테이블을 직접 병렬로 읽는다.
    private Dictionary<string, List<AddressEntry>> GetAddressMap(CancellationToken ct)
    {
        if (_addressMap != null) return _addressMap;

        var perAssembly = new ConcurrentBag<List<(string Rva, AddressEntry Entry)>>();
        Parallel.ForEach(_assemblies.Select(a => a.Location).Where(File.Exists),
            new ParallelOptions { CancellationToken = ct },
            path => perAssembly.Add(AddressIndexer.Index(path)));

        var map = new Dictionary<string, List<AddressEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rva, entry) in perAssembly.SelectMany(x => x))
        {
            if (!map.TryGetValue(rva, out var list)) map[rva] = list = new List<AddressEntry>();
            list.Add(entry);
        }
        return _addressMap = map;
    }

    private sealed record AddressEntry(string Assembly, int Token, string Display);

    private static class AddressIndexer
    {
        public static List<(string, AddressEntry)> Index(string path)
        {
            var result = new List<(string, AddressEntry)>();
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) return result;
                var md = pe.GetMetadataReader();
                if (!md.IsAssembly) return result;
                string asmName = md.GetString(md.GetAssemblyDefinition().Name);
                var isAddressCtor = new Dictionary<EntityHandle, bool>();
                var provider = new StringTypeProvider();

                foreach (var typeHandle in md.TypeDefinitions)
                {
                    var typeDef = md.GetTypeDefinition(typeHandle);
                    string? typeName = null;
                    foreach (var methodHandle in typeDef.GetMethods())
                    {
                        var methodDef = md.GetMethodDefinition(methodHandle);
                        foreach (var attrHandle in methodDef.GetCustomAttributes())
                        {
                            var attr = md.GetCustomAttribute(attrHandle);
                            if (!isAddressCtor.TryGetValue(attr.Constructor, out bool isAddress))
                            {
                                isAddressCtor[attr.Constructor] = isAddress = IsAddressAttribute(md, attr.Constructor);
                            }
                            if (!isAddress) continue;

                            string? rva = null;
                            try
                            {
                                var value = attr.DecodeValue(provider);
                                foreach (var arg in value.NamedArguments)
                                {
                                    if (arg.Name == "RVA") rva = arg.Value?.ToString();
                                }
                            }
                            catch (Exception) { /* 형식이 다른 속성은 건너뛴다 */ }

                            if (string.IsNullOrEmpty(rva) || rva == "-1" || rva == "0x0") break;
                            typeName ??= TypeDisplay(md, typeHandle);
                            string name = md.GetString(methodDef.Name);
                            result.Add((rva, new AddressEntry(asmName, MetadataTokens.GetToken(methodHandle), $"{typeName}.{name}")));
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 메타데이터가 깨진 DLL은 공유 주소 집계에서 뺀다.
            }
            return result;
        }

        private static bool IsAddressAttribute(MetadataReader md, EntityHandle ctor)
        {
            StringHandle name, ns;
            switch (ctor.Kind)
            {
                case HandleKind.MethodDefinition:
                    var declaring = md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType());
                    name = declaring.Name; ns = declaring.Namespace;
                    break;
                case HandleKind.MemberReference:
                    var parent = md.GetMemberReference((MemberReferenceHandle)ctor).Parent;
                    if (parent.Kind == HandleKind.TypeReference)
                    {
                        var tr = md.GetTypeReference((TypeReferenceHandle)parent);
                        name = tr.Name; ns = tr.Namespace;
                    }
                    else if (parent.Kind == HandleKind.TypeDefinition)
                    {
                        var td = md.GetTypeDefinition((TypeDefinitionHandle)parent);
                        name = td.Name; ns = td.Namespace;
                    }
                    else return false;
                    break;
                default:
                    return false;
            }
            return md.StringComparer.Equals(name, "AddressAttribute") && Array.IndexOf(DummyAttributeNamespaces, md.GetString(ns)) >= 0;
        }

        // DisplayFullName과 같은 표기: 네임스페이스.Outer.Inner (제네릭 arity 제거)
        private static string TypeDisplay(MetadataReader md, TypeDefinitionHandle handle)
        {
            var parts = new List<string>();
            var current = handle;
            string ns = "";
            while (!current.IsNil)
            {
                var td = md.GetTypeDefinition(current);
                parts.Add(StripArity(md.GetString(td.Name)));
                ns = md.GetString(td.Namespace);
                current = td.GetDeclaringType();
            }
            parts.Reverse();
            string name = string.Join(".", parts);
            return ns.Length > 0 ? ns + "." + name : name;
        }
    }

    private sealed class StringTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }

    private Type[] GetTypes(Assembly asm)
    {
        if (_types.TryGetValue(asm, out var cached)) return cached;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Select(t => t!).ToArray(); }
        catch (Exception) { types = Array.Empty<Type>(); }
        return _types[asm] = types;
    }

    // ───────────────────────────── 더미 속성 ─────────────────────────────

    private static bool IsDummyAssembly(Assembly asm)
    {
        try
        {
            if (asm.GetReferencedAssemblies().Any(r => string.Equals(r.Name, "Il2CppDummyDll", StringComparison.OrdinalIgnoreCase))) return true;
            return asm.GetType("Cpp2ILInjected.AddressAttribute", false) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // [Address(RVA = "0x..", Offset = "0x..")] 같은 더미 속성의 이름 붙은 인자를 속성을 만들지 않고 메타데이터로만 읽는다.
    private static Dictionary<string, string>? ReadDummyAttribute(MemberInfo member, string attributeName)
    {
        IList<CustomAttributeData> data;
        try { data = member.GetCustomAttributesData(); }
        catch (Exception) { return null; }

        foreach (var attr in data)
        {
            Type attrType;
            try { attrType = attr.AttributeType; }
            catch (Exception) { continue; }
            if (attrType.Name != attributeName || Array.IndexOf(DummyAttributeNamespaces, attrType.Namespace) < 0) continue;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in attr.NamedArguments) values[arg.MemberName] = arg.TypedValue.Value?.ToString() ?? string.Empty;
            return values;
        }
        return null;
    }

    private static string? GetRva(MethodBase m)
    {
        var values = ReadDummyAttribute(m, "AddressAttribute");
        if (values == null || !values.TryGetValue("RVA", out var rva) || rva.Length == 0 || rva == "-1" || rva == "0x0") return null;
        return rva;
    }

    private static string? GetFieldOffset(FieldInfo f)
    {
        var values = ReadDummyAttribute(f, "FieldOffsetAttribute");
        return values != null && values.TryGetValue("Offset", out var offset) && offset.Length > 0 ? offset : null;
    }

    // ───────────────────────────── 이름 ─────────────────────────────

    internal static bool IsCompilerGeneratedName(string name) => name.StartsWith("<", StringComparison.Ordinal);

    /// <summary>Il2CppInterop이 원래 이름의 식별자 밖 문자(&lt; &gt; | 등)를 _로 바꾸는 규칙을 흉내 낸다. (예: &lt;Start&gt;b__5_0 → _Start_b__5_0)</summary>
    internal static string Mangle(string name) => InvalidIdentifierChars.Replace(name.Replace('+', '.'), m => m.Value == "." ? "." : "_");

    // <Start>b__5_0 → Start, <<Start>g__Local|5_0>b__1 → Start, <>c → "" (특정 메서드에 속하지 않음)
    internal static string? ParentMethodName(string name)
    {
        if (!IsCompilerGeneratedName(name)) return null;
        int depth = 0;
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '<') depth++;
            else if (name[i] == '>' && --depth == 0)
            {
                string inner = name[1..i];
                return IsCompilerGeneratedName(inner) ? ParentMethodName(inner) : inner;
            }
        }
        return null;
    }

    private static string StripArity(string name)
    {
        int tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }

    internal static string DisplayName(Type t) =>
        t.IsNested && t.DeclaringType != null ? DisplayName(t.DeclaringType) + "." + StripArity(t.Name) : StripArity(t.Name);

    internal static string DisplayFullName(Type t)
    {
        Type outer = t;
        while (outer.IsNested && outer.DeclaringType != null) outer = outer.DeclaringType;
        return string.IsNullOrEmpty(outer.Namespace) ? DisplayName(t) : outer.Namespace + "." + DisplayName(t);
    }

    internal static string FormatType(Type t)
    {
        try
        {
            if (t.IsByRef) return FormatType(t.GetElementType()!);
            if (t.IsPointer) return FormatType(t.GetElementType()!) + "*";
            if (t.IsArray) return FormatType(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsGenericParameter) return t.Name;
            if (!t.IsGenericType && t.FullName != null && Keywords.TryGetValue(t.FullName, out var keyword)) return keyword;
            string name = StripArity(t.Name);
            if (t.IsGenericType) name += "<" + string.Join(", ", t.GetGenericArguments().Select(FormatType)) + ">";
            return name;
        }
        catch (Exception)
        {
            return t.Name;
        }
    }

    // ───────────────────────────── 출력 ─────────────────────────────

    private sealed class ReportWriter
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private readonly ClosureInspector _owner;
        private readonly Dictionary<string, List<AddressEntry>> _map;
        private readonly Func<string, string?, InteropLookup>? _interopLookup;
        private readonly StringBuilder _sb = new();
        private bool _sawSharedAddress;
        private bool _sawStateMachine;
        private bool _sawMissingInInterop;
        private int _items;

        public ReportWriter(ClosureInspector owner, Dictionary<string, List<AddressEntry>> map, Func<string, string?, InteropLookup>? interopLookup)
        {
            _owner = owner;
            _map = map;
            _interopLookup = interopLookup;
        }

        public void WriteOwner(Type type, string? methodFilter)
        {
            WriteHeader(type, methodFilter);

            var methods = SafeMethods(type);
            if (methodFilter != null)
            {
                var originals = methods.Where(m => NameEquals(m.Name, methodFilter) || (methodFilter is ".ctor" or "ctor" && m is ConstructorInfo)).ToList();
                if (originals.Count > 0)
                {
                    _sb.AppendLine("원래 메서드:");
                    foreach (var m in originals) WriteMethod(m, "  ", type);
                    _sb.AppendLine();
                }
            }

            // 람다가 this만 캡처하면 클로저 클래스 없이 소유 타입의 인스턴스 메서드(<X>b__N_M)로 생긴다. 로컬 함수(<X>g__Name|N_M)도 여기 있다.
            var ownMembers = methods.Where(m => IsCompilerGeneratedName(m.Name) && Matches(m.Name, methodFilter)).ToList();
            if (ownMembers.Count > 0)
            {
                _sb.AppendLine($"소유 타입에 직접 생긴 람다·로컬 함수 ({DisplayName(type)}):");
                foreach (var m in ownMembers) WriteMethod(m, "  ", type);
                _sb.AppendLine();
                _items++;
            }

            Type[] nested;
            try { nested = type.GetNestedTypes(All); }
            catch (Exception) { nested = Array.Empty<Type>(); }

            foreach (var n in nested.Where(n => IsCompilerGeneratedName(n.Name)).OrderBy(n => n.Name, StringComparer.Ordinal))
            {
                WriteGeneratedType(n, methodFilter, header: false);
            }

            if (_items == 0)
            {
                _sb.AppendLine(methodFilter != null
                    ? $"'{methodFilter}'에서 생긴 람다·클로저·코루틴이 없습니다. (메서드 이름 철자를 확인하거나 method 없이 타입 전체를 보세요)"
                    : "컴파일러가 만든 람다·클로저·코루틴이 없습니다.");
            }
        }

        public void WriteGeneratedType(Type type, string? methodFilter, bool header)
        {
            if (header) WriteHeader(type, methodFilter);

            bool stateMachine = IsStateMachine(type);
            var methods = SafeMethods(type).Where(m => m is not ConstructorInfo).ToList();

            List<MethodBase> shown;
            if (stateMachine)
            {
                // 코루틴/async 본문은 MoveNext에 있다. 나머지(IEnumerator.Reset, get_Current 등)는 훅 대상이 아니다.
                if (!header && !Matches(type.Name, methodFilter)) return;
                shown = methods.Where(m => m.Name == "MoveNext").ToList();
            }
            else
            {
                shown = methods.Where(m => header || Matches(m.Name, methodFilter)).ToList();
                if (!header && methodFilter != null && shown.Count == 0) return;
                // <>c는 메서드에 딸리지 않은 정적 람다 모음이라 필터 없이 볼 때 람다만 보여 준다.
                if (!header && methodFilter == null) shown = shown.Where(m => IsCompilerGeneratedName(m.Name)).ToList();
                if (!header && shown.Count == 0) return;
            }

            string kind = stateMachine ? StateMachineKind(type)
                : type.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal) ? "클로저(캡처 변수 보관)"
                : type.Name == "<>c" || type.Name.StartsWith("<>c__", StringComparison.Ordinal) ? "정적 람다 모음"
                : "컴파일러 생성 타입";
            string parent = ParentMethodName(type.Name) is { Length: > 0 } p ? $" ← {p}()" : "";

            _sb.AppendLine($"[{kind}] {DisplayName(type)}{parent}");
            _sb.AppendLine($"  Interop 타입: {InteropTypeName(type)}");
            if (stateMachine) _sawStateMachine = true;

            var fields = SafeFields(type);
            if (fields.Count > 0)
            {
                string label = stateMachine ? "상태·지역 변수" : "필드";
                _sb.AppendLine($"  {label}: " + string.Join(", ", fields.Select(f => $"{FormatType(f.FieldType)} {f.Name}" + (GetFieldOffset(f) is { } o ? $" @{o}" : ""))));
            }

            foreach (var m in shown) WriteMethod(m, "  ", type);
            _sb.AppendLine();
            _items++;
        }

        private void WriteHeader(Type type, string? methodFilter)
        {
            _sb.AppendLine($"{DisplayFullName(type)}" + (methodFilter != null ? $" · {methodFilter}()" : "") +
                           $"  ({type.Assembly.GetName().Name}, {_owner.Flavor} DummyDll: {_owner.Directory})");
            _sb.AppendLine();
        }

        private void WriteMethod(MethodBase m, string indent, Type declaring)
        {
            string ret = m is MethodInfo mi ? FormatType(mi.ReturnType) + " " : "";
            string parameters;
            try { parameters = string.Join(", ", m.GetParameters().Select(p => $"{FormatType(p.ParameterType)} {p.Name}")); }
            catch (Exception) { parameters = "?"; }

            string name = m is ConstructorInfo ? ".ctor" : m.Name;
            string? rva = GetRva(m);
            _sb.AppendLine($"{indent}{(m.IsStatic ? "static " : "")}{ret}{name}({parameters})  RVA {rva ?? "없음"}");

            if (IsCompilerGeneratedName(m.Name) || IsCompilerGeneratedName(declaring.Name))
            {
                _sb.AppendLine($"{indent}  Interop: {InteropMemberName(declaring, name)}");
            }

            if (m is MethodInfo method && FindStateMachine(method) is { } sm)
            {
                var moveNext = SafeMethods(sm).FirstOrDefault(x => x.Name == "MoveNext");
                _sb.AppendLine($"{indent}  → 본문은 {DisplayName(sm)}.MoveNext  RVA {(moveNext != null ? GetRva(moveNext) ?? "없음" : "없음")}" +
                               $"  (Interop: {InteropMemberName(sm, "MoveNext")})");
                _sawStateMachine = true;
            }

            if (rva != null && _map.TryGetValue(rva, out var sharing) && sharing.Count > 1)
            {
                _sawSharedAddress = true;
                if (sharing.Count >= CommonCodeThreshold)
                {
                    _sb.AppendLine($"{indent}  ⚠ ICF 공용 코드: {sharing.Count:N0}개 메서드가 이 주소를 공유 (빈 메서드·단순 반환 같은 흔한 기계어). 이 주소의 네이티브 훅은 전부에서 실행됩니다.");
                }
                else
                {
                    string self = m.Module.Assembly.GetName().Name ?? "";
                    var peers = sharing.Where(p => !(p.Token == m.MetadataToken && p.Assembly == self)).Select(Describe).Distinct().ToList();
                    _sb.AppendLine($"{indent}  ⚠ ICF: {sharing.Count}개 메서드가 같은 기계어 → " + string.Join(", ", peers.Take(PeerListLimit)) +
                                   (peers.Count > PeerListLimit ? $" 외 {peers.Count - PeerListLimit}개" : ""));
                }
            }
        }

        public string Finish()
        {
            if (_sawSharedAddress || _sawStateMachine || _sawMissingInInterop || _interopLookup == null) _sb.AppendLine("참고:");
            if (_sawMissingInInterop)
            {
                _sb.AppendLine("- ✗ 표시: Il2CppInterop이 이 멤버를 만들지 않았습니다(로컬 함수 등). typeof(...)로 이름을 지정할 수 없으니 위 RVA로 네이티브 훅을 걸거나 " +
                               "(GameAssembly.dll 베이스 + RVA), 이 멤버를 부르는 원래 메서드를 패치하세요.");
            }
            if (_sawStateMachine)
            {
                _sb.AppendLine("- 코루틴/async 메서드를 패치하면 상태 머신 객체를 만드는 순간만 잡힙니다. 본문 실행을 잡으려면 상태 머신의 MoveNext를 패치하고, " +
                               "__instance의 상태·지역 변수 필드(<>1__state, <>4__this, <i>5__2 등)를 읽으세요.");
            }
            if (_sawSharedAddress)
            {
                _sb.AppendLine("- ICF(동일 코드 병합): 링커가 기계어가 같은 메서드를 한 주소로 합쳤습니다. Il2CppInterop의 Harmony 패치도 네이티브 주소에 디투어를 걸기 때문에, " +
                               "공유하는 다른 메서드가 호출될 때도 패치가 실행됩니다. __instance 타입이나 인자로 거르거나, 이 메서드를 부르는 쪽(호출자)을 패치하세요.");
            }
            if (_interopLookup == null)
            {
                _sb.AppendLine("- Interop 이름은 규칙으로 추정한 값입니다(Il2Cpp 네임스페이스 접두사, < > | → _). 같은 게임의 Il2CppInterop 덤프(reference_dir)가 있으면 실제로 있는지까지 확인합니다.");
            }
            return _sb.ToString().TrimEnd();
        }

        // ─── 이름 ───

        private string InteropTypeName(Type type)
        {
            string display = Mangle(DisplayName(type));
            string guess = GuessInteropNamespace(type) + "." + display;
            if (_interopLookup == null) return guess;
            var found = _interopLookup(display, null);
            if (found.Name != null) return found.Name + "  (레퍼런스에서 확인)";
            return guess + "  (레퍼런스에서 못 찾음: 추정)";
        }

        private string InteropMemberName(Type type, string member)
        {
            string display = Mangle(DisplayName(type));
            string mangled = member == ".ctor" ? member : Mangle(member);
            string guess = GuessInteropNamespace(type) + "." + display + "." + mangled;
            if (_interopLookup == null) return guess;
            var found = _interopLookup(display, mangled);
            if (found.Name != null) return found.Name + "  (레퍼런스에서 확인)";
            if (found.TypeResolved)
            {
                _sawMissingInInterop = true;
                return guess + "  ✗ Interop 어셈블리에 없음 → 이름으로 Harmony 패치 불가";
            }
            return guess + "  (레퍼런스에서 못 찾음: 추정)";
        }

        private static string GuessInteropNamespace(Type type)
        {
            Type outer = type;
            while (outer.IsNested && outer.DeclaringType != null) outer = outer.DeclaringType;
            string ns = outer.Namespace ?? "";
            if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("Unity.", StringComparison.Ordinal)) return ns;
            return ns.Length == 0 ? "Il2Cpp" : "Il2Cpp" + ns;
        }

        private static string Describe(AddressEntry e) =>
            e.Assembly.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ? e.Display : $"[{e.Assembly}] {e.Display}";

        // ─── 분류 ───

        private static bool Matches(string compilerName, string? methodFilter) =>
            methodFilter == null || NameEquals(ParentMethodName(compilerName), methodFilter);

        private static bool NameEquals(string? a, string b) =>
            a != null && (string.Equals(a, b, StringComparison.Ordinal) || string.Equals(Mangle(a), Mangle(b), StringComparison.OrdinalIgnoreCase));

        private static bool IsStateMachine(Type t)
        {
            try
            {
                return t.GetInterfaces().Any(i =>
                    i.FullName is "System.Runtime.CompilerServices.IAsyncStateMachine" or "System.Collections.IEnumerator");
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string StateMachineKind(Type t)
        {
            try
            {
                if (t.GetInterfaces().Any(i => i.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine")) return "async 상태 머신";
            }
            catch (Exception) { }
            return "코루틴/이터레이터 상태 머신";
        }

        // [IteratorStateMachine(typeof(<X>d__40))] / [AsyncStateMachine(...)]
        private static Type? FindStateMachine(MethodInfo m)
        {
            IList<CustomAttributeData> data;
            try { data = m.GetCustomAttributesData(); }
            catch (Exception) { return null; }

            foreach (var attr in data)
            {
                string name;
                try { name = attr.AttributeType.Name; }
                catch (Exception) { continue; }
                if (name is not ("IteratorStateMachineAttribute" or "AsyncStateMachineAttribute" or "AsyncIteratorStateMachineAttribute")) continue;
                if (attr.ConstructorArguments.Count == 1 && attr.ConstructorArguments[0].Value is Type sm) return sm;
            }
            return null;
        }

        private static List<MethodBase> SafeMethods(Type t)
        {
            try { return t.GetConstructors(All).Cast<MethodBase>().Concat(t.GetMethods(All)).ToList(); }
            catch (Exception) { return new List<MethodBase>(); }
        }

        private static List<FieldInfo> SafeFields(Type t)
        {
            try { return t.GetFields(All).Where(f => !f.IsLiteral && !f.IsStatic).ToList(); }
            catch (Exception) { return new List<FieldInfo>(); }
        }
    }

    // 더미 mscorlib은 공개 키 토큰이 비어 있어 참조 쪽(b77a5c561934e089)과 맞지 않는다. PathAssemblyResolver는 토큰까지 비교해 거부하므로 단순 이름으로만 찾는다.
    private sealed class SimpleNameResolver : MetadataAssemblyResolver
    {
        private readonly Dictionary<string, string> _paths;

        public SimpleNameResolver(Dictionary<string, string> paths) => _paths = paths;

        public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName) =>
            assemblyName.Name != null && _paths.TryGetValue(assemblyName.Name, out var path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
