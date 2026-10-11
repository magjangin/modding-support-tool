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
    int FailedFiles)
{
    public double LineReductionPercent => OriginalLines > 0 
        ? Math.Round((1.0 - (double)OutlineLines / OriginalLines) * 100, 1) 
        : 0.0;
}

/// <summary>
/// Roslyn AST 구문 분석을 활용하여 C# 소스 코드에서 메서드/프로퍼티의 세부 구현 본문만을 비우고
/// 시그니처, 필드, 어트리뷰트, 문서 주석만을 남겨 AI 프롬프트 토큰을 극대화하여 절약하는 아웃라이너.
/// </summary>
public static class RoslynOutliner
{
    private static readonly BlockSyntax MethodStubBlock = SyntaxFactory.Block()
        .WithOpenBraceToken(SyntaxFactory.Token(SyntaxFactory.TriviaList(SyntaxFactory.Space), SyntaxKind.OpenBraceToken, SyntaxFactory.TriviaList()))
        .WithCloseBraceToken(SyntaxFactory.Token(
            SyntaxFactory.TriviaList(SyntaxFactory.Comment("/* ... */ ")),
            SyntaxKind.CloseBraceToken,
            SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed)));

    /// <summary>
    /// 단일 C# 소스 문자열을 아웃라인 스켈레톤 코드로 변환합니다.
    /// </summary>
    public static string GenerateOutline(string sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode)) return sourceCode;

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = syntaxTree.GetRoot();

        var rewriter = new OutlineRewriter();
        var strippedRoot = rewriter.Visit(root);

        return strippedRoot.ToFullString();
    }

    /// <summary>
    /// 디렉터리 내의 모든 .cs 파일에 대해 아웃라인을 생성하여 출력 디렉터리에 동일한 구조로 저장합니다.
    /// </summary>
    public static async Task<OutlineStats> ProcessDirectoryAsync(
        string sourceDir,
        string targetDir,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"원본 디렉터리가 존재하지 않습니다: {sourceDir}");
        }

        Directory.CreateDirectory(targetDir);

        var csFiles = Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
            // obj, bin 등 임시 폴더는 제외
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();

        int processed = 0;
        int failed = 0;
        int originalLines = 0;
        int outlineLines = 0;

        foreach (var file in csFiles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                string relPath = Path.GetRelativePath(sourceDir, file);
                string destPath = Path.Combine(targetDir, relPath);

                string? destDir = Path.GetDirectoryName(destPath);
                if (destDir != null && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                string content = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
                int fileOriginalLines = CountLines(content);
                originalLines += fileOriginalLines;

                string outlined = GenerateOutline(content);
                int fileOutlineLines = CountLines(outlined);
                outlineLines += fileOutlineLines;

                await File.WriteAllTextAsync(destPath, outlined, Encoding.UTF8, ct);
                processed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                log?.Invoke($"  [경고] 아웃라인 변환 실패: {Path.GetFileName(file)} ({ex.Message})");
            }
        }

        // .csproj 파일이 원본에 있다면 함께 복사 (IDE/에디터 탐색 편의 제공)
        try
        {
            var csprojFiles = Directory.GetFiles(sourceDir, "*.csproj", SearchOption.TopDirectoryOnly);
            foreach (var csproj in csprojFiles)
            {
                string destCsproj = Path.Combine(targetDir, Path.GetFileName(csproj));
                File.Copy(csproj, destCsproj, overwrite: true);
            }
        }
        catch
        {
            // .csproj 복사 실패는 치명적이지 않음
        }

        return new OutlineStats(csFiles.Length, processed, originalLines, outlineLines, failed);
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') count++;
        }
        return count;
    }

    private sealed class OutlineRewriter : CSharpSyntaxRewriter
    {
        // 1. 일반 메서드 (MethodDeclarationSyntax)
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

            if (visited.Body != null)
            {
                return visited.WithBody(MethodStubBlock);
            }

            if (visited.ExpressionBody != null)
            {
                return visited
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(MethodStubBlock);
            }

            return visited;
        }

        // 2. 생성자 (ConstructorDeclarationSyntax)
        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;

            if (visited.Body != null)
            {
                return visited.WithBody(MethodStubBlock);
            }

            if (visited.ExpressionBody != null)
            {
                return visited
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(MethodStubBlock);
            }

            return visited;
        }

        // 3. 소멸자 (DestructorDeclarationSyntax)
        public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node)
        {
            var visited = (DestructorDeclarationSyntax)base.VisitDestructorDeclaration(node)!;

            if (visited.Body != null)
            {
                return visited.WithBody(MethodStubBlock);
            }

            if (visited.ExpressionBody != null)
            {
                return visited
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(MethodStubBlock);
            }

            return visited;
        }

        // 4. 연산자 오버로딩 (OperatorDeclarationSyntax, ConversionOperatorDeclarationSyntax)
        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            var visited = (OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!;

            if (visited.Body != null)
            {
                return visited.WithBody(MethodStubBlock);
            }

            if (visited.ExpressionBody != null)
            {
                return visited
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(MethodStubBlock);
            }

            return visited;
        }

        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            var visited = (ConversionOperatorDeclarationSyntax)base.VisitConversionOperatorDeclaration(node)!;

            if (visited.Body != null)
            {
                return visited.WithBody(MethodStubBlock);
            }

            if (visited.ExpressionBody != null)
            {
                return visited
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(MethodStubBlock);
            }

            return visited;
        }

        // 5. 프로퍼티 (PropertyDeclarationSyntax)
        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;

            // Expression body 프로퍼티: public int Score => m_score + 10;
            // -> public int Score { get; }
            if (visited.ExpressionBody != null)
            {
                var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                var accessorList = SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter));

                return visited
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithAccessorList(accessorList);
            }

            return visited;
        }

        // 6. 프로퍼티 / 인덱서 / 이벤트 접근자 (AccessorDeclarationSyntax)
        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            var visited = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;

            // get { return _x; } -> get;
            // set { _x = value; } -> set;
            if (visited.Body != null)
            {
                return visited
                    .WithBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            }

            if (visited.ExpressionBody != null)
            {
                return visited
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            }

            return visited;
        }

        // 7. 인덱서 (IndexerDeclarationSyntax)
        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            var visited = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;

            if (visited.ExpressionBody != null)
            {
                var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                var accessorList = SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter));

                return visited
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithAccessorList(accessorList);
            }

            return visited;
        }
    }
}
