using System.Text.Json;
using System.Text.Json.Nodes;
using Quicklight.Core;
using Quicklight.Core.Calc;
using Quicklight.Core.Everything;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;

namespace Quicklight.Mcp;

/// <summary>
/// Model Context Protocol server (JSON-RPC 2.0, one message per line on stdio) exposing Quicklight search to AI clients.
/// </summary>
public sealed class McpServer(SearchEngine engine, EverythingClient? everything, McpServer.Options options)
{
    public sealed record Options(bool AllowOpen = true, Action<string>? Opener = null, Action<string>? Revealer = null);

    public const string LatestProtocol = "2025-06-18";
    static readonly string[] SupportedProtocols = [LatestProtocol, "2025-03-26", "2024-11-05"];

    static readonly string[] KindNames = Enum.GetNames<ResultKind>().Select(ToSnake).ToArray();

    readonly Task _warmUp = engine.WarmUpAsync();

    /// <summary>Handles one JSON-RPC message. Returns null for notifications.</summary>
    public async Task<JsonObject?> HandleAsync(JsonObject msg, CancellationToken ct = default)
    {
        var id = msg["id"]?.DeepClone();
        var method = Str(msg["method"]);
        if (method is null) return id is null ? null : Error(id, -32600, "Invalid request: missing or non-string method");
        bool isNotification = id is null;

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(msg["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = ToolDefinitions() },
                "tools/call" => await CallToolAsync(msg["params"] as JsonObject, ct),
                _ when method.StartsWith("notifications/") => null,
                _ => throw new RpcException(-32601, $"Method not found: {method}"),
            };
            if (isNotification) return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (RpcException ex)
        {
            return isNotification ? null : Error(id!, ex.Code, ex.Message);
        }
    }

    static JsonObject Initialize(JsonObject? p)
    {
        var requested = Str(p?["protocolVersion"]);
        var version = requested is not null && SupportedProtocols.Contains(requested) ? requested : LatestProtocol;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "quicklight", ["title"] = "Quicklight", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" },
            ["instructions"] = "Local Windows search. Use `search` like Spotlight (apps, files, settings, calculator, URLs, no prefixes needed). " +
                               "Use `search_files` for file-only queries with Everything syntax (ext:pdf, dm:today, size:>10mb, path matching). " +
                               "`open` launches a target returned by a search.",
        };
    }

