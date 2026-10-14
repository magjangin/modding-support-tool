using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ModdingSupportTool.Core;

public sealed record OutlineStats(
    int TotalFiles,
    int ProcessedFiles,
    int OriginalLines,
    int OutlineLines,
    int FailedFiles,
    long OriginalChars = 0,
    long OutlineChars = 0)
{
    public double LineReductionPercent => Percent(OriginalLines, OutlineLines);

    // 토큰 수는 줄 수보다 글자 수에 비례하므로 에이전트 관점의 절감률은 이쪽이 더 정확하다.
    public double CharReductionPercent => Percent(OriginalChars, OutlineChars);

    private static double Percent(double original, double outline) =>
        original > 0 ? Math.Round((1.0 - outline / original) * 100, 1) : 0.0;
}

/// <summary>
/// Roslyn 구문 트리로 C# 소스에서 메서드·생성자·접근자 본문을 제거하고 시그니처, 필드, 어트리뷰트, 문서 주석만 남긴다.
/// 결과는 SignatureDumper 스켈레톤과 같은 "선언;" 형태라서 에이전트가 두 형식을 똑같이 읽을 수 있다.
/// </summary>
public static class RoslynOutliner
{
    public const string FileHeader = "// [AI outline] 메서드 본문 생략됨 · 전체 소스는 _Outline이 없는 폴더의 같은 경로";

