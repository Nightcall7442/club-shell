using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

namespace ClubShell.Server.Infrastructure;

/// <summary>
/// The contract's JSON Schemas (<c>contracts/openapi.json</c>, Draft 2020-12, formats checked) for request bodies that are
/// stored as documents rather than bound to records: the club settings (DESIGN §4.2: "валидируется JSON-схемой контракта")
/// and the health thresholds. A mismatch is <c>400 validation</c> with the deepest failing <c>field</c> as a JSON path
/// (<c>zones[1].color</c>) and the contract's reason for the keyword (<c>required</c>, <c>format</c>, <c>enum</c>,
/// <c>min</c>, <c>max</c>; anything else <c>schema</c>).
/// </summary>
public static partial class ContractSchemas
{
    private static readonly Uri BaseUri = new("https://clubshell.local/openapi.json");
    private static readonly ConcurrentDictionary<string, JsonSchema> Built = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    private static readonly Lazy<SchemaRegistry> Registry = new(() =>
    {
        var document = JsonElement.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "openapi.json")));
        var registry = new SchemaRegistry();
        registry.Register(BaseUri, new JsonElementBaseDocument(document, BaseUri));
        return registry;
    });

    /// <summary>Throws <c>400 validation</c> unless <paramref name="instance"/> matches <c>#/components/schemas/<paramref name="schema"/></c>.</summary>
    public static void Validate(string schema, JsonElement instance)
    {
        EvaluationResults result;

        // One at a time: the registry resolves references lazily and is not safe for concurrent use (settings saves are rare).
        lock (Gate)
        {
            var built = Built.GetOrAdd(schema, name => JsonSchema.Build(
                JsonElement.Parse($$"""{ "$ref": "{{BaseUri}}#/components/schemas/{{name}}" }"""),
                new BuildOptions { SchemaRegistry = Registry.Value, Dialect = Dialect.Draft202012.With([], allowUnknownKeywords: true) },
                new Uri($"https://clubshell.local/request/{name}")));
            result = built.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true });
        }

        if (result.IsValid)
        {
            return;
        }

        // Only failing nodes are walked: a oneOf that matched does not report the errors of its other branches.
        var errors = new List<(string Path, string Keyword, string Message)>();
        void Walk(EvaluationResults node)
        {
            if (node.IsValid)
            {
                return;
            }

            foreach (var (keyword, message) in node.Errors ?? new Dictionary<string, string>())
            {
                if (!Aggregates.Contains(keyword))
                {
                    errors.Add((node.InstanceLocation.ToString(), keyword, message));
                }
            }

            foreach (var child in node.Details ?? [])
            {
                Walk(child);
            }
        }

        Walk(result);

        // A discriminated union (AdminRuleTrigger, AdminRuleAction) fails in every branch: the branch whose "kind" matched
        // reports the real problem one level up, the others only their "kind" — drop those.
        var branchKinds = errors.Where(e => e.Keyword is "enum" or "const" && e.Path.EndsWith("/kind", StringComparison.Ordinal)
            && errors.Any(o => o.Path == e.Path[..^"/kind".Length] || (o.Path.StartsWith(e.Path[..^"/kind".Length] + "/", StringComparison.Ordinal) && o.Path != e.Path)))
            .ToList();
        var (path, keyword, message) = errors.Except(branchKinds).DefaultIfEmpty((Path: "", Keyword: "schema", Message: "")).OrderByDescending(e => Depth(e.Path)).First();
        var field = Field(path);
        if (keyword == "required" && MissingName().Match(message) is { Success: true } missing)
        {
            field = field.Length == 0 ? missing.Groups[1].Value : $"{field}.{missing.Groups[1].Value}";
        }

        throw ApiException.Validation(field.Length == 0 ? "body" : field, Reason(keyword), $"Request does not match {schema}");
    }

    /// <summary>Keywords that only say that something below them failed.</summary>
    private static readonly HashSet<string> Aggregates =
        ["properties", "items", "prefixItems", "patternProperties", "oneOf", "anyOf", "allOf", "$ref", "not", "then", "else", "dependentSchemas"];

    private static int Depth(string pointer) => pointer.Count(ch => ch == '/');

    /// <summary>JSON pointer <c>/zones/1/color</c> → <c>zones[1].color</c>.</summary>
    private static string Field(string pointer)
    {
        var field = "";
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            field += segment.All(char.IsAsciiDigit) ? $"[{segment}]" : field.Length == 0 ? segment : "." + segment;
        }

        return field;
    }

    private static string Reason(string keyword) => keyword switch
    {
        "required" => "required",
        "type" or "pattern" or "format" => "format",
        "enum" or "const" or "propertyNames" => "enum",
        "minimum" or "exclusiveMinimum" or "minItems" or "minLength" or "minProperties" => "min",
        "maximum" or "exclusiveMaximum" or "maxItems" or "maxLength" or "maxProperties" => "max",
        _ => "schema",
    };

    [GeneratedRegex("\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex MissingName();
}
