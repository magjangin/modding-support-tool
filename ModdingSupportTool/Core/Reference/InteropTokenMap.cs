using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ModdingSupportTool.Core.Reference;

/// <summary>
/// Il2CppInterop이 만든 어셈블리(MelonLoader/Il2CppAssemblies)에서 "원래 메서드 토큰 → Interop 메서드 이름"을 읽는다.
/// Il2CppInterop은 Cpp2IL 더미 어셈블리를 입력으로 쓰고, 각 타입의 정적 생성자에서
/// <c>NativeMethodInfoPtr_X = IL2CPP.GetIl2CppMethodByToken(classPtr, 원래토큰)</c>을 만든다.
/// 이름에 '|'가 들어간 로컬 함수 등은 "난독화된 이름"으로 취급되어 Method_Internal_Static_Void_Int32_PDM_0처럼
/// 시그니처 기반 이름으로 바뀌므로(_PDM·번호는 생성 시점 분석에 달려 있어 규칙으로 추정할 수 없음), 토큰으로 정확히 찾는다.
/// </summary>
public sealed class InteropTokenMap
{
    private const string MethodInfoFieldPrefix = "NativeMethodInfoPtr_";
    private static readonly Dictionary<ushort, OperandType> OperandTypes = BuildOperandTable();

    private readonly ImmutableArray<byte> _image;
    private readonly Dictionary<int, (TypeDefinitionHandle Type, string Field)> _byToken = new();
    private readonly Dictionary<TypeDefinitionHandle, Dictionary<string, string>> _methodNames = new();

    public string AssemblyPath { get; }

    private InteropTokenMap(string path, ImmutableArray<byte> image)
    {
        AssemblyPath = path;
        _image = image;
    }

    /// <summary>더미 폴더가 게임의 MelonLoader cpp2il_out이면, 같은 게임의 Il2CppAssemblies에서 같은 이름의 어셈블리를 연다.</summary>
    public static InteropTokenMap? TryOpen(string interopDirectory, string assemblyName)
    {
        // Il2CppInterop은 Assembly-CSharp 말고는 파일 이름 앞에 Il2Cpp를 붙인다 (Alchemy.dll → Il2CppAlchemy.dll).
        string path = Path.Combine(interopDirectory, assemblyName + ".dll");
        if (!File.Exists(path)) path = Path.Combine(interopDirectory, "Il2Cpp" + assemblyName + ".dll");
        if (!File.Exists(path)) return null;
        try
        {
            var map = new InteropTokenMap(path, ImmutableArray.Create(File.ReadAllBytes(path)));
            map.IndexStaticConstructors();
            return map._byToken.Count > 0 ? map : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>원래(Cpp2IL 더미) 메서드 토큰으로 Interop 타입 전체 이름과 메서드 이름을 찾는다. 이 어셈블리에 없으면 null.</summary>
    public (string TypeName, string MethodName)? Resolve(int originalToken)
    {
        if (!_byToken.TryGetValue(originalToken, out var entry)) return null;
        using var pe = new PEReader(_image);
        var md = pe.GetMetadataReader();

        if (!_methodNames.TryGetValue(entry.Type, out var names))
        {
            _methodNames[entry.Type] = names = MapFieldsToMethods(pe, md, entry.Type);
        }

        // 필드 이름에서 접두사를 뗀 것이 메서드 이름과 같은 경우가 많지만(Method_Internal_..._0), 일반 메서드는
        // NativeMethodInfoPtr_Start_Private_Void_0 ↔ Start처럼 다르므로 그 필드를 읽는 메서드를 찾는다.
        string method = names.TryGetValue(entry.Field, out var found) ? found : entry.Field[MethodInfoFieldPrefix.Length..];
        return (TypeName(md, entry.Type), method);
    }

    private void IndexStaticConstructors()
    {
        using var pe = new PEReader(_image);
        var md = pe.GetMetadataReader();
        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (!md.StringComparer.Equals(method.Name, ".cctor") || method.RelativeVirtualAddress == 0) continue;

                int? lastInt = null;
                int? pendingToken = null;
                foreach (var (op, operand) in Instructions(pe, method))
                {
                    if (op == OpCodes.Ldc_I4.Value) lastInt = operand;
                    else if (op == OpCodes.Call.Value)
                    {
                        pendingToken = MemberName(md, operand) == "GetIl2CppMethodByToken" ? lastInt : null;
                    }
                    else if (op == OpCodes.Stsfld.Value)
                    {
                        string? field = MemberName(md, operand);
                        if (pendingToken is int token && field != null && field.StartsWith(MethodInfoFieldPrefix, StringComparison.Ordinal))
                        {
                            _byToken[token] = (typeHandle, field);
                        }
                        pendingToken = null;
                    }
                }
            }
        }
    }

