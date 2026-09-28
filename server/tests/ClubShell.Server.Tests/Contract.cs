using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

namespace ClubShell.Server.Tests;

/// <summary>A contract operation as the test reads it from <c>server/contracts/openapi.json</c>, independently of the server.</summary>
public sealed record ContractOp(string Method, string Path, string OperationId, JsonElement Operation, Regex Pattern, int Parameters);

/// <summary>
/// Schema checks against the vendored contract (DESIGN §10.a; port of club-server <c>TestHelpers.Contract</c>): the whole
/// document in a <see cref="SchemaRegistry"/>, Draft 2020-12, unknown keywords allowed, formats required.
/// </summary>
public static class Contract
{
    private static readonly Uri BaseUri = new("https://clubshell.local/openapi.json");
    private static readonly Dialect Dialect = Dialect.Draft202012.With([], allowUnknownKeywords: true);
    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new();
    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch"];

    private static readonly Lazy<(SchemaRegistry Registry, JsonElement Document)> Loaded = new(() =>
    {
        var document = JsonElement.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "openapi.json")));
        var registry = new SchemaRegistry();
        registry.Register(BaseUri, new JsonElementBaseDocument(document, BaseUri));
        return (registry, document);
    });

    public static JsonElement Document => Loaded.Value.Document;

    public static IReadOnlyList<ContractOp> Operations { get; } = LoadOperations();

    /// <summary>The operation for a request; a literal segment beats a parameter (<c>/auth/qr/start</c> vs <c>/auth/qr/{token}</c>).</summary>
    public static ContractOp? Match(string method, string pathUnderApi) =>
        Operations.Where(o => o.Method == method && o.Pattern.IsMatch(pathUnderApi)).MinBy(o => o.Parameters);

    /// <summary>
    /// Validates a response of <paramref name="operationId"/>: the status must be declared (501 is always allowed — the
    /// catch-all answers it for operations the slice has not implemented yet) and the body must match its schema.
    /// </summary>
    public static void AssertResponse(string operationId, int status, JsonElement? body)
    {
        var op = Operations.Single(o => o.OperationId == operationId);
        var code = status.ToString(CultureInfo.InvariantCulture);
        JsonElement response;
        if (op.Operation.GetProperty("responses").TryGetProperty(code, out var declared))
        {
            response = Resolve(declared);
        }
        else if (status == 501)
        {
            response = Resolve(Document.GetProperty("components").GetProperty("responses").GetProperty("NotImplemented"));
        }
        else
        {
            Assert.Fail($"{operationId}: status {status} is not declared in the contract\n{body}");
            return;
        }

        if (!response.TryGetProperty("content", out var content))
        {
            Assert.True(body is null, $"{operationId} {status}: the contract declares no body, got {body}");
            return;
        }

        Assert.True(body is not null, $"{operationId} {status}: body expected");
        var reference = content.GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()!;
        AssertMatchesRef(reference, body.Value);
    }

    public static void AssertMatches(string schemaName, JsonElement instance) => AssertMatchesRef("#/components/schemas/" + schemaName, instance);

    public static void AssertError(JsonElement body, string code, string? reason = null)
    {
        AssertMatches("ServerErrorEnvelope", body);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
        if (reason is not null)
        {
            Assert.Equal(reason, body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
        }
    }

    public static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response, int status, string code, string? reason = null)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status == (int)response.StatusCode, $"expected {status}, got {(int)response.StatusCode}: {body}");
        AssertError(body, code, reason);
        return body;
    }

    private static void AssertMatchesRef(string reference, JsonElement instance)
    {
        var schema = Schemas.GetOrAdd(reference, r =>
        {
            var options = new BuildOptions { SchemaRegistry = Loaded.Value.Registry, Dialect = Dialect };
            var wrapper = JsonElement.Parse($$"""{ "$ref": "{{BaseUri}}{{r}}" }""");
            return JsonSchema.Build(wrapper, options, new Uri($"https://clubshell.local/check/{Guid.NewGuid():N}"));
        });
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (!result.IsValid)
        {
            var errors = (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                .Select(d => $"{d.InstanceLocation}: {string.Join("; ", d.Errors!.Values)}");
            Assert.Fail($"{reference} does not match the contract:\n{string.Join("\n", errors)}\n{instance}");
        }
    }

    /// <summary>Follows <c>$ref: '#/components/responses/X'</c>.</summary>
    private static JsonElement Resolve(JsonElement response)
    {
        if (!response.TryGetProperty("$ref", out var reference))
        {
            return response;
        }

        var node = Document;
        foreach (var segment in reference.GetString()!.TrimStart('#', '/').Split('/'))
        {
            node = node.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        return node;
    }

    private static List<ContractOp> LoadOperations()
    {
        var ops = new List<ContractOp>();
        foreach (var path in Document.GetProperty("paths").EnumerateObject())
        {
            var pattern = new Regex("^" + Regex.Replace(Regex.Escape(path.Name), @"\\\{\w+}", "[^/]+") + "$", RegexOptions.CultureInvariant);
            var parameters = path.Name.Count(ch => ch == '{');
            foreach (var method in Methods)
            {
                if (path.Value.TryGetProperty(method, out var op))
                {
                    ops.Add(new ContractOp(method.ToUpperInvariant(), path.Name, op.GetProperty("operationId").GetString()!, op, pattern, parameters));
                }
            }
        }

        return ops;
    }
}

/// <summary>
/// Validates every <c>/api/v1</c> response of <see cref="ServerFixture.Http"/> (DESIGN §10.a): contract operation → its
/// declared status and schema; unknown route → error envelope. <c>X-Trace-Id</c> and <c>X-Server-Time</c> are always set.
/// </summary>
public sealed class ContractValidatingHandler(ServerFixture fixture) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        if (!path.StartsWith("/api/v1/", StringComparison.Ordinal))
        {
            return response;
        }

        Assert.True(response.Headers.Contains("X-Trace-Id"), $"{request.Method} {path}: no X-Trace-Id");
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", response.Headers.GetValues("X-Server-Time").Single());

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        JsonElement? body = bytes.Length == 0 ? null : JsonElement.Parse(bytes);
        var status = (int)response.StatusCode;
        var op = Contract.Match(request.Method.Method, path["/api/v1".Length..]);
        if (op is null)
        {
            Assert.True(status >= 400 && body is not null, $"{request.Method} {path} is not in the contract but answered {status}");
            Contract.AssertMatches("ServerErrorEnvelope", body!.Value);
        }
        else
        {
            Contract.AssertResponse(op.OperationId, status, body);
            fixture.Covered[$"{op.OperationId} {status}"] = true;
        }

        return response;
    }
}
