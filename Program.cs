using System.Reflection;
using System.Text;
using TokenUsage.Config;
using TokenUsage.Errors;
using TokenUsage.Providers;

namespace TokenUsage;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
        }

        var parsed = ArgParser.Parse(args);

        if (parsed.SelfTest)
        {
            Console.WriteLine("GLM 解析自检");
            Console.WriteLine("============");
            var fails = Providers.Glm.GlmUsageTests.Run();
            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "全部通过 ✓" : $"失败 {fails} 项");
            return fails == 0 ? ExitCode.Success : ExitCode.ApiError;
        }

        if (parsed.ShowHelp)
        {
            Console.WriteLine(Usage.Get());
            return ExitCode.Success;
        }

        if (parsed.ShowVersion)
        {
            Console.WriteLine($"aiusage {Usage.GetVersion()}");
            return ExitCode.Success;
        }

        if (parsed.UnknownSubcommand is { } unknown)
        {
            await Console.Error.WriteLineAsync($"错误: 未知子命令: {unknown}");
            await Console.Error.WriteLineAsync($"可用: {string.Join(" / ", ProviderRegistry.KnownNames())} / all");
            return ExitCode.ConfigError;
        }

        var selectedProviders = ResolveProviders(parsed.Subcommand);
        if (selectedProviders.Count == 0)
        {
            await Console.Error.WriteLineAsync($"错误: 子命令 '{parsed.Subcommand}' 解析后无可用 provider");
            return ExitCode.ConfigError;
        }

        if (parsed.Subcommand == SubcommandKind.All && parsed.TokenOverride is not null)
        {
            await Console.Error.WriteLineAsync("错误: all 子命令不支持 --token 覆盖（请使用 minimax / deepseek 子命令）");
            return ExitCode.ConfigError;
        }

        if (selectedProviders.Count == 1 && parsed.TokenOverride is null && parsed.Subcommand != SubcommandKind.All)
        {
            var single = selectedProviders[0];
            if (!parsed.ConfigTokens.TryGetValue(single.Name, out _))
            {
                await Console.Error.WriteLineAsync($"错误: {single.Name} 未配置（请在 config.json 的 providers.{single.Name}.token 写入 token，或使用 {single.Name} --token <value>）");
                return ExitCode.ConfigError;
            }
        }

        if (parsed.Subcommand == SubcommandKind.All && parsed.ConfigTokens.Count == 0)
        {
            await Console.Error.WriteLineAsync("错误: 无任何 provider 配置（请在 config.json 的 providers.<name>.token 写入 token）");
            return ExitCode.ConfigError;
        }

        if (selectedProviders.Count > 1)
        {
            var missing = selectedProviders.Where(p => !parsed.ConfigTokens.ContainsKey(p.Name)).Select(p => p.Name).ToArray();
            if (missing.Length > 0)
            {
                await Console.Error.WriteLineAsync($"错误: all 模式下未配置的 provider: {string.Join(", ", missing)}（已自动跳过）");
            }
            selectedProviders = selectedProviders.Where(p => parsed.ConfigTokens.ContainsKey(p.Name)).ToList();
            if (selectedProviders.Count == 0)
            {
                await Console.Error.WriteLineAsync("错误: all 模式下无可用 provider（全部未配置）");
                return ExitCode.ConfigError;
            }
        }

        var tasks = selectedProviders.Select(p => RunOneAsync(p, parsed.TokenOverride, parsed.ConfigTokens)).ToArray();
        var results = await Task.WhenAll(tasks);

        var exitCode = ExitCode.Success;
        foreach (var r in results)
        {
            Console.WriteLine();
            Console.Write(r.Output);
            if (r.ExitCode > exitCode) exitCode = r.ExitCode;
        }

        return exitCode;
    }

    private static List<IProvider> ResolveProviders(SubcommandKind kind) => kind switch
    {
        SubcommandKind.All => ProviderRegistry.KnownNames().Select(n => ProviderRegistry.TryGet(n)!).ToList(),
        SubcommandKind.Minimax => new() { ProviderRegistry.TryGet("minimax")! },
        SubcommandKind.DeepSeek => new() { ProviderRegistry.TryGet("deepseek")! },
        SubcommandKind.Glm => new() { ProviderRegistry.TryGet("glm")! },
        _ => new()
    };

    private static async Task<RunResult> RunOneAsync(IProvider provider, string? tokenOverride, IReadOnlyDictionary<string, string> configTokens)
    {
        var token = tokenOverride ?? configTokens[provider.Name];
        try
        {
            var output = await provider.FetchAndRenderAsync(token, CancellationToken.None);
            return new RunResult { Output = output, ExitCode = ExitCode.Success };
        }
        catch (ProviderException ex) when (ex.Kind is ProviderExceptionKind.Unauthorized or ProviderExceptionKind.ApiError)
        {
            return new RunResult
            {
                Output = $"[{provider.Name}] 错误: {ex.Message}",
                ExitCode = ExitCode.ApiError
            };
        }
        catch (ProviderException ex) when (ex.Kind is ProviderExceptionKind.Timeout or ProviderExceptionKind.Network)
        {
            return new RunResult
            {
                Output = $"[{provider.Name}] 错误: {ex.Message}",
                ExitCode = ExitCode.NetworkError
            };
        }
    }

    private sealed class RunResult
    {
        public string Output { get; init; } = string.Empty;
        public int ExitCode { get; init; }
    }
}

