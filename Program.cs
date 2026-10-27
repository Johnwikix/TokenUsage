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

        var options = CommandLine.Parse(args);

        if (options.ShowHelp)
        {
            Console.WriteLine(Usage.Get());
            return ExitCode.Success;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"aiusage {Usage.GetVersion()}");
            return ExitCode.Success;
        }

        if (options.SelfTest)
        {
            return SelfTests.RunAll() == 0 ? ExitCode.Success : ExitCode.ApiError;
        }

        if (options.Error is { } argumentError)
        {
            await Console.Error.WriteLineAsync($"错误: {argumentError}");
            await Console.Error.WriteLineAsync("使用 --help 查看用法");
            return ExitCode.ConfigError;
        }

        var selected = options.Selected;
        foreach (var name in selected)
        {
            if (ProviderRegistry.TryGet(name) is null)
            {
                await Console.Error.WriteLineAsync($"错误: 未知 provider: {name}");
                await Console.Error.WriteLineAsync($"可用: {string.Join(" / ", ProviderRegistry.KnownNames())} / all");
                return ExitCode.ConfigError;
            }
        }

        var explicitSelection = selected.Count > 0;
        var names = explicitSelection ? selected : ProviderRegistry.KnownNames();

        if (options.TokenOverride is not null && names.Count != 1)
        {
            await Console.Error.WriteLineAsync("错误: --token 只能与单个 provider 子命令一起使用");
            return ExitCode.ConfigError;
        }

        var loaded = ConfigLoader.Load(AppContext.BaseDirectory);
        var work = new List<WorkItem>();

        foreach (var name in names)
        {
            var provider = ProviderRegistry.TryGet(name);
            if (provider is null) continue;

            var configured = loaded.Providers.TryGetValue(name, out var config) ? config : null;

            var effective = new ProviderConfig
            {
                Token = options.TokenOverride ?? configured?.Token,
                Region = options.Region ?? configured?.Region,
                AccountId = configured?.AccountId,
                BaseUrl = options.BaseUrl ?? configured?.BaseUrl,
            };
            provider.ApplyAmbientCredentials(effective);

            if (!effective.IsConfigured)
            {
                if (explicitSelection)
                {
                    await Console.Error.WriteLineAsync(name == "codex"
                        ? "错误: codex 未登录：请先运行 codex login（工具会自动读取 %USERPROFILE%\\.codex\\auth.json），或在 config.json 的 providers.codex.token 指定 access token"
                        : loaded.Error is { } loadError
                            ? $"错误: {loadError}"
                            : $"错误: {name} 未配置（请在 config.json 的 providers.{name}.token 写入 token，或使用 {name} --token <value>）");
                    if (name != "codex" && loaded.Error is not null && !string.IsNullOrWhiteSpace(loaded.Detail))
                    {
                        await Console.Error.WriteLineAsync(loaded.Detail);
                    }
                    return ExitCode.ConfigError;
                }
                continue;
            }

            work.Add(new WorkItem(provider, effective));
        }

        if (work.Count == 0)
        {
            await Console.Error.WriteLineAsync(loaded.Error is { } loadError
                ? $"错误: {loadError}"
                : $"错误: 没有任何可查询的 provider（请在 config.json 的 providers.<name>.token 写入 token）");
            if (loaded.Error is null && !string.IsNullOrWhiteSpace(loaded.Detail))
            {
                await Console.Error.WriteLineAsync(loaded.Detail);
            }
            return ExitCode.ConfigError;
        }

        var results = await Task.WhenAll(work.Select(RunAsync));

        var exitCode = ExitCode.Success;
        foreach (var result in results)
        {
            Console.WriteLine();
            Console.Write(result.Output);
            if (result.ExitCode > exitCode) exitCode = result.ExitCode;
        }
        Console.WriteLine();

        return exitCode;
    }

    private static async Task<RunResult> RunAsync(WorkItem item)
    {
        try
        {
            var output = await item.Provider.FetchAndRenderAsync(item.Config, CancellationToken.None);
            return new RunResult(output, ExitCode.Success);
        }
        catch (ProviderException ex) when (ex.Kind is ProviderExceptionKind.Unauthorized or ProviderExceptionKind.ApiError)
        {
            return new RunResult(ErrorBlock(item.Provider, ex.Message), ExitCode.ApiError);
        }
        catch (ProviderException ex) when (ex.Kind is ProviderExceptionKind.Timeout or ProviderExceptionKind.Network)
        {
            return new RunResult(ErrorBlock(item.Provider, ex.Message), ExitCode.NetworkError);
        }
        catch (Exception ex)
        {
            return new RunResult(ErrorBlock(item.Provider, $"未处理的异常: {ex.Message}"), ExitCode.ApiError);
        }
    }

    private static string ErrorBlock(IProvider provider, string message) =>
        $"{provider.DisplayTitle}{Environment.NewLine}错误: {message}";

    private sealed record WorkItem(IProvider Provider, ProviderConfig Config);

    private sealed record RunResult(string Output, int ExitCode);
}

