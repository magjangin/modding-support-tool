using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ModdingSupportTool.Core;

/// <summary>
/// 앱에 내장된 덤퍼 코어(BuiltinSignatureDumper.cs)를 그대로 담은 독립 실행형 콘솔 프로젝트를 내보낸다.
/// 코어 소스는 빌드 시 임베디드 리소스로 들어가므로, 앱과 독립 프로젝트의 덤프 로직이 항상 같다.
/// </summary>
public static class SignatureDumperExporter
{
    private const string CoreResourceName = "Export/SignatureDumper.cs";

    public static readonly string[] ExportedFiles = { "SignatureDumper.csproj", "Program.cs", "SignatureDumper.cs", "README.md" };

    private const string CsprojContent = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net472;net8.0</TargetFrameworks>
    <RootNamespace>SignatureDumper</RootNamespace>
    <AssemblyName>SignatureDumper</AssemblyName>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <Compile Remove="**\*.cs" />
    <Compile Include="Program.cs" />
    <Compile Include="SignatureDumper.cs" />
  </ItemGroup>
</Project>

""";

    private const string ProgramContent = """
using System;
using System.IO;
using ModdingSupportTool.Core;

namespace SignatureDumper
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            string inputDir = args[0];
            string outputDir = args.Length > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "Decompiled");
            string[] targets = args.Length > 2
                ? args[2].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                : new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" };
            bool includeInterop = Array.IndexOf(args, "--keep-interop") >= 0;

            if (!Directory.Exists(inputDir))
            {
                Console.Error.WriteLine($"[오류] 어셈블리 폴더를 찾을 수 없습니다: {inputDir}");
                return 1;
            }

            Directory.CreateDirectory(outputDir);
            Console.WriteLine($"입력 폴더 : {inputDir}");
            Console.WriteLine($"출력 폴더 : {outputDir}");
            Console.WriteLine();

            int succeeded = 0;
            int failed = 0;

            foreach (string rawTarget in targets)
            {
                string targetName = rawTarget.Trim();
                string dllPath = Path.Combine(inputDir, targetName);
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"[건너뜀] 파일 없음: {dllPath}");
                    continue;
                }

                string moduleOutputDir = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(targetName));
                if (Directory.Exists(moduleOutputDir))
                {
                    try
                    {
                        Directory.Delete(moduleOutputDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[경고] 이전 출력 폴더를 지우지 못해 예전 파일이 남을 수 있습니다: {moduleOutputDir} ({ex.Message})");
                    }
                }

                try
                {
                    var options = new SignatureDumperOptions
                    {
                        TargetAssemblyPath = dllPath,
                        SearchDirectory = inputDir,
                        OutputDirectory = moduleOutputDir,
                        IncludeInteropMembers = includeInterop
                    };

                    new BuiltinSignatureDumper(options, Console.WriteLine).Dump();
                    succeeded++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [실패] {targetName}: {ex.Message}");
                    failed++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"결과: 성공 {succeeded}개, 실패 {failed}개");

            // 하나도 덤프하지 못했거나 실패가 있으면 종료 코드 3 (앱 CLI와 같은 규칙)
            if (succeeded == 0 || failed > 0)
            {
                Console.Error.WriteLine("[실패] 덤프된 어셈블리가 없거나 일부가 실패했습니다.");
                return 3;
            }

            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("사용법:");
            Console.WriteLine("  SignatureDumper <어셈블리폴더> [출력폴더] [대상1.dll,대상2.dll] [--keep-interop]");
            Console.WriteLine();
            Console.WriteLine("예:");
            Console.WriteLine("  SignatureDumper \"D:\\Games\\MyGame\\MelonLoader\\Il2CppAssemblies\"");
            Console.WriteLine("  SignatureDumper \"D:\\Games\\MyGame\\MelonLoader\\Il2CppAssemblies\" .\\Decompiled Assembly-CSharp.dll");
        }
    }
}

""";

    private const string ReadmeContent = """
# SignatureDumper

Modding Support Tool에 내장된 시그니처 덤퍼 코어를 그대로 담은 독립 실행형 콘솔 도구입니다.
MelonLoader가 생성한 Il2Cpp 더미 어셈블리(`MelonLoader/Il2CppAssemblies`)나 Mono 어셈블리를 읽어
네임스페이스별 C# 시그니처 스켈레톤(`<출력>/<모듈>/<Namespace>/<Type>.cs`)을 만듭니다.

## 빌드 및 실행

```bash
dotnet build
dotnet run --framework net8.0 -- "<어셈블리폴더>" "./Decompiled" "Assembly-CSharp.dll,Assembly-CSharp-firstpass.dll"
```

- `--keep-interop`: Il2CppInterop 내부 포인터 멤버(`NativeFieldInfoPtr_*` 등)도 출력합니다. (기본은 생략)

## 종료 코드

- `0`: 성공 (어셈블리를 하나 이상 덤프했고 실패가 없음)
- `1`: 인자 없음 또는 입력 폴더를 찾을 수 없음
- `3`: 덤프 실패 (대상 어셈블리를 하나도 덤프하지 못했거나 일부가 실패)

""";

    public static async Task ExportToDirectoryAsync(string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        var utf8 = new UTF8Encoding(false);

        await File.WriteAllTextAsync(Path.Combine(destinationDir, "SignatureDumper.csproj"), CsprojContent, utf8);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, "Program.cs"), ProgramContent, utf8);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, "SignatureDumper.cs"), ReadCoreSource(), utf8);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, "README.md"), ReadmeContent, utf8);
    }

    private static string ReadCoreSource()
    {
        using var stream = typeof(SignatureDumperExporter).Assembly.GetManifestResourceStream(CoreResourceName)
            ?? throw new FileNotFoundException($"임베디드 덤퍼 소스를 찾을 수 없습니다: {CoreResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