internal enum SubcommandKind
{
    All,
    Minimax,
    DeepSeek,
    Glm,
}

internal sealed class ParsedArgs
{
    public SubcommandKind Subcommand { get; init; } = SubcommandKind.All;
    public string? UnknownSubcommand { get; init; }
    public string? TokenOverride { get; init; }
    public Dictionary<string, string> ConfigTokens { get; init; } = new(StringComparer.Ordinal);
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }
    public bool SelfTest { get; init; }
}

internal static class ArgParser
{
    public static ParsedArgs Parse(string[] args)
    {
        SubcommandKind subcommand = SubcommandKind.All;
        string? unknownSubcommand = null;
        string? tokenOverride = null;
        bool showHelp = false;
        bool showVersion = false;
        bool selfTest = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--help" || a == "-h")
            {
                showHelp = true;
                continue;
            }
            if (a == "--version" || a == "-v")
            {
                showVersion = true;
                continue;
            }
            if (a == "--self-test")
            {
                selfTest = true;
                continue;
            }
            if (a == "--token")
            {
                if (i + 1 >= args.Length)
                {
                    tokenOverride = string.Empty;
                }
                else
                {
                    tokenOverride = args[i + 1];
                    i++;
                }
                continue;
            }
            if (a.StartsWith("--token=", StringComparison.Ordinal))
            {
                tokenOverride = a.Substring("--token=".Length);
                continue;
            }
            switch (a)
            {
                case "minimax": subcommand = SubcommandKind.Minimax; break;
                case "deepseek": subcommand = SubcommandKind.DeepSeek; break;
                case "glm": subcommand = SubcommandKind.Glm; break;
                case "all": subcommand = SubcommandKind.All; break;
                default: unknownSubcommand ??= a; break;
            }
        }

        var exeDir = AppContext.BaseDirectory;
        var configResult = ConfigLoader.Load(exeDir);

        if (!showHelp && !showVersion && !selfTest && configResult.Error is not null)
        {
            Console.Error.WriteLine($"错误: {configResult.Error}");
            if (!string.IsNullOrEmpty(configResult.Detail))
            {
                Console.Error.WriteLine(configResult.Detail);
            }
            Environment.Exit(ExitCode.ConfigError);
        }

        return new ParsedArgs
        {
            Subcommand = subcommand,
            UnknownSubcommand = unknownSubcommand,
            TokenOverride = string.IsNullOrWhiteSpace(tokenOverride) ? null : tokenOverride.Trim(),
            ConfigTokens = configResult.Tokens,
            ShowHelp = showHelp,
            ShowVersion = showVersion,
            SelfTest = selfTest,
        };
    }
}

internal static class Usage
{
    public static string Get() =>
        """
        aiusage — 统一查询 AI 平台的 token plan / 余额

        用法:
          aiusage                        等价 all
          aiusage all                    并行查询所有已配置的 provider
          aiusage minimax                只查询 minimax
          aiusage deepseek               只查询 deepseek
          aiusage glm                    只查询 GLM Token Plan（智谱 BigModel）
          aiusage <provider> --token X   CLI 覆盖 token（all 模式不支持）

        选项:
          --token <value>     覆盖 config.json 中的 token（仅单 provider 子命令）
          --token=<value>     同上，等号形式
          --help, -h          显示本帮助
          --version, -v       显示版本
          --self-test         运行 GLM 解析逻辑离线自检

        配置文件: <exe-dir>/config.json
          {
            "providers": {
              "minimax":  { "token": "..." },
              "deepseek": { "token": "..." },
              "glm":      { "token": "..." }
            }
          }

        退出码:
          0  全部成功
          2  配置 / 参数错误
          3  API 错误（401 / 其他 4xx 5xx）
          4  网络异常 / 超时
        """;

    public static string GetVersion()
    {
        var asm = typeof(Program).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (info is not null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
        {
            return info.InformationalVersion;
        }
        var v = asm.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}