internal sealed class ParsedArgs
{
    public List<string> Selected { get; init; } = new();
    public string? TokenOverride { get; init; }
    public string? Region { get; init; }
    public string? BaseUrl { get; init; }
    public string? Error { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }
    public bool SelfTest { get; init; }
}

internal static class CommandLine
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kimi-coding"] = "kimi",
        ["kimi-code"] = "kimi",
        ["zhipu"] = "glm",
        ["bigmodel"] = "glm",
        ["zai"] = "glm",
        ["open-code"] = "opencode",
        ["opencode-go"] = "opencode",
    };

    public static ParsedArgs Parse(string[] args)
    {
        var selected = new List<string>();
        string? token = null;
        string? region = null;
        string? baseUrl = null;
        string? error = null;
        var showHelp = false;
        var showVersion = false;
        var selfTest = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (TryFlag(args, arg, "--token", ref i, out var value, ref error)) { token = value; continue; }
            if (TryFlag(args, arg, "--region", ref i, out value, ref error)) { region = value; continue; }
            if (TryFlag(args, arg, "--base-url", ref i, out value, ref error)) { baseUrl = value; continue; }

            switch (arg)
            {
                case "--help" or "-h" or "-?":
                    showHelp = true;
                    continue;
                case "--version" or "-v" or "-V":
                    showVersion = true;
                    continue;
                case "--self-test":
                    selfTest = true;
                    continue;
            }

            if (arg.StartsWith('-'))
            {
                error ??= $"未知选项: {arg}";
                continue;
            }

            if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                selected.Clear();
                continue;
            }

            selected.Add(Aliases.TryGetValue(arg, out var alias) ? alias : arg.ToLowerInvariant());
        }

        var unique = new List<string>();
        foreach (var name in selected)
        {
            if (!unique.Contains(name, StringComparer.OrdinalIgnoreCase)) unique.Add(name);
        }

        return new ParsedArgs
        {
            Selected = unique,
            TokenOverride = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            Region = string.IsNullOrWhiteSpace(region) ? null : region.Trim(),
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim(),
            Error = error,
            ShowHelp = showHelp,
            ShowVersion = showVersion,
            SelfTest = selfTest,
        };
    }

    private static bool TryFlag(string[] args, string arg, string name, ref int index, out string? value, ref string? error)
    {
        value = null;

        if (arg.StartsWith(name + "=", StringComparison.Ordinal))
        {
            value = arg[(name.Length + 1)..];
            return true;
        }

        if (arg != name) return false;

        if (index + 1 >= args.Length)
        {
            error ??= $"{name} 缺少取值";
            return true;
        }

        value = args[++index];
        return true;
    }
}

internal static class Usage
{
    public static string Get() =>
        """
        aiusage — 统一查询 AI 平台的 API 余额 / Coding Plan 套餐用量

        用法:
          aiusage                      等价 all：并行查询所有已配置的 provider
          aiusage all                  同上
          aiusage kimi                 只查询 Kimi For Coding（5h / 周 / 月度总额度）
          aiusage glm                  只查询 GLM Coding Plan（智谱 BigModel / z.ai）
          aiusage minimax              只查询 MiniMax Coding Plan
          aiusage opencode             只查询 OpenCode Go
          aiusage codex                只查询 Codex（ChatGPT 订阅）
          aiusage deepseek             只查询 DeepSeek 余额
          aiusage moonshot             只查询 Moonshot / Kimi 开放平台余额
          aiusage openrouter           只查询 OpenRouter 余额
          aiusage siliconflow          只查询 SiliconFlow 余额
          aiusage zenmux               只查询 ZenMux 余额
          aiusage kimi --token X       用命令行 token 覆盖配置（仅单个 provider）

        选项:
          --token <value>      覆盖 config.json 中的 token（仅单个 provider）
          --region <cn|global> 覆盖站点区域（cn 默认；global 走国际站）
          --base-url <url>     覆盖端点基址（代理 / 兼容网关）
          --help, -h           显示本帮助
          --version, -v        显示版本
          --self-test          运行解析逻辑离线自检

        配置文件: <exe-dir>/config.json
          {
            "providers": {
              "kimi":     { "token": "sk-kimi-...",  "region": "cn" },
              "glm":      { "token": "...",           "region": "cn" },
              "minimax":  { "token": "...",           "region": "cn" },
              "deepseek": { "token": "sk-..." },
              "codex":    { "token": "<oauth>",       "accountId": "..." }
            }
          }

          未配置 token 的 provider 在 all 模式下自动跳过。
          codex 无需 API key：未配置时自动读取 Codex CLI 的 ChatGPT 登录态
          （%USERPROFILE%\\.codex\\auth.json），只需先运行 codex login。

        退出码:
          0  全部成功
          2  配置 / 参数错误
          3  API 错误（401 / 其他 4xx 5xx）
          4  网络异常 / 超时
        """;

    public static string GetVersion()
    {
        var assembly = typeof(Program).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (info is not null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
        {
            return info.InformationalVersion;
        }
        var version = assembly.GetName().Version;
        return version is null ? "1.1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
