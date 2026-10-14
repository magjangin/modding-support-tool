using System;
using System.Collections.Generic;
using System.Linq;

namespace ModdingSupportTool.Cli;

/// <summary>위치 인자와 --옵션 값/플래그를 나눠 담는 작은 파서. 짧은 옵션(-i)은 별칭으로 긴 이름에 붙인다.</summary>
internal sealed class CliArgs
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-i"] = "input", ["-o"] = "output", ["-t"] = "targets", ["-r"] = "ref", ["-h"] = "help",
        ["-k"] = "kind", ["-n"] = "limit", ["-m"] = "member", ["-c"] = "client", ["-s"] = "scope"
    };

    // 값을 받지 않는 옵션
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "no-outline", "outline", "keep-interop", "full", "force", "dry-run", "no-mcp", "user", "project-scope", "cli", "auto", "overwrite"
    };

    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = new();

    public static CliArgs Parse(IEnumerable<string> args)
    {
        var result = new CliArgs();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string arg = list[i];
            string? name = null;
            if (Aliases.TryGetValue(arg, out var alias)) name = alias;
            else if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2) name = arg[2..];
            else if (arg == "-outline") name = "outline"; // 예전 표기 호환

            if (name == null)
            {
                result.Positionals.Add(arg);
                continue;
            }

            string? value = null;
            int eq = name.IndexOf('=');
            if (eq > 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (!Flags.Contains(name) && i + 1 < list.Count)
            {
                value = list[++i];
            }

            result._options[name] = value;
        }
        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(params string[] names)
    {
        foreach (var n in names)
        {
            if (_options.TryGetValue(n, out var v) && v != null) return v;
        }
        return null;
    }

    public int GetInt(string name, int fallback) => int.TryParse(Get(name), out var v) ? v : fallback;

    public string? Positional(int index) => index < Positionals.Count ? Positionals[index] : null;
}