    JsonArray ToolDefinitions()
    {
        var tools = new JsonArray
        {
            Tool("search", "Spotlight-style search over installed apps, files and folders (Everything index), Windows settings, system commands, calculator and URLs. Results are ranked; the first is the best match.",
                new JsonObject
                {
                    ["query"] = Prop("string", "What to look for, e.g. \"vscode\", \"블루투스\", \"quarterly report\", \"12*7\"."),
                    ["limit"] = Prop("integer", "Maximum results (1-50, default 10)."),
                    ["kinds"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Only return these kinds.",
                        ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(KindNames.Select(k => (JsonNode)k).ToArray()) },
                    },
                }, ["query"], readOnly: true),
            Tool("search_files", "Search files and folders by name with the Everything index. Supports Everything syntax: wildcards (*.pdf), ext:pdf;docx, dm:today, dm:last7days, size:>10mb, folder:, file:, parent:C:\\path, \"exact phrase\", | for OR, ! for NOT.",
                new JsonObject
                {
                    ["query"] = Prop("string", "Everything search expression."),
                    ["limit"] = Prop("integer", "Maximum results (1-500, default 30)."),
                    ["sort"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("name", "path", "size", "modified", "run_count"), ["description"] = "Sort order (default modified = newest first)." },
                    ["match_path"] = Prop("boolean", "Match the query against the full path, not just the name."),
                    ["regex"] = Prop("boolean", "Treat the query as a regular expression."),
                }, ["query"], readOnly: true),
            Tool("convert_currency", "Convert money between currencies with daily reference rates (open.er-api.com, ECB fallback). Currencies can be ISO codes (USD, KRW, JPY) or names (달러, 원, 엔, euro).",
                new JsonObject
                {
                    ["amount"] = Prop("number", "Amount in the source currency."),
                    ["from"] = Prop("string", "Source currency, e.g. \"USD\" or \"달러\"."),
                    ["to"] = Prop("string", "Target currency (default: the user's default currency, KRW unless configured)."),
                }, ["amount", "from"], readOnly: true),
            Tool("translate", "Translate text on this computer with a local LibreTranslate server (nothing is sent to the internet). The source language is detected automatically.",
                new JsonObject
                {
                    ["text"] = Prop("string", "Text to translate (up to 5000 characters)."),
                    ["to"] = Prop("string", "Target language: code (ko, en, ja, zh, ...) or name (영어, 한국어, japanese). Default: the system language, or English when the text already is in it."),
                }, ["text"], readOnly: true),
            Tool("calculate", "Evaluate a math expression: + - * / % ^, parentheses, sqrt abs round floor ceil sin cos tan asin acos atan log ln exp, pi, e.",
                new JsonObject { ["expression"] = Prop("string", "e.g. \"(1200*1.1)^2 / 3\"") }, ["expression"], readOnly: true),
        };
        if (options.AllowOpen)
        {
            tools.Add(Tool("open", "Open a document, folder, URL (http/https), Windows settings page (ms-settings:) or installed app (shell:AppsFolder\\... from search) with its default handler, as if the user double-clicked it. Executables, scripts, shortcuts and network paths are refused; use `reveal` to show those in Explorer instead.",
                new JsonObject { ["target"] = Prop("string", "Absolute path, URL, ms-settings: URI or shell:AppsFolder\\<id>.") }, ["target"], readOnly: false));
            tools.Add(Tool("reveal", "Show a file or folder selected in File Explorer.",
                new JsonObject { ["path"] = Prop("string", "Absolute path of an existing file or folder.") }, ["path"], readOnly: false));
        }
        return tools;
    }

    async Task<JsonObject> CallToolAsync(JsonObject? p, CancellationToken ct)
    {
        var name = Str(p?["name"]) ?? throw new RpcException(-32602, "Missing or non-string tool name");
        var args = p!["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            return name switch
            {
                "search" => await SearchAsync(args, ct),
                "search_files" => await SearchFilesAsync(args, ct),
                "calculate" => Calculate(args),
                "translate" => await TranslateAsync(args, ct),
                "convert_currency" => await ConvertCurrencyAsync(args, ct),
                "open" when options.AllowOpen => await OpenAsync(args),
                "reveal" when options.AllowOpen => Reveal(args),
                _ => throw new RpcException(-32602, $"Unknown tool: {name}"),
            };
        }
        catch (ToolException ex) { return ToolError(ex.Message); }
        catch (EverythingUnavailableException ex) { return ToolError(ex.Message); }
        catch (TimeoutException ex) { return ToolError(ex.Message); }
        catch (ArgumentTypeException ex) { return ToolError("Invalid arguments: " + ex.Message); }
    }

    async Task<JsonObject> SearchAsync(JsonObject args, CancellationToken ct)
    {
        var query = RequiredString(args, "query");
        int limit = Math.Clamp(Arg<int>(args, "limit") ?? 10, 1, 50);
        HashSet<ResultKind>? kinds = null;
        if (args["kinds"] is JsonArray arr && arr.Count > 0)
        {
            kinds = [];
            foreach (var k in arr)
            {
                var s = Str(k) ?? "";
                var match = Enum.GetValues<ResultKind>().FirstOrDefault(v => ToSnake(v.ToString()) == s || v.ToString().Equals(s, StringComparison.OrdinalIgnoreCase), (ResultKind)(-1));
                if ((int)match < 0) throw new ToolException($"Unknown kind '{s}'. Use one of: {string.Join(", ", KindNames)}");
                kinds.Add(match);
            }
        }
        await _warmUp;
        var results = await engine.SearchAsync(query, limit, ct, kinds);
        var items = new JsonArray();
        foreach (var r in results)
        {
            var o = new JsonObject
            {
                ["title"] = r.Title,
                ["kind"] = ToSnake(r.Kind.ToString()),
                ["target"] = r.Target,
                ["subtitle"] = r.Subtitle,
                ["score"] = Math.Round(r.Score, 1),
            };
            if (r.Arguments is not null) o["arguments"] = r.Arguments;
            if (r.Modified is { } m) o["modified"] = m.ToString("yyyy-MM-dd HH:mm");
            if (r.Size is { } sz) o["size"] = sz;
            if (r.RequiresConfirmation) o["destructive"] = true;
            if (r.IsSystem) o["is_system"] = true;
            if (r.IsSystemSummary) o["is_system_summary"] = true;
            items.Add(o);
        }
        return ToolResult(new JsonObject { ["query"] = query, ["results"] = items }, McpMarkdown.Search(query, results));
    }

    async Task<JsonObject> SearchFilesAsync(JsonObject args, CancellationToken ct)
    {
        if (everything is null) throw new ToolException("File search is disabled.");
        var query = RequiredString(args, "query");
        int limit = Math.Clamp(Arg<int>(args, "limit") ?? 30, 1, 500);
        var sort = (Str(args["sort"]) ?? "modified") switch
        {
            "name" => EverythingSort.NameAscending,
            "path" => EverythingSort.PathAscending,
            "size" => EverythingSort.SizeDescending,
            "run_count" => EverythingSort.RunCountDescending,
            "modified" => EverythingSort.DateModifiedDescending,
            var s => throw new ToolException($"Unknown sort '{s}'."),
        };
        var items = await everything.SearchAsync(new EverythingQuery(query, limit, sort,
            MatchPath: Arg<bool>(args, "match_path") ?? false, Regex: Arg<bool>(args, "regex") ?? false), ct);
        var arr = new JsonArray();
        foreach (var i in items)
        {
            var o = new JsonObject { ["path"] = i.FullPath, ["is_folder"] = i.IsFolder };
            if (i.Size >= 0) o["size"] = i.Size;
            if (i.Modified is { } m) o["modified"] = m.ToString("yyyy-MM-dd HH:mm");
            arr.Add(o);
        }
        return ToolResult(new JsonObject { ["query"] = query, ["count"] = arr.Count, ["results"] = arr }, McpMarkdown.Files(query, items));
    }

    async Task<JsonObject> ConvertCurrencyAsync(JsonObject args, CancellationToken ct)
    {
        double amount = Arg<double>(args, "amount") ?? throw new ToolException("'amount' is required.");
        if (!double.IsFinite(amount) || amount <= 0) throw new ToolException("'amount' must be a positive number.");
        var fromText = RequiredString(args, "from");
        var toText = Str(args["to"]);
        if (engine.Rates is null) throw new ToolException("Currency conversion is not available.");
        var table = await engine.Rates.GetAsync(TimeSpan.FromSeconds(8), ct)
            ?? throw new ToolException("Exchange rates could not be downloaded and none are cached.");

        var from = Quicklight.Core.Currency.Currencies.Resolve(fromText, table.Has) ?? throw new ToolException($"Unknown currency '{fromText}'.");
        var fallback = table.Has(engine.DefaultCurrency) ? engine.DefaultCurrency : "USD"; // a bad setting must not break the tool
        var to = toText is null
            ? (from == fallback ? (fallback == "USD" ? "KRW" : "USD") : fallback)
            : Quicklight.Core.Currency.Currencies.Resolve(toText, table.Has) ?? throw new ToolException($"Unknown currency '{toText}'.");
        double value = table.Convert(amount, from, to);
        if (!double.IsFinite(value)) throw new ToolException("Result out of range.");
        var formatted = Quicklight.Core.Currency.Currencies.Format(value, to);
        var markdown = McpMarkdown.Answer(formatted,
            $"{Quicklight.Core.Currency.Currencies.Format(amount, from)} → {Quicklight.Core.Currency.Currencies.KoreanName(to)} · " +
            $"1 {from} = {Quicklight.Core.Currency.Currencies.FormatRate(table.Rate(from, to))} {to} · {table.UpdatedUtc:yyyy-MM-dd} 기준 ({table.Source})");
        return ToolResult(new JsonObject
        {
            ["amount"] = amount,
            ["from"] = from,
            ["to"] = to,
            ["result"] = Math.Round(value, 6),
            ["formatted"] = Quicklight.Core.Currency.Currencies.Format(value, to),
            ["rate"] = Math.Round(table.Rate(from, to), 8),
            ["rates_date"] = table.UpdatedUtc.ToString("yyyy-MM-dd"),
            ["source"] = table.Source,
        }, markdown);
    }

    async Task<JsonObject> TranslateAsync(JsonObject args, CancellationToken ct)
    {
        var text = RequiredString(args, "text");
        if (text.Length > 5000) throw new ToolException("'text' is longer than 5000 characters.");
        var toText = Str(args["to"]);
        if (engine.Translator is null) throw new ToolException("Translation is turned off in Quicklight settings.");
        string target;
        if (toText is null)
            target = Quicklight.Core.Translation.TranslationParser.DefaultTarget(text, Quicklight.Core.Translation.Languages.System, engine.SecondaryLanguage, engine.TranslationLanguages);
        else
            target = Quicklight.Core.Translation.Languages.CodeFor(toText)
                     ?? (toText.Length is 2 or 3 && toText.All(char.IsAsciiLetter) ? toText.ToLowerInvariant() : throw new ToolException($"Unknown language '{toText}'."));

        // The first call may start the local server and download models, so allow a while.
        if (!await engine.Translator.EnsureReadyAsync(TimeSpan.FromSeconds(90), ct))
            throw new ToolException("The local LibreTranslate server is not available (see %LOCALAPPDATA%\\Quicklight\\libretranslate.log).");
        Quicklight.Core.Translation.TranslationResult r;
        try
        {
            r = await engine.Translator.TranslateAsync(text, target, ct);
            if (toText is null && r.Source == target)
                r = await engine.Translator.TranslateAsync(text, Quicklight.Core.Translation.TranslationParser.Fallback(target, engine.SecondaryLanguage, engine.TranslationLanguages), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ToolException("Translation timed out."); }
        catch (Exception ex) when (ex is Quicklight.Core.Translation.TranslationException or HttpRequestException) { throw new ToolException("Translation failed: " + ex.Message); }
        return ToolResult(new JsonObject
        {
            ["text"] = text,
            ["translation"] = r.Text,
            ["source"] = r.Source,
            ["target"] = r.Target,
            ["alternatives"] = new JsonArray(r.Alternatives.Select(a => (JsonNode)a).ToArray()),
        }, McpMarkdown.Translation(r, text));
    }

    static JsonObject Calculate(JsonObject args)
    {
        var expr = RequiredString(args, "expression");
        // Bare numbers are valid math here, unlike in the launcher where "42" is more likely a file name.
        if (!Calculator.TryEvaluate(expr, out var v) && !Calculator.TryEvaluate("0+" + expr, out v))
            throw new ToolException($"Cannot evaluate '{expr}'.");
        return ToolResult(new JsonObject { ["expression"] = expr, ["value"] = v, ["formatted"] = Calculator.Format(v) },
            McpMarkdown.Answer(Calculator.Format(v), expr.Trim().TrimEnd('=').Trim() + " ="));
    }

    async Task<JsonObject> OpenAsync(JsonObject args)
    {
        var target = RequiredString(args, "target").Trim();
        if (target.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
        {
            // Only apps from the index: an arbitrary AppsFolder path can name any program under a known folder.
            await _warmUp;
            if (!engine.Apps.IsKnownLaunchTarget(target)) throw new ToolException("Unknown app. Use a shell:AppsFolder target returned by `search`.");
        }
        else if (OpenableTarget(target) is not { } normalized)
        {
            throw new ToolException(IsBlockedExecutable(target)
                ? "Refusing to open executables, scripts or shortcuts from MCP. Use `reveal` to show it in Explorer and let the user decide."
                : "Refusing to open: target must be an existing local absolute path, an http(s) URL or an ms-settings: URI.");
        }
        else target = normalized;
        (options.Opener ?? (t => ShellLauncher.Open(t)))(target);
        return ToolResult(new JsonObject { ["opened"] = target }, $"열었습니다: `{target.Replace('`', '\'')}`");
    }

    JsonObject Reveal(JsonObject args)
    {
        var path = RequiredString(args, "path").Trim();
        if (PathProvider.IsRemoteOrDevice(path) || !Path.IsPathFullyQualified(path) || !(File.Exists(path) || Directory.Exists(path)))
            throw new ToolException($"Not an existing absolute path: {path}");
        (options.Revealer ?? ShellLauncher.Reveal)(path);
        return ToolResult(new JsonObject { ["revealed"] = path }, $"탐색기에서 보여 줍니다: `{path.Replace('`', '\'')}`");
    }

    /// <summary>Extensions that run code when opened. PATHEXT is added at startup.</summary>
    static readonly HashSet<string> BlockedExtensions = BuildBlockedExtensions();

    static HashSet<string> BuildBlockedExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msc", ".ps1", ".psm1", ".psd1",
            ".lnk", ".url", ".website", ".hta", ".scr", ".pif", ".msi", ".msp", ".mst", ".cpl", ".reg", ".inf", ".jar",
            ".appref-ms", ".application", ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms",
            ".diagcab", ".appx", ".appxbundle", ".msix", ".msixbundle", ".gadget", ".chm", ".sys", ".dll", ".ocx",
            // Run code through an installed handler, mount images that drop Mark-of-the-Web, or connect to remote hosts.
            ".py", ".pyw", ".pyz", ".xll", ".xlam", ".ppam", ".vsto", ".jnlp", ".wsc", ".sct", ".ps1xml",
            ".iso", ".img", ".vhd", ".vhdx", ".theme", ".themepack", ".deskthemepack", ".scf", ".rdp", ".ica",
        };
        foreach (var ext in (Environment.GetEnvironmentVariable("PATHEXT") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            set.Add(ext.Trim());
        return set;
    }

    static bool IsBlockedExecutable(string path)
    {
        try { return BlockedExtensions.Contains(Path.GetExtension(path.TrimEnd('.', ' '))); }
        catch (ArgumentException) { return true; }
    }

    /// <summary>
    /// What `open` accepts besides indexed apps: http(s) URLs (normalized), ms-settings: pages, and existing local documents
    /// or folders. Returns the string to hand to the shell, or null. Never touches UNC paths (that would leak credentials).
    /// </summary>
    internal static string? OpenableTarget(string t)
    {
        if (t.Length == 0 || t.IndexOfAny(['\r', '\n', '\0', '"']) >= 0) return null;
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !u.IsUnc)
            return u.AbsoluteUri;
        if (t.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) && t.Length < 100 && t.All(c => char.IsLetterOrDigit(c) || c is ':' or '-' or '_'))
            return t;
        if (PathProvider.IsRemoteOrDevice(t) || !Path.IsPathFullyQualified(t) || t.IndexOf(':', 2) >= 0) return null; // UNC, relative, alternate data streams
        if (Directory.Exists(t)) return t;
        return File.Exists(t) && !IsBlockedExecutable(t) ? t : null;
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Optional typed argument; a value of the wrong JSON type is a tool error, not a crash.</summary>
    static T? Arg<T>(JsonObject args, string name) where T : struct
    {
        var node = args[name];
        if (node is null) return null;
        if (node is JsonValue v && v.TryGetValue<T>(out var value)) return value;
        // Any JSON number is fine for a double ("amount": 100 as well as 100.5).
        if (typeof(T) == typeof(double) && node.GetValueKind() == JsonValueKind.Number &&
            double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return (T)(object)d;
        throw new ArgumentTypeException($"'{name}' must be a {(typeof(T) == typeof(bool) ? "boolean" : "number")}.");
    }

    static string RequiredString(JsonObject args, string name)
    {
        var node = args[name];
        if (node is not null && Str(node) is null) throw new ArgumentTypeException($"'{name}' must be a string.");
        var v = Str(node);
        if (string.IsNullOrWhiteSpace(v)) throw new ToolException($"'{name}' is required.");
        return v;
    }

    static JsonObject Tool(string name, string description, JsonObject props, string[] required, bool readOnly) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["openWorldHint"] = false },
    };

    static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    /// <summary>
    /// Text content is Markdown shaped like the launcher (see <see cref="McpMarkdown"/>), so a client shows a readable
    /// panel instead of a window; the same data stays machine-readable in structuredContent.
    /// </summary>
    static JsonObject ToolResult(JsonObject structured, string markdown) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = markdown }),
        ["structuredContent"] = structured,
        ["isError"] = false,
    };

    static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };

    static JsonObject Error(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    /// <summary>Keeps Korean and other non-ASCII text readable (and shorter) on the wire instead of escape sequences.</summary>
    public static readonly JsonSerializerOptions WireOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string ToSnake(string s) => string.Concat(s.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

    sealed class RpcException(int code, string message) : Exception(message) { public int Code { get; } = code; }
    sealed class ToolException(string message) : Exception(message);
    sealed class ArgumentTypeException(string message) : Exception(message);
}