    // 문서 주석을 구조로 파싱하지 않으면(DocumentationMode.None) 파싱이 빨라지고, 주석은 일반 trivia로 그대로 보존된다.
    internal static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithDocumentationMode(DocumentationMode.None);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>단일 C# 소스 문자열을 아웃라인 코드로 변환한다.</summary>
    public static string GenerateOutline(string sourceCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCode)) return sourceCode;

        var root = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions, cancellationToken: ct).GetRoot(ct);
        return new OutlineRewriter().Visit(root).ToFullString();
    }

    /// <summary>디렉터리의 모든 .cs 파일을 병렬로 아웃라인 변환해 같은 상대 경로로 저장한다.</summary>
    public static async Task<OutlineStats> ProcessDirectoryAsync(
        string sourceDir,
        string targetDir,
        Action<string>? log = null,
        CancellationToken ct = default,
        Action<string, string>? fileWritten = null)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"원본 디렉터리가 존재하지 않습니다: {sourceDir}");
        }

        Directory.CreateDirectory(targetDir);
        string[] csFiles = EnumerateSourceFiles(sourceDir).ToArray();

        int processed = 0, failed = 0, originalLines = 0, outlineLines = 0;
        long originalChars = 0, outlineChars = 0;

        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        await Parallel.ForEachAsync(csFiles, options, async (file, token) =>
        {
            string relPath = Path.GetRelativePath(sourceDir, file);
            try
            {
                string content = await File.ReadAllTextAsync(file, token);
                string outlined = FileHeader + Environment.NewLine + GenerateOutline(content, token);

                string destPath = Path.Combine(targetDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await File.WriteAllTextAsync(destPath, outlined, Utf8NoBom, token);
                fileWritten?.Invoke(destPath, outlined);

                Interlocked.Add(ref originalLines, CountLines(content));
                Interlocked.Add(ref outlineLines, CountLines(outlined));
                Interlocked.Add(ref originalChars, content.Length);
                Interlocked.Add(ref outlineChars, outlined.Length);
                Interlocked.Increment(ref processed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                log?.Invoke($"  [경고] 아웃라인 변환 실패: {relPath} ({ex.Message})");
            }
        });

        // .csproj가 원본에 있으면 함께 복사한다. (IDE/에디터 탐색 편의)
        foreach (var csproj in Directory.GetFiles(sourceDir, "*.csproj", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Copy(csproj, Path.Combine(targetDir, Path.GetFileName(csproj)), overwrite: true);
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [경고] .csproj 복사 실패: {Path.GetFileName(csproj)} ({ex.Message})");
            }
        }

        return new OutlineStats(csFiles.Length, processed, originalLines, outlineLines, failed, originalChars, outlineChars);
    }

    /// <summary>obj/bin 같은 빌드 산출물 폴더를 뺀 .cs 파일 목록.</summary>
    internal static System.Collections.Generic.IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(root, "*.cs", options).Where(f => !IsBuildArtifact(Path.GetRelativePath(root, f)));
    }

    private static bool IsBuildArtifact(string relativePath)
    {
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 1;
        foreach (char c in text)
        {
            if (c == '\n') count++;
        }
        return count;
    }

    private sealed class OutlineRewriter : CSharpSyntaxRewriter
    {
        // 본문은 버릴 것이므로 base.VisitXxx로 본문 안까지 내려가지 않는다. (로컬 함수·람다도 함께 사라진다)
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => StubBody(node);
        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) => StubBody(node);
        public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node) => StubBody(node);
        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) => StubBody(node);
        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) => StubBody(node);

        // public int Score => m_score + 10;  ->  public int Score { get; }
        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (node.ExpressionBody != null)
            {
                return ReplaceExpressionBodyWithGetter(node, node.ExpressionBody, node.SemicolonToken,
                    (n, list) => n.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(list));
            }

            return CollapseAccessorList((PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!);
        }

        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            if (node.ExpressionBody != null)
            {
                return ReplaceExpressionBodyWithGetter(node, node.ExpressionBody, node.SemicolonToken,
                    (n, list) => n.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(list));
            }

            return CollapseAccessorList((IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!);
        }

        public override SyntaxNode? VisitEventDeclaration(EventDeclarationSyntax node) =>
            CollapseAccessorList((EventDeclarationSyntax)base.VisitEventDeclaration(node)!);

        // get { return _x; } -> get;
        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            if (node.Body == null && node.ExpressionBody == null) return node;

            var trailing = node.GetLastToken().TrailingTrivia;
            var trimmed = TrimBeforeBody(node, node.Body?.OpenBraceToken ?? node.ExpressionBody!.ArrowToken);
            return trimmed
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(trailing));
        }

        // void Foo()\n{ ... }  ->  void Foo();   (원래 선언 끝의 줄바꿈은 세미콜론 뒤로 옮긴다)
        private static SyntaxNode StubBody(BaseMethodDeclarationSyntax node)
        {
            if (node.Body == null && node.ExpressionBody == null) return node;

            var trailing = node.GetLastToken().TrailingTrivia;
            var trimmed = TrimBeforeBody(node, node.Body?.OpenBraceToken ?? node.ExpressionBody!.ArrowToken);
            return trimmed
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(trailing));
        }

        // 본문 바로 앞 토큰(보통 닫는 괄호)의 줄바꿈을 지워서 세미콜론이 같은 줄에 붙게 한다.
        private static T TrimBeforeBody<T>(T node, SyntaxToken bodyStart) where T : SyntaxNode
        {
            var previous = bodyStart.GetPreviousToken();
            return node.ReplaceToken(previous, previous.WithTrailingTrivia());
        }

        private static T ReplaceExpressionBodyWithGetter<T>(
            T node,
            ArrowExpressionClauseSyntax expressionBody,
            SyntaxToken semicolon,
            Func<T, AccessorListSyntax, T> apply) where T : SyntaxNode
        {
            var trailing = semicolon.TrailingTrivia;
            var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithLeadingTrivia(SyntaxFactory.Space)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            var list = SingleLineAccessorList(SyntaxFactory.SingletonList(getter), trailing);
            return apply(TrimBeforeBody(node, expressionBody.ArrowToken), list);
        }

        // 본문이 모두 사라진 접근자 목록은 { get; private set; } 한 줄로 합친다.
        private static T CollapseAccessorList<T>(T node) where T : BasePropertyDeclarationSyntax
        {
            var list = node.AccessorList;
            if (list == null || list.Accessors.Any(a => a.Body != null || a.ExpressionBody != null)) return node;

            var accessors = SyntaxFactory.List(list.Accessors.Select(a =>
                a.WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia()));
            var collapsed = SingleLineAccessorList(accessors, list.CloseBraceToken.TrailingTrivia);

            var trimmed = TrimBeforeBody(node, list.OpenBraceToken);
            return (T)trimmed.WithAccessorList(collapsed);
        }

        private static AccessorListSyntax SingleLineAccessorList(SyntaxList<AccessorDeclarationSyntax> accessors, SyntaxTriviaList trailing) =>
            SyntaxFactory.AccessorList(
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(SyntaxFactory.Space),
                accessors,
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(trailing));
    }
}
