using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;

namespace ModdingSupportTool.Core;

public class TypeSignatureInfo
{
    public string Namespace { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? RawTypeName { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Kind { get; set; } = "class"; // class, struct, interface, enum, delegate
    public string AccessModifiers { get; set; } = "public";
    public string? BaseType { get; set; }
    public List<string> Interfaces { get; set; } = new();
    public List<string> EnumMembers { get; set; } = new();
    public List<string> Fields { get; set; } = new();
    public List<string> Properties { get; set; } = new();
    public List<string> Events { get; set; } = new();
    public List<string> Constructors { get; set; } = new();
    public List<string> Methods { get; set; } = new();
}

public class SignatureDumperOptions
{
    public string TargetAssemblyPath { get; set; } = string.Empty;
    public string? SearchDirectory { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
}

public class BuiltinSignatureDumper
{
    private readonly SignatureDumperOptions _options;
    private readonly Action<string> _log;

    public BuiltinSignatureDumper(SignatureDumperOptions options, Action<string> log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? (_ => { });
    }

    public void Dump(CancellationToken ct = default)
    {
        string assemblyPath = _options.TargetAssemblyPath;
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"어셈블리를 찾을 수 없습니다: {assemblyPath}");
        }

        _log($"[내장 SignatureDumper] 대상 어셈블리: {assemblyPath}");
        string searchDir = _options.SearchDirectory ?? Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
        string? melonLoaderRoot = Directory.GetParent(searchDir)?.FullName;

        // AssemblyLoadContext를 사용하여 어셈블리 잠금 방지 및 언로드 가능하게 로드
        var alc = new AssemblyLoadContext($"SignatureDumper_{Guid.NewGuid()}", isCollectible: true);

        alc.Resolving += (context, asmName) =>
        {
            try
            {
                var dllName = asmName.Name + ".dll";
                var candidatePaths = new List<string> { Path.Combine(searchDir, dllName) };

                if (melonLoaderRoot != null)
                {
                    candidatePaths.Add(Path.Combine(melonLoaderRoot, "net6", dllName));
                    candidatePaths.Add(Path.Combine(melonLoaderRoot, "net472", dllName));
                    candidatePaths.Add(Path.Combine(melonLoaderRoot, "net35", dllName));
                    candidatePaths.Add(Path.Combine(melonLoaderRoot, "Dependencies", "SupportModules", dllName));
                    candidatePaths.Add(Path.Combine(melonLoaderRoot, dllName));
                }

                foreach (var path in candidatePaths)
                {
                    if (File.Exists(path))
                    {
                        return context.LoadFromAssemblyPath(path);
                    }
                }
            }
            catch { }
            return null;
        };

        try
        {
            Assembly asm = alc.LoadFromAssemblyPath(assemblyPath);
            Type[] types;

            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
                _log($"[내장 SignatureDumper] 경고: 일부 참조 누락 상태에서 {types.Length}개 타입을 로드했습니다.");
                foreach (var loaderEx in ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct().Take(5))
                {
                    _log($"[내장 SignatureDumper]   누락 참조: {loaderEx}");
                }
            }

            ct.ThrowIfCancellationRequested();
            _log($"[내장 SignatureDumper] {types.Length}개 타입 정적 분석 시작...");

            var infos = new List<TypeSignatureInfo>();

            foreach (var type in types.OrderBy(t => t.Namespace).ThenBy(t => t.Name))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    infos.Add(AnalyzeType(type));
                }
                catch (Exception ex)
                {
                    _log($"[내장 SignatureDumper] 타입 분석 경고 '{type.FullName}': {ex.Message}");
                }
            }

            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_options.OutputDirectory);
            WriteIndividualFiles(_options.OutputDirectory, infos, ct);
            _log($"[내장 SignatureDumper] 완료: 총 {infos.Count}개 C# 스켈레톤 소스 파일 생성 -> {_options.OutputDirectory}");
        }
        finally
        {
            alc.Unload();
        }
    }

    private TypeSignatureInfo AnalyzeType(Type type)
    {
        var info = new TypeSignatureInfo
        {
            Namespace = type.Namespace ?? "<global>",
            Name = GetTypeName(type),
            RawTypeName = type.Name,
            FullName = type.FullName ?? type.Name
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
            string val = "";
            if (field.IsLiteral && !field.IsInitOnly)
            {
                try { val = $" = {field.GetRawConstantValue()}"; } catch { }
            }
            info.Fields.Add($"{mods} {fType} {field.Name}{val};");
        }

        foreach (var prop in type.GetProperties(flags))
        {
            string pType = GetTypeName(prop.PropertyType);
            var getMethod = prop.GetGetMethod(true);
            var setMethod = prop.GetSetMethod(true);

            string getStr = getMethod != null ? (getMethod.IsPublic ? "get; " : $"{GetMethodAccess(getMethod)} get; ") : "";
            string setStr = setMethod != null ? (setMethod.IsPublic ? "set; " : $"{GetMethodAccess(setMethod)} set; ") : "";
            string access = getMethod != null ? GetMethodAccess(getMethod) : (setMethod != null ? GetMethodAccess(setMethod) : "public");

            info.Properties.Add($"{access} {pType} {prop.Name} {{ {getStr}{setStr}}}");
        }

        foreach (var ev in type.GetEvents(flags))
        {
            string eType = GetTypeName(ev.EventHandlerType);
            info.Events.Add($"public event {eType} {ev.Name};");
        }

        foreach (var ctor in type.GetConstructors(flags))
        {
            string access = GetMethodAccess(ctor);
            string paramsStr = string.Join(", ", ctor.GetParameters().Select(FormatParameter));
            info.Constructors.Add($"{access} {info.Name}({paramsStr});");
        }

        foreach (var method in type.GetMethods(flags))
        {
            if (method.IsSpecialName) continue;
            string access = GetMethodAccess(method);
            string mods = GetMethodModifiers(method);
            string retType = GetTypeName(method.ReturnType);
            string paramsStr = string.Join(", ", method.GetParameters().Select(FormatParameter));
            string genArgs = method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(t => t.Name))}>" : "";

            info.Methods.Add($"{access}{mods} {retType} {method.Name}{genArgs}({paramsStr});");
        }

        return info;
    }

    private string FormatParameter(ParameterInfo p)
    {
        string prefix = "";
        if (p.IsOut) prefix = "out ";
        else if (p.ParameterType.IsByRef) prefix = "ref ";
        else if (p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Any()) prefix = "params ";

        string typeName = GetTypeName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType);
        string def = "";

        if (p.HasDefaultValue)
        {
            if (p.DefaultValue == null) def = " = null";
            else if (p.DefaultValue is string s) def = $" = \"{s}\"";
            else if (p.DefaultValue is bool b) def = $" = {(b ? "true" : "false")}";
            else def = $" = {p.DefaultValue}";
        }

        return $"{prefix}{typeName} {p.Name}{def}";
    }

    private string GetFieldModifiers(FieldInfo f)
    {
        string access = f.IsPublic ? "public" : (f.IsPrivate ? "private" : (f.IsFamily ? "protected" : "internal"));
        if (f.IsLiteral) return $"public const";
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
        if (m.IsAbstract) parts.Add("abstract");
        else if (m.IsVirtual && !m.IsFinal) parts.Add("virtual");
        return parts.Count > 0 ? " " + string.Join(" ", parts) : "";
    }

    private string GetTypeName(Type? t)
    {
        if (t == null) return "void";
        if (t == typeof(void)) return "void";
        if (t == typeof(int)) return "int";
        if (t == typeof(long)) return "long";
        if (t == typeof(short)) return "short";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(string)) return "string";
        if (t == typeof(object)) return "object";

        if (t.IsGenericType)
        {
            string genericName = t.Name.Split('`')[0];
            var typeArgs = string.Join(", ", t.GetGenericArguments().Select(GetTypeName));
            return $"{genericName}<{typeArgs}>";
        }

        return t.Name;
    }

    private void WriteIndividualFiles(string decompileDir, List<TypeSignatureInfo> infos, CancellationToken ct)
    {
        foreach (var info in infos)
        {
            ct.ThrowIfCancellationRequested();

            string nsPath = info.Namespace == "<global>" || string.IsNullOrEmpty(info.Namespace)
                ? "Global"
                : info.Namespace.Replace('.', Path.DirectorySeparatorChar);

            string targetDir = Path.Combine(decompileDir, nsPath);
            Directory.CreateDirectory(targetDir);

            string safeFileName = SanitizeFileName(info.RawTypeName ?? info.Name) + ".cs";
            string filePath = Path.Combine(targetDir, safeFileName);

            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            writer.WriteLine("using System;");
            writer.WriteLine("using System.Collections.Generic;");
            writer.WriteLine("using UnityEngine;");
            writer.WriteLine();

            string nsHeader = info.Namespace == "<global>" || string.IsNullOrEmpty(info.Namespace) ? "Global" : info.Namespace;
            writer.WriteLine($"namespace {nsHeader}");
            writer.WriteLine("{");

            string baseClause = "";
            var bases = new List<string>();
            if (!string.IsNullOrEmpty(info.BaseType)) bases.Add(info.BaseType);
            if (info.Interfaces.Count > 0) bases.AddRange(info.Interfaces);
            if (bases.Count > 0) baseClause = $" : {string.Join(", ", bases)}";

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
