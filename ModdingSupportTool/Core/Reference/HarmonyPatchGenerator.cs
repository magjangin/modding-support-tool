using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModdingSupportTool.Core.Detection;

namespace ModdingSupportTool.Core.Reference;

[Flags]
public enum PatchKind { Prefix = 1, Postfix = 2, Both = Prefix | Postfix }

public sealed record PatchGenerationResult(string? Code, string? Error, IReadOnlyList<string> Notes);

/// <summary>
/// 레퍼런스에 있는 실제 선언(파라미터 이름·타입·ref/out·static·오버로드)을 읽어 MelonLoader 모드용 HarmonyX 패치 코드를 만든다.
/// </summary>
public static class HarmonyPatchGenerator
{
    private sealed record Parameter(string Name, string Type, string RefKind);

    private sealed record Target(
        string Kind,                 // method, ctor, getter, setter
        string MemberName,
        string ReturnType,           // void 포함
        IReadOnlyList<Parameter> Parameters,
        bool IsStatic,
        bool IsPublic,
        bool IsGeneric,
        string Signature);

    public static PatchGenerationResult Generate(ReferenceIndex index, ReferenceType type, string memberName, PatchKind kind, UnityBackend backend)
    {
        var located = ReferenceIndex.LocateTypeNode(type, index.GetOutlinePath(type));
        if (located is not { } found || found.Node is not TypeDeclarationSyntax typeNode)
        {
            return new PatchGenerationResult(null, $"'{type.FullName}'의 선언을 파일에서 찾지 못했습니다: {index.GetOutlinePath(type)}", Array.Empty<string>());
        }

        var targets = CollectTargets(typeNode, type, memberName.Trim());
        if (targets.Count == 0)
        {
            var similar = type.Members
                .Where(m => m.Name.Contains(memberName, StringComparison.OrdinalIgnoreCase))
                .Select(m => "  " + m.Signature)
                .Distinct()
                .Take(15)
                .ToList();
            string hint = similar.Count > 0 ? "\n비슷한 멤버:\n" + string.Join("\n", similar) : "";
            return new PatchGenerationResult(null, $"'{type.DisplayName}'에 '{memberName}' 메서드/프로퍼티/생성자가 없습니다.{hint}", Array.Empty<string>());
        }

        bool il2Cpp = backend == UnityBackend.Il2Cpp || type.Namespace.StartsWith("Il2Cpp", StringComparison.Ordinal);
        var usings = CollectUsings(found.Root, type);
        var notes = new List<string>();
        var sb = new StringBuilder();

        sb.AppendLine("using System;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using HarmonyLib;");
        foreach (var u in usings) sb.Append("using ").Append(u).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("namespace YourMod");
        sb.AppendLine("{");

        bool overloaded = targets.Count(t => t.Kind == targets[0].Kind) > 1;
        for (int i = 0; i < targets.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            string suffix = targets.Count > 1 ? "_" + (i + 1) : "";
            RenderPatchClass(sb, type, targets[i], kind, overloaded, il2Cpp, suffix, notes);
        }

        sb.AppendLine("}");

        notes.Add("MelonLoader는 모드 어셈블리의 [HarmonyPatch] 클래스를 자동으로 적용합니다. (직접 적용하려면 [assembly: HarmonyDontPatchAll] 후 HarmonyInstance.PatchAll)");
        if (il2Cpp)
        {
            notes.Add("Il2Cpp: Il2CppInterop가 패치를 네이티브 메서드에 연결합니다. 아주 짧아 인라인된 메서드는 패치가 호출되지 않을 수 있습니다.");
            notes.Add("Il2Cpp: 필드는 프로퍼티로 노출되므로 ___필드 인자 주입 대신 __instance.필드명 을 쓰세요. 다른 타입으로 바꿀 땐 .Cast<T>()/.TryCast<T>().");
        }
        else
        {
            notes.Add("Mono: private 필드는 ___필드명(언더바 3개) 인자로 주입받거나 AccessTools.Field / Traverse로 접근할 수 있습니다.");
        }

        return new PatchGenerationResult(sb.ToString(), null, notes.Distinct().ToList());
    }

