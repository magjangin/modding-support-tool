using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using ModdingSupportTool.Core.Detection;

namespace ModdingSupportTool.Core.Reference;

public sealed record MelonTemplate(
    string ModName,
    string ProjectFileName,
    string ProjectXml,
    string CoreFileName,
    string CoreSource,
    IReadOnlyList<string> Notes);

/// <summary>
/// 게임 폴더를 보고 실제로 존재하는 MelonLoader/게임 어셈블리 경로로 모드 프로젝트(.csproj + Core.cs)를 만든다.
/// </summary>
public static class MelonModTemplate
{
    public static MelonTemplate Create(GameInfo game, UnityBackend backend, string modName, string? author)
    {
        string name = ToIdentifier(modName);
        string authorName = string.IsNullOrWhiteSpace(author) ? "Unknown" : author.Trim();
        var notes = new List<string>();

        string mlDir = game.MelonLoaderDirectory ?? Path.Combine(game.Root, "MelonLoader");
        if (!Directory.Exists(mlDir))
        {
            notes.Add("MelonLoader가 설치되어 있지 않습니다. 먼저 MelonLoader를 설치하고 게임을 한 번 실행하세요. (아래 경로는 설치 후 기준)");
        }

        string targetFramework;
        string loaderSubdir;
        var gameReferences = new List<(string Include, string Comment)>();

        if (backend == UnityBackend.Il2Cpp)
        {
            targetFramework = "net6.0";
            loaderSubdir = "net6";
            string asmDir = @"$(MelonLoaderDir)\Il2CppAssemblies";
            gameReferences.Add((@"$(MelonLoaderDir)\net6\Il2CppInterop.Runtime.dll", "Il2CppInterop (Cast/TryCast, ClassInjector, DelegateSupport)"));
            gameReferences.Add((@"$(MelonLoaderDir)\net6\Il2CppInterop.Common.dll", null!));
            gameReferences.Add((asmDir + @"\Il2Cpp*.dll", "Il2CppSystem·Il2Cppmscorlib 등 Il2Cpp 런타임 래퍼"));
            gameReferences.Add((asmDir + @"\UnityEngine*.dll", "Unity 엔진 모듈"));
            gameReferences.Add((asmDir + @"\Unity.*.dll", "TextMeshPro, InputSystem 등 (있을 때만)"));
            gameReferences.Add((asmDir + @"\Assembly-CSharp*.dll", "게임 코드"));

            if (!Directory.Exists(Path.Combine(mlDir, "Il2CppAssemblies")))
            {
                notes.Add("MelonLoader/Il2CppAssemblies가 아직 없습니다. MelonLoader 설치 후 게임을 한 번 실행하면 생성됩니다.");
            }
        }
        else
        {
            // MelonLoader 0.7은 Mono 게임용으로 net472와 net35 빌드를 함께 배포한다. 있는 쪽을 쓴다.
            bool hasNet472 = File.Exists(Path.Combine(mlDir, "net472", "MelonLoader.dll"));
            loaderSubdir = hasNet472 ? "net472" : "net35";
            targetFramework = hasNet472 ? "net472" : "net35";
            notes.Add($"Mono: TargetFramework를 {targetFramework}로 잡았습니다. MelonLoader/Latest.log의 'Runtime Type'과 다르면 맞춰 주세요.");

            string managedDir = game.DataDirectory != null
                ? @"$(GameDir)\" + Path.GetFileName(game.DataDirectory) + @"\Managed"
                : @"$(GameDir)\<Game>_Data\Managed";
            gameReferences.Add((managedDir + @"\UnityEngine*.dll", "Unity 엔진 모듈"));
            gameReferences.Add((managedDir + @"\Unity.*.dll", "TextMeshPro, InputSystem 등 (있을 때만)"));
            gameReferences.Add((managedDir + @"\Assembly-CSharp*.dll", "게임 코드"));
        }

        if (backend == UnityBackend.Unknown)
        {
            notes.Add("백엔드를 판별하지 못해 Mono 기준으로 만들었습니다. Il2Cpp 게임이면 backend를 확인하세요.");
        }

        if (!File.Exists(Path.Combine(mlDir, loaderSubdir, "MelonLoader.dll")) && Directory.Exists(mlDir))
        {
            notes.Add($"MelonLoader/{loaderSubdir}/MelonLoader.dll이 없습니다. MelonLoader 0.6 이상 폴더 구조인지 확인하세요.");
        }

        var xml = new StringBuilder();
        xml.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        xml.AppendLine("  <PropertyGroup>");
        xml.AppendLine($"    <TargetFramework>{targetFramework}</TargetFramework>");
        xml.AppendLine($"    <AssemblyName>{name}</AssemblyName>");
        xml.AppendLine($"    <RootNamespace>{name}</RootNamespace>");
        xml.AppendLine("    <LangVersion>latest</LangVersion>");
        xml.AppendLine("    <Nullable>disable</Nullable>");
        xml.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
        xml.AppendLine("    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>");
        xml.AppendLine("    <GenerateDocumentationFile>false</GenerateDocumentationFile>");
        xml.AppendLine("    <!-- Modding Support Tool이 감지한 게임 경로 -->");
        xml.AppendLine($"    <GameDir>{SecurityElement.Escape(game.Root)}</GameDir>");
        xml.AppendLine(@"    <MelonLoaderDir>$(GameDir)\MelonLoader</MelonLoaderDir>");
        xml.AppendLine("    <!-- true로 바꾸거나 dotnet build -p:CopyToMods=true 로 빌드하면 DLL을 게임의 Mods 폴더로 복사합니다 -->");
        xml.AppendLine("    <CopyToMods>false</CopyToMods>");
        xml.AppendLine("  </PropertyGroup>");
        xml.AppendLine();
        xml.AppendLine("  <ItemGroup>");
        xml.AppendLine("    <!-- 게임에 이미 있는 DLL이므로 출력 폴더로 복사하지 않는다 (Private=false) -->");
        xml.AppendLine($@"    <Reference Include=""$(MelonLoaderDir)\{loaderSubdir}\MelonLoader.dll"" Private=""false"" />");
        xml.AppendLine($@"    <Reference Include=""$(MelonLoaderDir)\{loaderSubdir}\0Harmony.dll"" Private=""false"" />");
        foreach (var (include, comment) in gameReferences)
        {
            if (!string.IsNullOrEmpty(comment)) xml.AppendLine($"    <!-- {comment} -->");
            xml.AppendLine($@"    <Reference Include=""{include}"" Private=""false"" />");
        }
        xml.AppendLine("  </ItemGroup>");
        xml.AppendLine();
        xml.AppendLine("  <Target Name=\"CopyToMods\" AfterTargets=\"Build\" Condition=\"'$(CopyToMods)' == 'true'\">");
        xml.AppendLine(@"    <Copy SourceFiles=""$(TargetPath)"" DestinationFolder=""$(GameDir)\Mods"" />");
        xml.AppendLine("    <Message Importance=\"high\" Text=\"$(TargetFileName) -> $(GameDir)\\Mods\" />");
        xml.AppendLine("  </Target>");
        xml.AppendLine("</Project>");

        string company = Escape(game.Company ?? "UNKNOWN");
        string product = Escape(game.Product ?? game.Name);
        if (game.Company == null || game.Product == null)
        {
            notes.Add("*_Data/app.info를 읽지 못해 MelonGame 값이 추정치입니다. 게임 회사명/제품명과 다르면 모드가 로드되지 않으니 확인하세요.");
        }

        var core = new StringBuilder();
        core.AppendLine("using MelonLoader;");
        core.AppendLine();
        core.AppendLine($"[assembly: MelonInfo(typeof({name}.Core), \"{Escape(modName.Trim())}\", \"1.0.0\", \"{Escape(authorName)}\")]");
        core.AppendLine($"[assembly: MelonGame(\"{company}\", \"{product}\")]");
        core.AppendLine();
        core.AppendLine($"namespace {name}");
        core.AppendLine("{");
        core.AppendLine("    public sealed class Core : MelonMod");
        core.AppendLine("    {");
        core.AppendLine("        public override void OnInitializeMelon()");
        core.AppendLine("        {");
        core.AppendLine($"            LoggerInstance.Msg(\"{Escape(modName.Trim())} 로드됨\");");
        core.AppendLine("        }");
        core.AppendLine();
        core.AppendLine("        public override void OnSceneWasLoaded(int buildIndex, string sceneName)");
        core.AppendLine("        {");
        core.AppendLine("            LoggerInstance.Msg($\"씬 로드: {sceneName} ({buildIndex})\");");
        core.AppendLine("        }");
        core.AppendLine("    }");
        core.AppendLine("}");

        notes.Add("빌드: dotnet build -c Release  (게임 Mods 폴더로 복사하려면 -p:CopyToMods=true)");
        return new MelonTemplate(modName.Trim(), name + ".csproj", xml.ToString(), "Core.cs", core.ToString(), notes);
    }

    public static IReadOnlyList<string> WriteTo(MelonTemplate template, string directory, bool overwrite)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        var utf8 = new UTF8Encoding(false);

        foreach (var (fileName, content) in new[] { (template.ProjectFileName, template.ProjectXml), (template.CoreFileName, template.CoreSource) })
        {
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path) && !overwrite)
            {
                throw new IOException($"이미 파일이 있습니다(덮어쓰려면 overwrite): {path}");
            }
            File.WriteAllText(path, content, utf8);
            written.Add(path);
        }
        return written;
    }

    private static string ToIdentifier(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim()) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0) sb.Append("MyMod");
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
