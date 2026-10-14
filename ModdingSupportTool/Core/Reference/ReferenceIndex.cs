using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ModdingSupportTool.Core.Reference;

public enum MemberKind { Method, Constructor, Property, Indexer, Field, Event, EnumMember, Operator }

public enum SearchScope { All, Types, Members, Methods, Properties, Fields }

public sealed class ReferenceType
{
    public required string Module { get; init; }
    public required string Namespace { get; init; }

    /// <summary>제네릭 인자를 뺀 단순 이름 (예: List)</summary>
    public required string Name { get; init; }

    /// <summary>중첩·제네릭을 포함한 이름 (예: Outer.Inner&lt;T&gt;). typeof(...)에 그대로 쓸 수 있다.</summary>
    public required string DisplayName { get; init; }

    public required string Kind { get; init; }
    public required string Declaration { get; init; }

    /// <summary>레퍼런스 루트 기준 상대 경로 (아웃라인과 전체 소스가 같은 상대 경로를 쓴다)</summary>
    public required string RelativePath { get; init; }

    public required int Line { get; init; }
    public bool IsPublic { get; init; }

    /// <summary>컴파일러가 만든 클로저·이터레이터 타입 (&lt;&gt;c, &lt;Init&gt;d__158, DisplayClass). Il2Cpp에선 __c, _Init_d__158처럼 바뀐다.</summary>
    public bool IsCompilerGenerated => Name.Contains('<') || Name.StartsWith("__c", StringComparison.Ordinal) ||
                                       Name.Contains("DisplayClass", StringComparison.Ordinal) || CompilerGeneratedPattern.IsMatch(Name);

    private static readonly Regex CompilerGeneratedPattern = new(@"_d__\d+$", RegexOptions.CultureInvariant);

    public string FullName => Namespace.Length > 0 ? Namespace + "." + DisplayName : DisplayName;
    public List<ReferenceMember> Members { get; } = new();
}

public sealed class ReferenceMember
{
    public required ReferenceType Owner { get; init; }
    public required string Name { get; init; }
    public required MemberKind Kind { get; init; }
    public required string Signature { get; init; }
    public required int Line { get; init; }
    public bool IsStatic { get; init; }
}

public sealed record SearchHit(ReferenceType Type, ReferenceMember? Member, int Score);