    private static List<Target> CollectTargets(TypeDeclarationSyntax typeNode, ReferenceType type, string memberName)
    {
        var targets = new List<Target>();
        bool wantsCtor = memberName is ".ctor" or "ctor" || memberName == type.Name;

        foreach (var member in typeNode.Members)
        {
            switch (member)
            {
                case ConstructorDeclarationSyntax ctor when wantsCtor && !ctor.Modifiers.Any(SyntaxKind.StaticKeyword):
                    targets.Add(new Target("ctor", ".ctor", "void", ParametersOf(ctor.ParameterList), false,
                        IsPublic(ctor.Modifiers), false, Squash(ctor.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default).WithAttributeLists(default))));
                    break;

                case MethodDeclarationSyntax method when !wantsCtor && method.Identifier.Text == memberName:
                    targets.Add(new Target("method", memberName, method.ReturnType.ToString(), ParametersOf(method.ParameterList),
                        method.Modifiers.Any(SyntaxKind.StaticKeyword), IsPublic(method.Modifiers), method.TypeParameterList != null,
                        Squash(method.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default).WithAttributeLists(default))));
                    break;

                case PropertyDeclarationSyntax property when !wantsCtor && property.Identifier.Text == memberName:
                    bool isStatic = property.Modifiers.Any(SyntaxKind.StaticKeyword);
                    var accessors = property.AccessorList?.Accessors.Select(a => a.Keyword.Kind()).ToList() ?? new List<SyntaxKind> { SyntaxKind.GetKeyword };
                    string signature = ReferenceIndex.Squash($"{property.Modifiers} {property.Type} {property.Identifier}");
                    string propertyType = property.Type.ToString();
                    if (accessors.Contains(SyntaxKind.GetKeyword))
                    {
                        targets.Add(new Target("getter", memberName, propertyType, Array.Empty<Parameter>(), isStatic, IsPublic(property.Modifiers), false, signature + " { get; }"));
                    }
                    if (accessors.Contains(SyntaxKind.SetKeyword))
                    {
                        targets.Add(new Target("setter", memberName, "void", new[] { new Parameter("value", propertyType, "") }, isStatic, IsPublic(property.Modifiers), false, signature + " { set; }"));
                    }
                    break;
            }
        }

        return targets;
    }

    private static void RenderPatchClass(StringBuilder sb, ReferenceType type, Target target, PatchKind kind, bool overloaded, bool il2Cpp, string suffix, List<string> notes)
    {
        string typeRef = TypeOfName(type);
        bool typeAccessible = il2Cpp || type.IsPublic;
        bool memberAccessible = il2Cpp || target.IsPublic;
        string className = Sanitize($"{type.Name}_{(target.Kind == "ctor" ? "Ctor" : target.MemberName)}{(target.Kind is "getter" or "setter" ? "_" + Capitalize(target.Kind) : "")}_Patch{suffix}");
        string instanceType = typeAccessible ? typeRef : "object";

        sb.Append("    // 대상: ").Append(type.FullName).Append(" → ").AppendLine(target.Signature);
        if (type.DisplayName.Contains('<'))
        {
            sb.AppendLine("    // ⚠ 제네릭 타입의 메서드입니다. 실제로 쓰이는 닫힌 타입(예: Foo<int>)으로 typeof를 바꾸세요.");
        }
        if (target.IsGeneric)
        {
            sb.AppendLine("    // ⚠ 제네릭 메서드는 [HarmonyPatch]로 바로 패치할 수 없습니다. TargetMethod()에서 MakeGenericMethod(typeof(구체타입))로 닫힌 메서드를 반환하세요.");
            notes.Add("제네릭 메서드는 TargetMethod() + MakeGenericMethod 로 닫힌 메서드를 지정해야 합니다.");
        }

        string argumentTypes = target.Parameters.Count > 0
            ? "new Type[] { " + string.Join(", ", target.Parameters.Select(p => $"typeof({StripNullable(p.Type)})")) + " }"
            : "Type.EmptyTypes";
        bool hasByRef = target.Parameters.Any(p => p.RefKind.Length > 0);
        string argumentVariations = hasByRef
            ? "new ArgumentType[] { " + string.Join(", ", target.Parameters.Select(p => p.RefKind switch { "out" => "ArgumentType.Out", "" => "ArgumentType.Normal", _ => "ArgumentType.Ref" })) + " }"
            : "";

        if (typeAccessible && !target.IsGeneric)
        {
            string memberRef = memberAccessible ? $"nameof({typeRef}.{target.MemberName})" : $"\"{target.MemberName}\"";
            string methodType = target.Kind switch { "getter" => "MethodType.Getter", "setter" => "MethodType.Setter", "ctor" => "MethodType.Constructor", _ => "" };
            var args = new List<string> { $"typeof({typeRef})" };
            if (target.Kind != "ctor") args.Add(memberRef);
            if (methodType.Length > 0) args.Add(methodType);
            // 오버로드가 여럿이면 인자 타입으로 고르고, 생성자는 파라미터가 있으면 항상 지정한다. (없으면 기본 생성자를 찾는다)
            bool needsArgumentTypes = target.Kind == "ctor" ? target.Parameters.Count > 0 : target.Kind == "method" && overloaded;
            if (needsArgumentTypes)
            {
                args.Add(argumentTypes);
                if (hasByRef) args.Add(argumentVariations);
            }
            sb.Append("    [HarmonyPatch(").Append(string.Join(", ", args)).AppendLine(")]");
            sb.Append("    internal static class ").AppendLine(className);
            sb.AppendLine("    {");
        }
        else
        {
            // 접근할 수 없는 타입이거나 제네릭이면 TargetMethod로 대상을 직접 고른다.
            string typeLookup = typeAccessible ? $"typeof({typeRef})" : $"AccessTools.TypeByName(\"{ReflectionName(type)}\")";
            string lookup = target.Kind switch
            {
                "ctor" => $"AccessTools.Constructor({typeLookup}, {argumentTypes})",
                "getter" => $"AccessTools.PropertyGetter({typeLookup}, \"{target.MemberName}\")",
                "setter" => $"AccessTools.PropertySetter({typeLookup}, \"{target.MemberName}\")",
                _ => target.IsGeneric
                    ? $"AccessTools.Method({typeLookup}, \"{target.MemberName}\").MakeGenericMethod(typeof(object) /* TODO: 구체 타입 */)"
                    : $"AccessTools.Method({typeLookup}, \"{target.MemberName}\", {argumentTypes})"
            };
            sb.AppendLine("    [HarmonyPatch]");
            sb.Append("    internal static class ").AppendLine(className);
            sb.AppendLine("    {");
            sb.Append("        private static MethodBase TargetMethod() => ").Append(lookup).AppendLine(";");
            sb.AppendLine();
            if (!typeAccessible)
            {
                notes.Add($"'{type.FullName}'는 public이 아니라서 __instance를 object로 받습니다. 멤버 접근은 Traverse.Create(__instance) 또는 AccessTools를 쓰세요.");
            }
        }

        var injected = new List<string>();
        if (!target.IsStatic) injected.Add($"{instanceType} __instance");
        injected.AddRange(target.Parameters.Select(p => (p.RefKind.Length > 0 ? "ref " : "") + $"{StripParams(p.Type)} {p.Name}"));

        bool returnsValue = target.ReturnType != "void" && target.Kind is "method" or "getter";
        bool wantPrefix = kind.HasFlag(PatchKind.Prefix) && target.Kind != "ctor";
        bool wantPostfix = kind.HasFlag(PatchKind.Postfix) || target.Kind == "ctor";

        if (wantPrefix)
        {
            var prefixArgs = new List<string>(injected);
            if (returnsValue) prefixArgs.Add($"ref {target.ReturnType} __result");
            sb.AppendLine("        // 원본 실행 전에 호출됩니다. false를 반환하면 원본을 건너뜁니다" + (returnsValue ? " (__result에 값을 넣어 반환값 대체)." : "."));
            sb.Append("        private static bool Prefix(").Append(string.Join(", ", prefixArgs)).AppendLine(")");
            sb.AppendLine("        {");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
        }

        if (wantPostfix)
        {
            if (wantPrefix) sb.AppendLine();
            var postfixArgs = new List<string>(injected);
            if (returnsValue) postfixArgs.Add($"ref {target.ReturnType} __result");
            sb.AppendLine("        // 원본 실행 후에 호출됩니다." + (returnsValue ? " __result로 반환값을 읽거나 바꿀 수 있습니다." : ""));
            sb.Append("        private static void Postfix(").Append(string.Join(", ", postfixArgs)).AppendLine(")");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
    }

    private static List<string> CollectUsings(CompilationUnitSyntax root, ReferenceType type)
    {
        var usings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var u in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<UsingDirectiveSyntax>())
        {
            if (u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name != null) usings.Add(u.Name.ToString());
        }
        if (type.Namespace.Length > 0) usings.Add(type.Namespace);
        usings.Remove("System");
        usings.Remove("System.Reflection");
        usings.Remove("HarmonyLib");
        return usings.ToList();
    }

    private static IReadOnlyList<Parameter> ParametersOf(ParameterListSyntax list) =>
        list.Parameters.Select(p =>
        {
            string refKind = p.Modifiers.Any(SyntaxKind.OutKeyword) ? "out"
                : p.Modifiers.Any(SyntaxKind.RefKeyword) ? "ref"
                : p.Modifiers.Any(SyntaxKind.InKeyword) ? "in"
                : "";
            bool isParams = p.Modifiers.Any(SyntaxKind.ParamsKeyword);
            string typeText = p.Type?.ToString() ?? "object";
            return new Parameter(p.Identifier.Text, isParams ? "params " + typeText : typeText, refKind);
        }).ToList();

    private static bool IsPublic(SyntaxTokenList modifiers) => modifiers.Any(SyntaxKind.PublicKeyword);

    // typeof()에 쓸 이름. 제네릭 타입은 열린 형태(Foo<>)로 바꾼다.
    private static string TypeOfName(ReferenceType type)
    {
        if (!type.DisplayName.Contains('<')) return type.DisplayName;
        var sb = new StringBuilder();
        int depth = 0;
        foreach (char c in type.DisplayName)
        {
            if (c == '<') { depth++; if (depth == 1) sb.Append('<'); continue; }
            if (c == '>') { depth--; if (depth == 0) sb.Append('>'); continue; }
            if (depth == 0) sb.Append(c);
            else if (depth == 1 && c == ',') sb.Append(',');
        }
        return sb.ToString();
    }

    // AccessTools.TypeByName용 리플렉션 이름: Ns.Outer+Inner`1
    private static string ReflectionName(ReferenceType type)
    {
        var parts = type.DisplayName.Split('.').Select(p =>
        {
            int lt = p.IndexOf('<');
            if (lt < 0) return p;
            int arity = p.Count(c => c == ',') + 1;
            return p[..lt] + "`" + arity;
        });
        string nested = string.Join("+", parts);
        return type.Namespace.Length > 0 ? type.Namespace + "." + nested : nested;
    }

    private static string StripParams(string type) => type.StartsWith("params ", StringComparison.Ordinal) ? type["params ".Length..] : type;

    private static readonly HashSet<string> ValueTypeKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort"
    };

    // typeof(string?)는 컴파일 오류지만 typeof(int?)는 Nullable<int>라서, 값 형식 키워드만 ?를 남긴다.
    private static string StripNullable(string type)
    {
        string t = StripParams(type);
        return t.EndsWith('?') && !ValueTypeKeywords.Contains(t[..^1]) ? t[..^1] : t;
    }

    private static string Squash(SyntaxNode node) => ReferenceIndex.Squash(node.ToString());

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }
}