    private static Dictionary<string, string> MapFieldsToMethods(PEReader pe, MetadataReader md, TypeDefinitionHandle typeHandle)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var methodHandle in md.GetTypeDefinition(typeHandle).GetMethods())
        {
            var method = md.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            string name = md.GetString(method.Name);
            if (name == ".cctor") continue;

            foreach (var (op, operand) in Instructions(pe, method))
            {
                if (op != OpCodes.Ldsfld.Value) continue;
                string? field = MemberName(md, operand);
                if (field != null && field.StartsWith(MethodInfoFieldPrefix, StringComparison.Ordinal)) result.TryAdd(field, name);
            }
        }
        return result;
    }

    private static string? MemberName(MetadataReader md, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            return handle.Kind switch
            {
                HandleKind.FieldDefinition => md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
                HandleKind.MethodDefinition => md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
                HandleKind.MemberReference => md.GetString(md.GetMemberReference((MemberReferenceHandle)handle).Name),
                HandleKind.MethodSpecification => MemberName(md, MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)handle).Method)),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var parts = new List<string>();
        string ns = "";
        for (var current = handle; !current.IsNil;)
        {
            var type = md.GetTypeDefinition(current);
            string name = md.GetString(type.Name);
            int tick = name.IndexOf('`');
            parts.Add(tick >= 0 ? name[..tick] : name);
            ns = md.GetString(type.Namespace);
            current = type.GetDeclaringType();
        }
        parts.Reverse();
        return (ns.Length > 0 ? ns + "." : "") + string.Join(".", parts);
    }

    // IL을 명령 단위로 읽어 (opcode, 4바이트 정수 피연산자)를 돌려준다. 정수가 아닌 피연산자는 0.
    private static IEnumerable<(ushort Op, int Operand)> Instructions(PEReader pe, MethodDefinition method)
    {
        BlobReader il;
        try { il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader(); }
        catch (Exception) { yield break; }

        while (il.RemainingBytes > 0)
        {
            ushort op = il.ReadByte();
            if (op == 0xFE)
            {
                if (il.RemainingBytes == 0) yield break;
                op = (ushort)(0xFE00 | il.ReadByte());
            }
            if (!OperandTypes.TryGetValue(op, out var operandType)) yield break;

            int operand = 0;
            switch (operandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: il.ReadByte(); break;
                case OperandType.InlineVar: il.ReadUInt16(); break;
                case OperandType.InlineI8:
                case OperandType.InlineR: il.ReadInt64(); break;
                case OperandType.InlineSwitch:
                    int count = il.ReadInt32();
                    for (int i = 0; i < count; i++) il.ReadInt32();
                    break;
                default: operand = il.ReadInt32(); break; // 4바이트: 정수·분기·토큰·float32
            }
            yield return (op, operand);
        }
    }

    private static Dictionary<ushort, OperandType> BuildOperandTable()
    {
        var table = new Dictionary<ushort, OperandType>();
        foreach (var field in typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode code) table[(ushort)code.Value] = code.OperandType;
        }
        return table;
    }
}