/// <summary>
/// 덤프 폴더의 C# 레퍼런스(스켈레톤/아웃라인)를 Roslyn으로 병렬 파싱해 타입·멤버 시그니처를 메모리에 색인한다.
/// 에이전트가 수천 개 파일을 직접 grep/read 하지 않고 필요한 선언만 찾게 해서 토큰을 아낀다.
/// </summary>
public sealed class ReferenceIndex
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, ReferenceIndex Index)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private ReferenceIndex(string root, string? outlineRoot, ReferenceManifest? manifest, List<ReferenceType> types, TimeSpan buildTime)
    {
        Root = root;
        OutlineRoot = outlineRoot;
        Manifest = manifest;
        Types = types;
        MemberCount = types.Sum(t => t.Members.Count);
        BuildTime = buildTime;
    }

    public string Root { get; }
    public string? OutlineRoot { get; }
    public ReferenceManifest? Manifest { get; }
    public IReadOnlyList<ReferenceType> Types { get; }
    public int MemberCount { get; }
    public TimeSpan BuildTime { get; }

    public string GetFullSourcePath(ReferenceType type) => Path.Combine(Root, type.RelativePath);

    /// <summary>읽기용 경로: 아웃라인이 있으면 아웃라인(토큰 절약), 없으면 원본.</summary>
    public string GetOutlinePath(ReferenceType type)
    {
        if (OutlineRoot != null)
        {
            string outline = Path.Combine(OutlineRoot, type.RelativePath);
            if (File.Exists(outline)) return outline;
        }
        return GetFullSourcePath(type);
    }

    public bool HasMethodBodies(ReferenceType type) =>
        Manifest?.Modules.FirstOrDefault(m => m.Name.Equals(type.Module, StringComparison.OrdinalIgnoreCase))?.HasMethodBodies
        ?? OutlineRoot != null;

    /// <summary>
    /// 레퍼런스 루트를 정규화한다. *_Outline 폴더나 모듈 폴더(Decompiled/Assembly-CSharp)를 넣어도 덤프 루트를 찾는다.
    /// </summary>
    public static string NormalizeRoot(string directory)
    {
        string dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (dir.EndsWith("_Outline", StringComparison.OrdinalIgnoreCase))
        {
            string full = dir[..^"_Outline".Length];
            if (Directory.Exists(full)) dir = full;
        }

        // 모듈 폴더를 넣었으면 reference.json이 있는 부모(덤프 루트)를 쓴다.
        string? parent = Path.GetDirectoryName(dir);
        if (!File.Exists(Path.Combine(dir, ReferenceManifest.FileName)) &&
            parent != null && File.Exists(Path.Combine(parent, ReferenceManifest.FileName)))
        {
            dir = parent;
        }

        return dir;
    }

    public static void Invalidate(string directory) => Cache.TryRemove(NormalizeRoot(directory), out _);

    /// <summary>캐시된 인덱스를 돌려주거나(덤프가 바뀌지 않았으면) 새로 만든다.</summary>
    /// <param name="preloaded">덤프가 방금 쓴 파일 내용(전체 경로 → 내용). 있으면 디스크에서 다시 읽지 않는다.</param>
    public static ReferenceIndex Load(string directory, CancellationToken ct = default, IReadOnlyDictionary<string, string>? preloaded = null)
    {
        string root = NormalizeRoot(directory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"레퍼런스 폴더가 없습니다: {root}");
        }

        DateTime stamp = GetStamp(root);
        if (Cache.TryGetValue(root, out var cached) && cached.Stamp == stamp)
        {
            return cached.Index;
        }

        var index = Build(root, ct, preloaded);
        Cache[root] = (stamp, index);
        return index;
    }

    private static DateTime GetStamp(string root)
    {
        string manifest = Path.Combine(root, ReferenceManifest.FileName);
        if (File.Exists(manifest)) return File.GetLastWriteTimeUtc(manifest);

        DateTime stamp = Directory.GetLastWriteTimeUtc(root);
        foreach (var sub in Directory.EnumerateDirectories(root))
        {
            var t = Directory.GetLastWriteTimeUtc(sub);
            if (t > stamp) stamp = t;
        }
        return stamp;
    }

    private static ReferenceIndex Build(string root, CancellationToken ct, IReadOnlyDictionary<string, string>? preloaded)
    {
        var watch = Stopwatch.StartNew();
        string outlineCandidate = root + "_Outline";
        string? outlineRoot = Directory.Exists(outlineCandidate) ? outlineCandidate : null;

        var files = RoslynOutliner.EnumerateSourceFiles(root).ToArray();
        var perFile = new List<ReferenceType>[files.Length];

        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.For(0, files.Length, options, i =>
        {
            string relPath = Path.GetRelativePath(root, files[i]);
            string parsePath = files[i];
            if (outlineRoot != null)
            {
                // 같은 선언을 훨씬 작은 아웃라인에서 읽으면 파싱이 빠르다.
                string outline = Path.Combine(outlineRoot, relPath);
                if (File.Exists(outline)) parsePath = outline;
            }

            try
            {
                // 방금 쓴 파일을 처음 열 때는 백신 검사 등으로 수 초가 걸리므로, 덤프가 넘겨준 내용이 있으면 그것을 쓴다.
                string text = preloaded != null && preloaded.TryGetValue(parsePath, out var cached) ? cached : File.ReadAllText(parsePath);
                var tree = CSharpSyntaxTree.ParseText(text, RoslynOutliner.ParseOptions, cancellationToken: ct);
                var walker = new IndexWalker(tree, ModuleOf(relPath), relPath, nodeMap: null);
                walker.Visit(tree.GetRoot(ct));
                perFile[i] = walker.Types;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                perFile[i] = new List<ReferenceType>(); // 읽을 수 없는 파일은 건너뛴다.
            }
        });

        var types = perFile.SelectMany(t => t)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ThenBy(t => t.Module, StringComparer.Ordinal)
            .ToList();

        return new ReferenceIndex(root, outlineRoot, ReferenceManifest.Load(root), types, watch.Elapsed);
    }

    private static string ModuleOf(string relPath)
    {
        int sep = relPath.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return sep > 0 ? relPath[..sep] : "(root)";
    }

    /// <summary>이름으로 타입을 찾는다. 전체 이름 → 표시 이름 → 단순 이름 순, 대소문자 일치를 먼저 본다.</summary>
    public IReadOnlyList<ReferenceType> FindTypes(string name)
    {
        string query = name.Trim();
        string stripped = StripGenericArguments(query);

        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            var hits = Types.Where(t => t.FullName.Equals(query, comparison) || StripGenericArguments(t.FullName).Equals(stripped, comparison)).ToList();
            if (hits.Count > 0) return hits;

            hits = Types.Where(t => t.DisplayName.Equals(query, comparison) || StripGenericArguments(t.DisplayName).Equals(stripped, comparison)).ToList();
            if (hits.Count > 0) return hits;

            hits = Types.Where(t => t.Name.Equals(stripped, comparison)).ToList();
            if (hits.Count > 0) return hits;
        }

        return Array.Empty<ReferenceType>();
    }

    public IReadOnlyList<SearchHit> Search(string query, SearchScope scope = SearchScope.All, string? namespaceFilter = null, int limit = 30)
    {
        var matcher = QueryMatcher.Create(query);
        if (matcher == null) return Array.Empty<SearchHit>();

        var hits = new List<SearchHit>();
        foreach (var type in Types)
        {
            if (namespaceFilter != null && !type.Namespace.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase)) continue;

            if (scope is SearchScope.All or SearchScope.Types)
            {
                int score = Math.Max(matcher.Score(type.DisplayName), Math.Max(matcher.Score(type.FullName), matcher.Score(type.Name)));
                if (score > 0) hits.Add(new SearchHit(type, null, score + 5 - Penalty(type))); // 같은 점수면 타입을 먼저 보여준다
            }

            if (scope == SearchScope.Types) continue;

            foreach (var member in type.Members)
            {
                if (!InScope(member.Kind, scope)) continue;
                int score = Math.Max(matcher.Score(member.Name), matcher.Score(type.DisplayName + "." + member.Name));
                if (score > 0) hits.Add(new SearchHit(type, member, score - Penalty(type)));
            }
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Member == null ? 0 : 1 + (int)h.Member.Kind)
            .ThenBy(h => h.Type.FullName.Length)
            .ThenBy(h => h.Type.FullName, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    // 컴파일러 생성 타입은 모드에서 거의 쓰지 않으므로 결과 뒤로 보낸다. (지우지는 않는다: 코루틴 MoveNext 패치 등에 필요할 수 있음)
    private static int Penalty(ReferenceType type) => type.IsCompilerGenerated ? 40 : 0;

    private static bool InScope(MemberKind kind, SearchScope scope) => scope switch
    {
        SearchScope.All or SearchScope.Members => true,
        SearchScope.Methods => kind is MemberKind.Method or MemberKind.Constructor or MemberKind.Operator,
        SearchScope.Properties => kind is MemberKind.Property or MemberKind.Indexer,
        SearchScope.Fields => kind is MemberKind.Field or MemberKind.EnumMember or MemberKind.Event,
        _ => false
    };

    internal static string StripGenericArguments(string name)
    {
        if (name.IndexOf('<') < 0) return name;
        var sb = new StringBuilder(name.Length);
        int depth = 0;
        foreach (char c in name)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>파일을 다시 파싱해 타입의 구문 노드를 찾는다. (조회·Harmony 패치 생성용)</summary>
    public static (SyntaxNode Node, CompilationUnitSyntax Root)? LocateTypeNode(ReferenceType type, string filePath, CancellationToken ct = default)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath), RoslynOutliner.ParseOptions, cancellationToken: ct);
        var map = new Dictionary<ReferenceType, SyntaxNode>();
        var walker = new IndexWalker(tree, type.Module, type.RelativePath, map);
        var root = (CompilationUnitSyntax)tree.GetRoot(ct);
        walker.Visit(root);

        var match = map.FirstOrDefault(kv => kv.Key.FullName == type.FullName && kv.Key.Kind == type.Kind).Value
                    ?? map.FirstOrDefault(kv => kv.Key.FullName == type.FullName).Value;
        return match != null ? (match, root) : null;
    }

    private sealed class QueryMatcher
    {
        private readonly string _query;
        private readonly string[] _terms;
        private readonly Regex? _wildcard;

        private QueryMatcher(string query)
        {
            _query = query;
            _terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (query.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                string pattern = "^" + Regex.Escape(query).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                _wildcard = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        public static QueryMatcher? Create(string? query) =>
            string.IsNullOrWhiteSpace(query) ? null : new QueryMatcher(query.Trim());

        public int Score(string candidate)
        {
            if (_wildcard != null) return _wildcard.IsMatch(candidate) ? 60 : 0;

            if (_terms.Length > 1)
            {
                return _terms.All(t => candidate.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 40 : 0;
            }

            if (candidate.Equals(_query, StringComparison.Ordinal)) return 100;
            if (candidate.Equals(_query, StringComparison.OrdinalIgnoreCase)) return 98;
            if (candidate.EndsWith("." + _query, StringComparison.OrdinalIgnoreCase)) return 90;
            if (candidate.StartsWith(_query, StringComparison.OrdinalIgnoreCase)) return 70;

            int idx = candidate.IndexOf(_query, StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? 50 - Math.Min(20, idx) : 0;
        }
    }

    /// <summary>타입·멤버 선언만 훑는다. 메서드 본문 안으로는 내려가지 않는다.</summary>
    private sealed class IndexWalker : CSharpSyntaxWalker
    {
        private readonly SyntaxTree _tree;
        private readonly string _module;
        private readonly string _relativePath;
        private readonly Dictionary<ReferenceType, SyntaxNode>? _nodeMap;
        private readonly List<string> _namespaces = new();
        private readonly Stack<ReferenceType> _typeStack = new();

        public IndexWalker(SyntaxTree tree, string module, string relativePath, Dictionary<ReferenceType, SyntaxNode>? nodeMap)
        {
            _tree = tree;
            _module = module;
            _relativePath = relativePath;
            _nodeMap = nodeMap;
        }

        public List<ReferenceType> Types { get; } = new();

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            _namespaces.Add(node.Name.ToString());
            base.VisitNamespaceDeclaration(node);
            _namespaces.RemoveAt(_namespaces.Count - 1);
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            _namespaces.Add(node.Name.ToString());
            base.VisitFileScopedNamespaceDeclaration(node);
            _namespaces.RemoveAt(_namespaces.Count - 1);
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) => VisitType(node, "class", node.TypeParameterList, () => base.VisitClassDeclaration(node));
        public override void VisitStructDeclaration(StructDeclarationSyntax node) => VisitType(node, "struct", node.TypeParameterList, () => base.VisitStructDeclaration(node));
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitType(node, "interface", node.TypeParameterList, () => base.VisitInterfaceDeclaration(node));
        public override void VisitEnumDeclaration(EnumDeclarationSyntax node) => VisitType(node, "enum", null, () => base.VisitEnumDeclaration(node));

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            string kind = node.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record";
            VisitType(node, kind, node.TypeParameterList, () => base.VisitRecordDeclaration(node));
        }

        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            string declaration = Squash($"{Mods(node.Modifiers)}delegate {node.ReturnType} {node.Identifier.Text}{node.TypeParameterList}{node.ParameterList}");
            AddType(node, node.Identifier.Text, node.TypeParameterList, "delegate", declaration, node.Modifiers);
        }

        private void VisitType(BaseTypeDeclarationSyntax node, string kind, TypeParameterListSyntax? typeParameters, Action visitChildren)
        {
            string keyword = kind;
            string declaration = Squash($"{Mods(node.Modifiers)}{keyword} {node.Identifier.Text}{typeParameters}{(node.BaseList != null ? " " + node.BaseList : "")}");
            var type = AddType(node, node.Identifier.Text, typeParameters, kind, declaration, node.Modifiers);

            _typeStack.Push(type);
            visitChildren();
            _typeStack.Pop();
        }

        private ReferenceType AddType(SyntaxNode node, string name, TypeParameterListSyntax? typeParameters, string kind, string declaration, SyntaxTokenList modifiers)
        {
            string own = name + (typeParameters != null ? Squash(typeParameters.ToString()) : "");
            string? declaring = _typeStack.Count > 0 ? _typeStack.Peek().DisplayName : FindNestedInComment(node);

            var type = new ReferenceType
            {
                Module = _module,
                Namespace = string.Join(".", _namespaces),
                Name = name,
                DisplayName = declaring != null ? declaring + "." + own : own,
                Kind = kind,
                Declaration = declaration,
                RelativePath = _relativePath,
                Line = LineOf(node),
                IsPublic = modifiers.Any(SyntaxKind.PublicKeyword)
            };

            Types.Add(type);
            _nodeMap?.Add(type, node);
            return type;
        }

        // SignatureDumper 스켈레톤은 중첩 타입을 별도 파일에 쓰고 "// Nested in: Outer" 주석을 남긴다.
        private static string? FindNestedInComment(SyntaxNode node)
        {
            foreach (var trivia in node.GetLeadingTrivia())
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)) continue;
                string text = trivia.ToString();
                const string marker = "// Nested in:";
                if (text.StartsWith(marker, StringComparison.Ordinal)) return text[marker.Length..].Trim();
            }
            return null;
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            string name = node.ExplicitInterfaceSpecifier != null ? $"{node.ExplicitInterfaceSpecifier.Name}.{node.Identifier.Text}" : node.Identifier.Text;
            AddMember(node, name, MemberKind.Method, node.Modifiers,
                $"{Mods(node.Modifiers)}{node.ReturnType} {name}{node.TypeParameterList}{node.ParameterList}");
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            AddMember(node, node.Identifier.Text, MemberKind.Constructor, node.Modifiers,
                $"{Mods(node.Modifiers)}{node.Identifier.Text}{node.ParameterList}");

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
            AddMember(node, node.Identifier.Text, MemberKind.Property, node.Modifiers,
                $"{Mods(node.Modifiers)}{node.Type} {node.Identifier.Text} {Accessors(node.AccessorList, node.ExpressionBody)}");

        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node) =>
            AddMember(node, "this", MemberKind.Indexer, node.Modifiers,
                $"{Mods(node.Modifiers)}{node.Type} this{node.ParameterList} {Accessors(node.AccessorList, node.ExpressionBody)}");

        public override void VisitEventDeclaration(EventDeclarationSyntax node) =>
            AddMember(node, node.Identifier.Text, MemberKind.Event, node.Modifiers,
                $"{Mods(node.Modifiers)}event {node.Type} {node.Identifier.Text}");

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            foreach (var variable in node.Declaration.Variables)
            {
                AddMember(node, variable.Identifier.Text, MemberKind.Event, node.Modifiers,
                    $"{Mods(node.Modifiers)}event {node.Declaration.Type} {variable.Identifier.Text}");
            }
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            bool isConst = node.Modifiers.Any(SyntaxKind.ConstKeyword);
            foreach (var variable in node.Declaration.Variables)
            {
                string value = isConst && variable.Initializer != null ? " = " + Truncate(Squash(variable.Initializer.Value.ToString()), 80) : "";
                AddMember(node, variable.Identifier.Text, MemberKind.Field, node.Modifiers,
                    $"{Mods(node.Modifiers)}{node.Declaration.Type} {variable.Identifier.Text}{value}");
            }
        }

        public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) =>
            AddMember(node, node.Identifier.Text, MemberKind.EnumMember, default,
                node.Identifier.Text + (node.EqualsValue != null ? " " + Squash(node.EqualsValue.ToString()) : ""));

        public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node) =>
            AddMember(node, "operator " + node.OperatorToken.Text, MemberKind.Operator, node.Modifiers,
                $"{Mods(node.Modifiers)}{node.ReturnType} operator {node.OperatorToken.Text}{node.ParameterList}");

        public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) =>
            AddMember(node, $"{node.ImplicitOrExplicitKeyword.Text} operator", MemberKind.Operator, node.Modifiers,
                $"{Mods(node.Modifiers)}{node.ImplicitOrExplicitKeyword.Text} operator {node.Type}{node.ParameterList}");

        public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node)
        {
            // 소멸자는 모드에서 쓰지 않으므로 색인하지 않는다.
        }

        private void AddMember(SyntaxNode node, string name, MemberKind kind, SyntaxTokenList modifiers, string signature)
        {
            if (_typeStack.Count == 0) return;
            var owner = _typeStack.Peek();
            owner.Members.Add(new ReferenceMember
            {
                Owner = owner,
                Name = name,
                Kind = kind,
                Signature = Squash(signature),
                Line = LineOf(node),
                IsStatic = modifiers.Any(SyntaxKind.StaticKeyword) || modifiers.Any(SyntaxKind.ConstKeyword)
            });
        }

        private int LineOf(SyntaxNode node) => _tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

        private static string Accessors(AccessorListSyntax? list, ArrowExpressionClauseSyntax? expressionBody)
        {
            if (list == null) return expressionBody != null ? "{ get; }" : "";
            return "{ " + string.Join(" ", list.Accessors.Select(a => Mods(a.Modifiers) + a.Keyword.Text + ";")) + " }";
        }
    }

    private static string Mods(SyntaxTokenList modifiers) =>
        modifiers.Count > 0 ? string.Join(" ", modifiers.Select(m => m.Text)) + " " : "";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    // 여러 줄 파라미터·주석 등을 한 줄로 압축한다.
    internal static string Squash(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace && c != ')' && c != ',' && c != ']' && sb[^1] != '(' && sb[^1] != '[')
            {
                sb.Append(' ');
            }
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
