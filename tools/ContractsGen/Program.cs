// ContractsGen — reflection-based generator that turns ClubShell.Contracts into the TypeScript (packages/contracts-ts)
// and Rust (crates/protocol) mirrors. Usage: ContractsGen --target ts|rs --out <dir> [--check] [--verbose]
//
// The generator is deterministic: same assembly → same bytes. Hand-written helpers survive regeneration when they sit
// between "// ---- BEGIN MANUAL ----" and "// ---- END MANUAL ----" in the existing target file: every such block is
// carried over, in order, into the single MANUAL slot the generator leaves at the end of the regenerated file (after the
// declarations, so TS runtime code may reference generated consts). Types listed in FileMap.{Ts,Rs}ManualTypes are
// never emitted (their mirror form is hand-written inside a MANUAL block); crates/protocol/src/lib.rs and ipc.rs are
// hand-written and not generated at all.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Linq;
using ClubShell.Contracts.Serialization;

var options = CliOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}

var assembly = typeof(JsonDefaults).Assembly;
var docsPath = Path.ChangeExtension(assembly.Location, ".xml");
var docs = XmlDocs.Load(docsPath);
if (docs.IsEmpty)
{
    Console.Error.WriteLine($"warning: no XML documentation found at '{docsPath}'; the output will carry no doc comments");
}

var model = ModelBuilder.Build(assembly, docs);
if (options.Verbose)
{
    Console.WriteLine($"ClubShell.Contracts v{model.Version}: {model.Types.Count} contract types");
    foreach (var contract in model.Types)
    {
        Console.WriteLine($"  {contract.Namespace}.{contract.Name} [{contract.Kind}] -> {FileMap.FileOf(contract, options.Target)}");
    }
}

var files = options.Target == "ts" ? TsEmitter.Emit(model) : RsEmitter.Emit(model);
return OutputWriter.Run(files, options);

// ---------------------------------------------------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------------------------------------------------

internal sealed record CliOptions(string Target, string OutDir, bool Check, bool Verbose)
{
    public const string Usage = "usage: ContractsGen --target ts|rs --out <dir> [--check] [--verbose]";

    public static CliOptions? Parse(IReadOnlyList<string> args)
    {
        string? target = null;
        string? outDir = null;
        var check = false;
        var verbose = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Count:
                    target = args[++i];
                    break;
                case "--out" when i + 1 < args.Count:
                    outDir = args[++i];
                    break;
                case "--check":
                    check = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    return null;
            }
        }

        return target is "ts" or "rs" && outDir is not null ? new CliOptions(target, outDir, check, verbose) : null;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Shape categories a contract property type maps to.</summary>
internal enum ShapeKind
{
    /// <summary>Scalar; <see cref="TypeShape.Name"/> is one of string, guid, datetime, dateonly, timeonly, bool, int, long, short, byte, sbyte, ushort, uint, ulong, float, double, decimal, json.</summary>
    Primitive,

    /// <summary>Sequence; <c>Args[0]</c> is the item shape.</summary>
    List,

    /// <summary>String-keyed dictionary; <c>Args[0]</c> is the value shape.</summary>
    Map,

    /// <summary>Another contract type (record or enum); <c>Args</c> are generic arguments.</summary>
    Contract,

    /// <summary>Generic type parameter of the declaring record.</summary>
    TypeParam,

    /// <summary>Anything else; emitted as an opaque JSON value.</summary>
    Unknown,
}

/// <summary>Language-neutral description of a property type.</summary>
internal sealed record TypeShape(ShapeKind Kind, string Name, bool Nullable, IReadOnlyList<TypeShape> Args)
{
    public static TypeShape Primitive(string name, bool nullable = false) => new(ShapeKind.Primitive, name, nullable, Array.Empty<TypeShape>());

    public static TypeShape List(TypeShape item, bool nullable = false) => new(ShapeKind.List, "list", nullable, new[] { item });

    public static TypeShape Map(TypeShape value, bool nullable = false) => new(ShapeKind.Map, "map", nullable, new[] { value });

    public static TypeShape Contract(string name, bool nullable = false, params TypeShape[] args) => new(ShapeKind.Contract, name, nullable, args);

    public static TypeShape TypeParam(string name, bool nullable = false) => new(ShapeKind.TypeParam, name, nullable, Array.Empty<TypeShape>());

    /// <summary>Names of every contract type referenced by this shape (recursively).</summary>
    public IEnumerable<string> ContractNames()
    {
        if (Kind == ShapeKind.Contract)
        {
            yield return Name;
        }

        foreach (var arg in Args)
        {
            foreach (var name in arg.ContractNames())
            {
                yield return name;
            }
        }
    }
}

internal sealed record EnumMember(string Name, string Wire, string? Doc);

internal sealed record Property(string Name, string JsonName, TypeShape Shape, bool AlwaysPresent, string? Doc)
{
    /// <summary>Omitted from the wire when null (<c>?</c> in TS, <c>skip_serializing_if</c> in Rust).</summary>
    public bool Optional => Shape.Nullable && !AlwaysPresent;
}

internal sealed record Constant(string Name, object Value, string? Doc);

/// <param name="All">The C# class exposes a static <c>All</c> list of its string constants (mirrored as <c>ALL</c> in Rust).</param>
internal sealed record Holder(string Name, string? Doc, IReadOnlyList<Constant> Constants, IReadOnlyList<Holder> Nested, bool All);

internal enum ContractKind
{
    Enum,
    Record,
    Holder,
}

internal sealed record ContractType(
    string Name,
    string Namespace,
    ContractKind Kind,
    string? Doc,
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<EnumMember> Members,
    IReadOnlyList<Property> Properties,
    IReadOnlyList<Constant> Constants,
    Holder? Holder)
{
    /// <summary>Distinct names of contract types this type's properties reference.</summary>
    public IEnumerable<string> Dependencies() => Properties.SelectMany(p => p.Shape.ContractNames()).Distinct(StringComparer.Ordinal);
}

internal sealed record ContractModel(string Version, IReadOnlyList<ContractType> Types)
{
    private readonly Dictionary<string, ContractType> _byName = Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public ContractType? Find(string name) => _byName.TryGetValue(name, out var type) ? type : null;
}

internal static class ModelBuilder
{
    private const string RootNamespace = "ClubShell.Contracts.";

    private static readonly JsonSerializerOptions EnumOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static readonly NullabilityInfoContext Nullability = new();

    private static readonly HashSet<Type> ListDefinitions =
    [
        typeof(IReadOnlyList<>), typeof(IReadOnlyCollection<>), typeof(IEnumerable<>), typeof(IList<>), typeof(ICollection<>),
        typeof(List<>), typeof(ISet<>), typeof(IReadOnlySet<>), typeof(HashSet<>),
    ];

    private static readonly HashSet<Type> MapDefinitions =
    [
        typeof(IReadOnlyDictionary<,>), typeof(IDictionary<,>), typeof(Dictionary<,>),
    ];

    private static readonly Dictionary<Type, string> Primitives = new()
    {
        [typeof(string)] = "string",
        [typeof(char)] = "string",
        [typeof(Uri)] = "string",
        [typeof(TimeSpan)] = "string",
        [typeof(Guid)] = "guid",
        [typeof(DateTimeOffset)] = "datetime",
        [typeof(DateTime)] = "datetime",
        [typeof(DateOnly)] = "dateonly",
        [typeof(TimeOnly)] = "timeonly",
        [typeof(bool)] = "bool",
        [typeof(int)] = "int",
        [typeof(long)] = "long",
        [typeof(short)] = "short",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(ushort)] = "ushort",
        [typeof(uint)] = "uint",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(JsonElement)] = "json",
        [typeof(JsonDocument)] = "json",
        [typeof(System.Text.Json.Nodes.JsonNode)] = "json",
    };

    public static ContractModel Build(Assembly assembly, XmlDocs docs)
    {
        var types = new List<ContractType>();
        foreach (var type in assembly.GetExportedTypes().Where(t => !t.IsNested).OrderBy(t => t.FullName ?? t.Name, StringComparer.Ordinal))
        {
            var contract = ToContract(type, assembly, docs);
            if (contract is not null)
            {
                types.Add(contract);
            }
        }

        return new ContractModel(VersionOf(assembly), types);
    }

    private static string VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            return informational.Split('+')[0];
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : FormattableString.Invariant($"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}");
    }

    private static ContractType? ToContract(Type type, Assembly assembly, XmlDocs docs)
    {
        var name = Naming.StripArity(type.Name);
        var ns = ShortNamespace(type.Namespace);
        var typeId = XmlDocs.TypeId(type);
        var prefix = XmlDocs.MemberPrefix(type);
        var doc = docs.Summary(typeId);

        if (type.IsEnum)
        {
            return new ContractType(name, ns, ContractKind.Enum, doc, [], EnumMembers(type, prefix, docs), [], [], null);
        }

        if (IsStaticClass(type))
        {
            var holder = ToHolder(type, docs);
            return holder is null ? null : new ContractType(name, ns, ContractKind.Holder, doc, [], [], [], [], holder);
        }

        if (!IsRecord(type))
        {
            return null;
        }

        var typeParams = type.IsGenericTypeDefinition ? type.GetGenericArguments().Select(a => a.Name).ToArray() : Array.Empty<string>();
        return new ContractType(
            name,
            ns,
            ContractKind.Record,
            doc,
            typeParams,
            [],
            Properties(type, assembly, prefix, typeId, docs),
            Constants(type, prefix, docs),
            null);
    }

    private static string ShortNamespace(string? ns) =>
        ns is not null && ns.StartsWith(RootNamespace, StringComparison.Ordinal) ? ns[RootNamespace.Length..] : ns ?? string.Empty;

    private static bool IsStaticClass(Type type) => type.IsClass && type.IsAbstract && type.IsSealed;

    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null
        || type.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance) is not null;

    private static List<EnumMember> EnumMembers(Type type, string prefix, XmlDocs docs)
    {
        var names = Enum.GetNames(type);
        var values = Enum.GetValues(type);
        var members = new List<EnumMember>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            var value = values.GetValue(i);
            var wire = value is null ? Naming.ToCamelCase(names[i]) : WireName(type, names[i], value);
            members.Add(new EnumMember(names[i], wire, docs.Summary("F:" + prefix + "." + names[i])));
        }

        return members;
    }

    /// <summary>
    /// Wire literal of an enum member: <c>JsonStringEnumMemberName</c> when present (looked up by name so this compiles on
    /// .NET 8 and 9), otherwise whatever the enum's <c>[JsonConverter]</c> writes (camelCase policy, IPC names, …), otherwise camelCase.
    /// </summary>
    private static string WireName(Type enumType, string memberName, object value)
    {
        var field = enumType.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
        if (field is not null)
        {
            foreach (var attribute in field.GetCustomAttributes(inherit: false))
            {
                var attributeType = attribute.GetType();
                if (attributeType.Name == "JsonStringEnumMemberNameAttribute" && attributeType.GetProperty("Name")?.GetValue(attribute) is string explicitName)
                {
                    return explicitName;
                }
            }
        }

        try
        {
            var json = JsonSerializer.Serialize(value, enumType, EnumOptions);
            if (json.Length >= 2 && json[0] == '"')
            {
                return JsonSerializer.Deserialize<string>(json) ?? Naming.ToCamelCase(memberName);
            }
        }
        catch (NotSupportedException)
        {
            // No converter usable through reflection: fall back to the camelCase policy.
        }
        catch (InvalidOperationException)
        {
            // Same.
        }
        catch (JsonException)
        {
            // Same.
        }

        return Naming.ToCamelCase(memberName);
    }

    private static List<Property> Properties(Type type, Assembly assembly, string prefix, string typeId, XmlDocs docs)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).MaxBy(c => c.GetParameters().Length);
        if (ctor is not null)
        {
            var parameters = ctor.GetParameters();
            for (var i = 0; i < parameters.Length; i++)
            {
                order[parameters[i].Name ?? string.Empty] = i;
            }
        }

        var candidates = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .Where(p => order.ContainsKey(p.Name) || p.SetMethod is { IsPublic: true })
            .OrderBy(p => order.TryGetValue(p.Name, out var position) ? position : int.MaxValue)
            .ThenBy(p => p.MetadataToken);

        var result = new List<Property>();
        foreach (var property in candidates)
        {
            var ignore = property.GetCustomAttribute<JsonIgnoreAttribute>();
            if (ignore is { Condition: JsonIgnoreCondition.Always })
            {
                continue;
            }

            var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? Naming.ToCamelCase(property.Name);
            var shape = ShapeOf(Nullability.Create(property), assembly);
            var doc = docs.Summary("P:" + prefix + "." + property.Name) ?? docs.Param(typeId, property.Name);
            result.Add(new Property(property.Name, jsonName, shape, ignore is { Condition: JsonIgnoreCondition.Never }, doc));
        }

        return result;
    }

    private static Constant[] Constants(Type type, string prefix, XmlDocs docs) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly)
            .OrderBy(f => f.MetadataToken)
            .Select(f => new Constant(f.Name, f.GetRawConstantValue() ?? string.Empty, docs.Summary("F:" + prefix + "." + f.Name)))
            .Where(c => c.Value is string or int or long or bool or double)
            .ToArray();

    private static Holder? ToHolder(Type type, XmlDocs docs)
    {
        var constants = Constants(type, XmlDocs.MemberPrefix(type), docs);
        var nested = type.GetNestedTypes(BindingFlags.Public)
            .Where(IsStaticClass)
            .OrderBy(t => t.MetadataToken)
            .Select(t => ToHolder(t, docs))
            .OfType<Holder>()
            .ToArray();
        return constants.Length == 0 && nested.Length == 0
            ? null
            : new Holder(type.Name, docs.Summary(XmlDocs.TypeId(type)), constants, nested, type.GetMember("All", BindingFlags.Public | BindingFlags.Static).Length > 0);
    }

    private static TypeShape ShapeOf(NullabilityInfo info, Assembly contracts)
    {
        var type = info.Type;
        var nullable = info.ReadState == NullabilityState.Nullable || info.WriteState == NullabilityState.Nullable;
        var underlying = System.Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            type = underlying;
            nullable = true;
        }

        if (type.IsGenericParameter)
        {
            // An unconstrained `T` reads as nullable through NullabilityInfo; the mirrors declare `T[]` / `Vec<T>`.
            return TypeShape.TypeParam(type.Name);
        }

        if (type.IsArray)
        {
            var element = info.ElementType is { } elementInfo ? ShapeOf(elementInfo, contracts) : TypeShape.Primitive("json");
            return TypeShape.List(element, nullable);
        }

        if (Primitives.TryGetValue(type, out var primitive))
        {
            return TypeShape.Primitive(primitive, nullable);
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var args = info.GenericTypeArguments;
            if (ListDefinitions.Contains(definition) && args.Length == 1)
            {
                return TypeShape.List(ShapeOf(args[0], contracts), nullable);
            }

            if (MapDefinitions.Contains(definition) && args.Length == 2 && args[0].Type == typeof(string))
            {
                return TypeShape.Map(ShapeOf(args[1], contracts), nullable);
            }

            if (definition.Assembly == contracts)
            {
                return TypeShape.Contract(Naming.StripArity(definition.Name), nullable, args.Select(a => ShapeOf(a, contracts)).ToArray());
            }

            return new TypeShape(ShapeKind.Unknown, type.FullName ?? type.Name, nullable, Array.Empty<TypeShape>());
        }

        return type.Assembly == contracts
            ? TypeShape.Contract(type.Name, nullable)
            : new TypeShape(ShapeKind.Unknown, type.FullName ?? type.Name, nullable, Array.Empty<TypeShape>());
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// XML documentation
// ---------------------------------------------------------------------------------------------------------------------

internal sealed class XmlDocs
{
    private readonly Dictionary<string, XElement> _members;

    private XmlDocs(Dictionary<string, XElement> members)
    {
        _members = members;
    }

    public bool IsEmpty => _members.Count == 0;

    public static XmlDocs Load(string path)
    {
        var members = new Dictionary<string, XElement>(StringComparer.Ordinal);
        if (path.Length > 0 && File.Exists(path))
        {
            foreach (var member in XDocument.Load(path).Descendants("member"))
            {
                var name = (string?)member.Attribute("name");
                if (name is not null)
                {
                    members[name] = member;
                }
            }
        }

        return new XmlDocs(members);
    }

    /// <summary>Doc-comment id of a type (<c>T:Ns.Outer.Nested`1</c>).</summary>
    public static string TypeId(Type type) => "T:" + MemberPrefix(type);

    /// <summary>Doc-comment prefix of a type's members (<c>Ns.Outer.Nested`1</c>).</summary>
    public static string MemberPrefix(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    public string? Summary(string id) => _members.TryGetValue(id, out var member) ? Render(member.Element("summary")) : null;

    public string? Param(string typeId, string paramName) =>
        _members.TryGetValue(typeId, out var member)
            ? Render(member.Elements("param").FirstOrDefault(p => string.Equals((string?)p.Attribute("name"), paramName, StringComparison.Ordinal)))
            : null;

    /// <summary>Display form of a cref id: <c>T:A.B.C</c> → <c>C</c>; <c>P:A.B.C.D</c> → <c>C.D</c>; parameters and arity stripped.</summary>
    public static string CrefDisplay(string cref)
    {
        var body = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = body.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            body = body[..paren];
        }

        var tick = body.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            body = body[..tick];
        }

        var segments = body.Split('.');
        var keep = cref.StartsWith("T:", StringComparison.Ordinal) ? 1 : 2;
        return segments.Length <= keep ? body : string.Join('.', segments[^keep..]);
    }

    private static string? Render(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        RenderNodes(element, sb);
        var text = Collapse(sb.ToString());
        return text.Length == 0 ? null : text;
    }

    private static void RenderNodes(XElement parent, StringBuilder sb)
    {
        foreach (var node in parent.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement child:
                    RenderElement(child, sb);
                    break;
                default:
                    break;
            }
        }
    }

    private static void RenderElement(XElement element, StringBuilder sb)
    {
        switch (element.Name.LocalName)
        {
            case "c":
            case "code":
                sb.Append('`');
                RenderNodes(element, sb);
                sb.Append('`');
                break;
            case "see":
            case "seealso":
                sb.Append(SeeText(element));
                break;
            case "paramref":
                sb.Append('`').Append(Naming.ToCamelCase((string?)element.Attribute("name") ?? string.Empty)).Append('`');
                break;
            case "typeparamref":
                sb.Append('`').Append((string?)element.Attribute("name") ?? string.Empty).Append('`');
                break;
            case "para":
                RenderNodes(element, sb);
                sb.Append(' ');
                break;
            default:
                RenderNodes(element, sb);
                break;
        }
    }

    private static string SeeText(XElement element)
    {
        var cref = (string?)element.Attribute("cref");
        if (cref is not null)
        {
            return "`" + CrefDisplay(cref) + "`";
        }

        var langword = (string?)element.Attribute("langword");
        if (langword is not null)
        {
            return "`" + langword + "`";
        }

        return (string?)element.Attribute("href") ?? element.Value;
    }

    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Pure helpers (unit-testable)
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Identifier conversions shared by both emitters.</summary>
internal static class Naming
{
    private static readonly HashSet<string> RustKeywords = new(StringComparer.Ordinal)
    {
        "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum", "extern", "false", "fn", "for",
        "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self", "static",
        "struct", "super", "trait", "true", "type", "unsafe", "use", "where", "while", "abstract", "become", "box", "do",
        "final", "macro", "override", "priv", "try", "typeof", "unsized", "virtual", "yield", "gen",
    };

    private static readonly HashSet<string> RawIdentifierForbidden = new(StringComparer.Ordinal) { "self", "Self", "crate", "super" };

    /// <summary>System.Text.Json's camelCase policy: the leading upper-case run is lowered (<c>URLValue</c> → <c>urlValue</c>, <c>ID</c> → <c>id</c>).</summary>
    public static string ToCamelCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || !char.IsUpper(name[0]))
        {
            return name;
        }

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
            {
                break;
            }

            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
            {
                break;
            }

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

    /// <summary><c>WtsSessionId</c> → <c>wts_session_id</c>, <c>URLValue</c> → <c>url_value</c>, <c>Sha256</c> → <c>sha256</c>.</summary>
    public static string ToSnakeCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var boundary = i > 0
                    && (char.IsLower(name[i - 1])
                        || char.IsDigit(name[i - 1])
                        || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (boundary)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary><c>TopupIntent</c> → <c>TOPUP_INTENT</c>.</summary>
    public static string ToScreamingSnake(string name) => ToSnakeCase(name).ToUpperInvariant();

    /// <summary>serde's <c>rename_all = "camelCase"</c> applied to a snake_case field name.</summary>
    public static string SerdeCamelOfSnake(string snake)
    {
        ArgumentNullException.ThrowIfNull(snake);
        var parts = snake.Split('_');
        var sb = new StringBuilder(snake.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i == 0 || part.Length == 0)
            {
                sb.Append(part);
            }
            else
            {
                sb.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
            }
        }

        return sb.ToString();
    }

    /// <summary>Rust field identifier for a C# property: snake_case, raw (<c>r#type</c>) when it collides with a keyword.</summary>
    public static string RustField(string propertyName)
    {
        var snake = ToSnakeCase(propertyName);
        if (!RustKeywords.Contains(snake))
        {
            return snake;
        }

        return RawIdentifierForbidden.Contains(snake) ? snake + "_" : "r#" + snake;
    }

    /// <summary>Best-effort English singular for the value type of a constant holder (<c>ShellCapabilities</c> → <c>ShellCapability</c>).</summary>
    public static string Singular(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.EndsWith("ies", StringComparison.Ordinal))
        {
            return name[..^3] + "y";
        }

        if (name.EndsWith("sses", StringComparison.Ordinal) || name.EndsWith("shes", StringComparison.Ordinal)
            || name.EndsWith("ches", StringComparison.Ordinal) || name.EndsWith("xes", StringComparison.Ordinal))
        {
            return name[..^2];
        }

        if (name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal))
        {
            return name[..^1];
        }

        return name + "Value";
    }

    /// <summary><c>PagedResult`1</c> → <c>PagedResult</c>.</summary>
    public static string StripArity(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }
}

/// <summary>C# shape → target language type text.</summary>
internal static class TypeMap
{
    /// <summary>TS alias for an opaque <c>JsonElement</c>; declared in commands.ts (<see cref="TsEmitter"/>) and imported elsewhere.</summary>
    public const string TsJsonObject = "JsonObject";

    public static string MapTsType(TypeShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var core = shape.Kind switch
        {
            ShapeKind.Primitive => shape.Name switch
            {
                "string" or "guid" or "datetime" or "dateonly" or "timeonly" => "string",
                "bool" => "boolean",
                "json" => TsJsonObject,
                _ => "number",
            },
            ShapeKind.List => ArrayOf(MapTsType(shape.Args[0])),
            ShapeKind.Map => "Record<string, " + MapTsType(shape.Args[0]) + ">",
            ShapeKind.Contract => shape.Args.Count == 0 ? shape.Name : shape.Name + "<" + string.Join(", ", shape.Args.Select(MapTsType)) + ">",
            ShapeKind.TypeParam => shape.Name,
            _ => "unknown",
        };
        return shape.Nullable && core != "unknown" ? core + " | null" : core;
    }

    public static string MapRsType(TypeShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var core = shape.Kind switch
        {
            ShapeKind.Primitive => shape.Name switch
            {
                "string" => "String",
                "guid" => "Uuid",
                "datetime" => "DateTime<Utc>",
                "dateonly" => "NaiveDate",
                "timeonly" => "NaiveTime",
                "bool" => "bool",
                "int" => "i32",
                "long" => "i64",
                "short" => "i16",
                "byte" => "u8",
                "sbyte" => "i8",
                "ushort" => "u16",
                "uint" => "u32",
                "ulong" => "u64",
                "float" => "f32",
                "double" or "decimal" => "f64",
                _ => "Value",
            },
            ShapeKind.List => "Vec<" + MapRsType(shape.Args[0]) + ">",
            ShapeKind.Map => "BTreeMap<String, " + MapRsType(shape.Args[0]) + ">",
            ShapeKind.Contract => shape.Args.Count == 0 ? shape.Name : shape.Name + "<" + string.Join(", ", shape.Args.Select(MapRsType)) + ">",
            ShapeKind.TypeParam => shape.Name,
            _ => "Value",
        };
        return shape.Nullable ? "Option<" + core + ">" : core;
    }

    /// <summary>serde <c>with</c> module pinning the C# wire form of a scalar (timestamps, club times, doubles), or null.</summary>
    public static string? RsSerdeWith(TypeShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Kind != ShapeKind.Primitive)
        {
            return null;
        }

        var module = shape.Name switch
        {
            "datetime" => "ts",
            "timeonly" => "hm",
            "double" or "decimal" => "num",
            _ => null,
        };
        return module is null ? null : "crate::wire::" + module + (shape.Nullable ? "_opt" : string.Empty);
    }

    /// <summary>Adds the <c>use</c> paths a shape needs inside Rust module <paramref name="module"/>.</summary>
    public static void CollectRsUses(TypeShape shape, string module, IReadOnlyDictionary<string, string> moduleOfType, ISet<string> uses)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(moduleOfType);
        ArgumentNullException.ThrowIfNull(uses);
        switch (shape.Kind)
        {
            case ShapeKind.Primitive:
                switch (shape.Name)
                {
                    case "guid":
                        uses.Add("uuid::Uuid");
                        break;
                    case "datetime":
                        uses.Add("chrono::DateTime");
                        uses.Add("chrono::Utc");
                        break;
                    case "dateonly":
                        uses.Add("chrono::NaiveDate");
                        break;
                    case "timeonly":
                        uses.Add("chrono::NaiveTime");
                        break;
                    case "json":
                        uses.Add("serde_json::Value");
                        break;
                    default:
                        break;
                }

                break;
            case ShapeKind.Map:
                uses.Add("std::collections::BTreeMap");
                break;
            case ShapeKind.Contract:
                if (moduleOfType.TryGetValue(shape.Name, out var other) && other != module)
                {
                    uses.Add("crate::" + other + "::" + shape.Name);
                }

                break;
            case ShapeKind.Unknown:
                uses.Add("serde_json::Value");
                break;
            default:
                break;
        }

        foreach (var arg in shape.Args)
        {
            CollectRsUses(arg, module, moduleOfType, uses);
        }
    }

    private static string ArrayOf(string item) => item.Contains(" | ", StringComparison.Ordinal) ? "(" + item + ")[]" : item + "[]";
}

/// <summary>Deterministic dependency ordering.</summary>
internal static class Graph
{
    /// <summary>
    /// Kahn's algorithm with a priority queue keyed on the original index, so among the ready nodes the earliest one
    /// always wins. Nodes on a cycle (and everything behind them) are appended in original order.
    /// </summary>
    public static IReadOnlyList<T> TopoSort<T>(IReadOnlyList<T> nodes, Func<T, IEnumerable<T>> dependencies)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(dependencies);
        var index = new Dictionary<T, int>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            index[nodes[i]] = i;
        }

        var indegree = new int[nodes.Count];
        var dependents = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            dependents[i] = [];
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            foreach (var dependency in dependencies(nodes[i]))
            {
                if (index.TryGetValue(dependency, out var j) && j != i)
                {
                    indegree[i]++;
                    dependents[j].Add(i);
                }
            }
        }

        var ready = new PriorityQueue<int, int>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (indegree[i] == 0)
            {
                ready.Enqueue(i, i);
            }
        }

        var result = new List<T>(nodes.Count);
        var emitted = new bool[nodes.Count];
        while (ready.TryDequeue(out var current, out _))
        {
            emitted[current] = true;
            result.Add(nodes[current]);
            foreach (var dependent in dependents[current])
            {
                if (--indegree[dependent] == 0)
                {
                    ready.Enqueue(dependent, dependent);
                }
            }
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            if (!emitted[i])
            {
                result.Add(nodes[i]);
            }
        }

        return result;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Layout: which file each type lands in, and the order inside a file
// ---------------------------------------------------------------------------------------------------------------------

internal static class FileMap
{
    private static readonly string[] NamespaceOrder = ["Errors", "Ipc", "Commands", "Games", "Pcs", "Sessions", "Shop", "Users", "Wallet"];

    private static readonly Dictionary<string, string> TsByNamespace = new(StringComparer.Ordinal)
    {
        ["Errors"] = "commands",
        ["Ipc"] = "commands",
        ["Commands"] = "commands",
        ["Games"] = "games",
        ["Pcs"] = "pc",
        ["Sessions"] = "session",
        ["Shop"] = "shop",
        ["Users"] = "user",
        ["Wallet"] = "wallet",
    };

    private static readonly Dictionary<string, string> RsByNamespace = new(StringComparer.Ordinal)
    {
        ["Errors"] = "error",
        ["Ipc"] = "commands",
        ["Commands"] = "commands",
        ["Games"] = "games",
        ["Pcs"] = "pc",
        ["Sessions"] = "session",
        ["Shop"] = "shop",
        ["Users"] = "user",
        ["Wallet"] = "wallet",
    };

    // Types whose mirror file differs from the namespace default (TS file, Rust module) — matches the hand-written mirrors.
    private static readonly Dictionary<string, (string Ts, string Rs)> Overrides = new(StringComparer.Ordinal)
    {
        // Ipc "Event payloads" region → events in both mirrors.
        ["RemoteControlState"] = ("events", "events"),
        ["ShellCommandKind"] = ("events", "events"),
        ["AdMediaType"] = ("events", "events"),
        ["AdminMessage"] = ("events", "events"),
        ["RemoteControlEvent"] = ("events", "events"),
        ["GameStateChanged"] = ("events", "events"),
        ["PolicyChanged"] = ("events", "events"),
        ["UpdateAvailable"] = ("events", "events"),
        ["UpdateProgress"] = ("events", "events"),
        ["UpdateReady"] = ("events", "events"),
        ["ConnectivityEvent"] = ("events", "events"),
        ["ShellRebootArgs"] = ("events", "events"),
        ["AdItem"] = ("events", "events"),
        ["ShowAdsArgs"] = ("events", "events"),
        ["ShowMessageArgs"] = ("events", "events"),
        ["ShellCommand"] = ("events", "events"),
        ["AuthExpired"] = ("events", "events"),

        // Notifications sit next to the events in TS and with the users in Rust.
        ["NotificationLevel"] = ("events", "user"),
        ["NotificationAction"] = ("events", "user"),
        ["Notification"] = ("events", "user"),

        // Policy sits with the commands in TS and with the PC types in Rust.
        ["AllowlistMode"] = ("commands", "pc"),
        ["ShellReplacementPolicy"] = ("commands", "pc"),
        ["ProcessAllowlistPolicy"] = ("commands", "pc"),
        ["UsbPolicy"] = ("commands", "pc"),
        ["WebFilterPolicy"] = ("commands", "pc"),
        ["ExplorerPolicy"] = ("commands", "pc"),
        ["PowerPolicy"] = ("commands", "pc"),
        ["UpdatesPolicy"] = ("commands", "pc"),
        ["AntiCheatPolicy"] = ("commands", "pc"),
        ["KioskPolicy"] = ("commands", "pc"),
        ["Policy"] = ("commands", "pc"),

        // The Rust crate keeps the envelope and the error types in modules of their own.
        ["IpcKind"] = ("commands", "ipc"),
        ["IpcEnvelope"] = ("commands", "ipc"),
        ["IpcError"] = ("commands", "error"),
        ["ValidationDetails"] = ("commands", "error"),
        ["ReasonDetails"] = ("commands", "error"),
        ["NameDetails"] = ("commands", "error"),
        ["RateLimitDetails"] = ("commands", "error"),
        ["InsufficientFundsDetails"] = ("commands", "error"),
        ["PolicyDeniedDetails"] = ("commands", "error"),
        ["AntiCheatBlockedDetails"] = ("commands", "error"),
        ["LaunchFailedDetails"] = ("commands", "error"),
        ["VersionMismatchDetails"] = ("commands", "error"),
        ["TraceDetails"] = ("commands", "error"),
    };

    // Types whose mirror form is hand-written inside a MANUAL block (generics/typed maps in TS, macros or custom serde in
    // Rust): never emitted, but they keep their file for import resolution. The Rust ipc.rs (envelope + framing) is
    // therefore entirely hand-written and never generated.
    private static readonly HashSet<string> TsManualTypes = new(StringComparer.Ordinal)
    {
        "IpcEnvelope", "ServerCommand", "ServerCommandEnvelope", "CommandAck", "WsAck", "WsFrame", "AgentEvent",
        "SessionEvent", "ShellCommand", "GamesListResponse", "WalletHistoryResponse", "ChatRooms",
    };

    private static readonly HashSet<string> RsManualTypes = new(StringComparer.Ordinal)
    {
        "IpcKind", "IpcEnvelope", "AgentCommand", "Money", "ChatRooms",
    };

    public static bool IsManual(ContractType type, string target) => (target == "ts" ? TsManualTypes : RsManualTypes).Contains(type.Name);

    public static string FileOf(ContractType type, string target)
    {
        if (Overrides.TryGetValue(type.Name, out var location))
        {
            return target == "ts" ? location.Ts : location.Rs;
        }

        var map = target == "ts" ? TsByNamespace : RsByNamespace;
        return map.TryGetValue(type.Namespace, out var file) ? file : type.Namespace.ToLowerInvariant();
    }

    public static int NamespaceIndex(string ns)
    {
        var index = Array.IndexOf(NamespaceOrder, ns);
        return index < 0 ? NamespaceOrder.Length : index;
    }

    /// <summary>Types per file, each file ordered by namespace → dependencies → name.</summary>
    public static SortedDictionary<string, IReadOnlyList<ContractType>> Group(ContractModel model, string target)
    {
        var groups = new SortedDictionary<string, List<ContractType>>(StringComparer.Ordinal);
        foreach (var type in model.Types.Where(t => !IsManual(t, target)))
        {
            var file = FileOf(type, target);
            if (!groups.TryGetValue(file, out var list))
            {
                list = [];
                groups[file] = list;
            }

            list.Add(type);
        }

        var result = new SortedDictionary<string, IReadOnlyList<ContractType>>(StringComparer.Ordinal);
        foreach (var (file, list) in groups)
        {
            var ordered = list
                .OrderBy(t => NamespaceIndex(t.Namespace))
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();
            var byName = ordered.ToDictionary(t => t.Name, StringComparer.Ordinal);
            result[file] = Graph.TopoSort(
                ordered,
                t => t.Dependencies().Select(n => byName.TryGetValue(n, out var dependency) ? dependency : null).OfType<ContractType>());
        }

        return result;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Shared text helpers
// ---------------------------------------------------------------------------------------------------------------------

internal static class FileHeader
{
    public static string Ts(string version) =>
        "// <auto-generated> by tools/ContractsGen from ClubShell.Contracts v" + version + " — edit only inside MANUAL blocks\n";

    public static string Rs(string version) =>
        "//! <auto-generated> by tools/ContractsGen from ClubShell.Contracts v" + version + " — edit only inside MANUAL blocks\n";
}

internal static class ManualSections
{
    public const string BeginMarker = "// ---- BEGIN MANUAL ----";
    public const string EndMarker = "// ---- END MANUAL ----";
    public const string EmptyBlock = BeginMarker + "\n" + EndMarker + "\n";

    /// <summary>
    /// Replaces the empty marker block in <paramref name="generated"/> with every marker block found in
    /// <paramref name="existing"/>, concatenated in file order (a mirror may interleave several blocks with the declarations).
    /// </summary>
    public static string Splice(string generated, string? existing)
    {
        if (existing is null)
        {
            return generated;
        }

        var blocks = Extract(existing);
        if (blocks.Count == 0)
        {
            return generated;
        }

        var at = generated.IndexOf(EmptyBlock, StringComparison.Ordinal);
        return at < 0 ? generated : string.Concat(generated.AsSpan(0, at), string.Join('\n', blocks), generated.AsSpan(at + EmptyBlock.Length));
    }

    /// <summary>Every BEGIN…END block of <paramref name="text"/>, markers included, each ending with its newline.</summary>
    public static IReadOnlyList<string> Extract(string text)
    {
        var blocks = new List<string>();
        var from = 0;
        while (true)
        {
            var begin = text.IndexOf(BeginMarker, from, StringComparison.Ordinal);
            if (begin < 0)
            {
                return blocks;
            }

            var end = text.IndexOf(EndMarker, begin, StringComparison.Ordinal);
            if (end < 0)
            {
                return blocks;
            }

            end += EndMarker.Length;
            if (end < text.Length && text[end] == '\n')
            {
                end++;
            }

            blocks.Add(text[begin..end]);
            from = end;
        }
    }
}

internal static class DocFormat
{
    /// <summary>Greedy word wrap; a line never starts with a Markdown list marker (keeps rustdoc/clippy quiet).</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width && !IsListMarker(word))
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(word);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    public static void JsDoc(StringBuilder sb, string? doc, string indent)
    {
        if (doc is null)
        {
            return;
        }

        var text = doc.Replace("*/", "*\\/", StringComparison.Ordinal);
        if (indent.Length + text.Length + 7 <= 120)
        {
            sb.Append(indent).Append("/** ").Append(text).Append(" */\n");
            return;
        }

        sb.Append(indent).Append("/**\n");
        foreach (var line in Wrap(text, 120 - indent.Length - 3))
        {
            sb.Append(indent).Append(" * ").Append(line).Append('\n');
        }

        sb.Append(indent).Append(" */\n");
    }

    public static void RustDoc(StringBuilder sb, string? doc, string indent)
    {
        if (doc is null)
        {
            return;
        }

        foreach (var line in Wrap(doc, 100 - indent.Length - 4))
        {
            sb.Append(indent).Append("/// ").Append(line).Append('\n');
        }
    }

    private static bool IsListMarker(string word)
    {
        if (word is "-" or "*" or "+")
        {
            return true;
        }

        return word.Length >= 2 && word[^1] is '.' or ')' && word[..^1].All(char.IsDigit);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// TypeScript
// ---------------------------------------------------------------------------------------------------------------------

internal static class TsEmitter
{
    public static IReadOnlyDictionary<string, string> Emit(ContractModel model)
    {
        var groups = FileMap.Group(model, "ts");
        var fileOf = model.Types.ToDictionary(t => t.Name, t => FileMap.FileOf(t, "ts"), StringComparer.Ordinal);
        var typeNames = new HashSet<string>(model.Types.Select(t => t.Name), StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, types) in groups)
        {
            files[file + ".ts"] = EmitFile(model, file, types, fileOf, typeNames);
        }

        files["index.ts"] = EmitIndex(model, groups.Keys);
        return files;
    }

    private static string EmitFile(
        ContractModel model,
        string file,
        IReadOnlyList<ContractType> types,
        Dictionary<string, string> fileOf,
        IReadOnlySet<string> typeNames)
    {
        var sb = new StringBuilder();
        sb.Append(FileHeader.Ts(model.Version)).Append('\n');

        var imports = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var name in types.SelectMany(t => t.Dependencies()).Distinct(StringComparer.Ordinal))
        {
            if (fileOf.TryGetValue(name, out var other) && other != file)
            {
                if (!imports.TryGetValue(other, out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    imports[other] = set;
                }

                set.Add(name);
            }
        }

        // `JsonElement` properties become `JsonObject`, an alias declared once in commands.ts.
        if (file != JsonObjectFile && types.SelectMany(t => t.Properties).Any(p => UsesJson(p.Shape)))
        {
            if (!imports.TryGetValue(JsonObjectFile, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                imports[JsonObjectFile] = set;
            }

            set.Add(TypeMap.TsJsonObject);
        }

        foreach (var (other, names) in imports)
        {
            var line = "import type { " + string.Join(", ", names) + " } from './" + other + ".js';";
            if (line.Length <= 120)
            {
                sb.Append(line).Append('\n');
            }
            else
            {
                sb.Append("import type {\n");
                foreach (var name in names)
                {
                    sb.Append("  ").Append(name).Append(",\n");
                }

                sb.Append("} from './").Append(other).Append(".js';\n");
            }
        }

        if (imports.Count > 0)
        {
            sb.Append('\n');
        }

        if (file == JsonObjectFile)
        {
            sb.Append("/** Opaque JSON object (`JsonElement` in C#). */\n");
            sb.Append("export type ").Append(TypeMap.TsJsonObject).Append(" = Record<string, unknown>;\n\n");
        }

        foreach (var type in types)
        {
            EmitType(sb, type, typeNames);
            sb.Append('\n');
        }

        sb.Append(ManualSections.EmptyBlock);
        return sb.ToString();
    }

    /// <summary>File that declares <see cref="TypeMap.TsJsonObject"/> (the Ipc/Errors/Commands namespaces always land there).</summary>
    private const string JsonObjectFile = "commands";

    private static bool UsesJson(TypeShape shape) =>
        (shape.Kind == ShapeKind.Primitive && shape.Name == "json") || shape.Args.Any(UsesJson);

    private static string EmitIndex(ContractModel model, IEnumerable<string> files)
    {
        var sb = new StringBuilder();
        sb.Append(FileHeader.Ts(model.Version));
        sb.Append("//\n");
        sb.Append("// Hand-written code (helpers, type guards, typed maps) lives between \"BEGIN MANUAL\" / \"END MANUAL\" line comments\n");
        sb.Append("// (exact marker text in tools/ContractsGen/Program.cs); ContractsGen carries every such block over, in order, to the\n");
        sb.Append("// end of the regenerated file. Everything outside the markers is replaced on regeneration.\n\n");
        foreach (var file in files)
        {
            sb.Append("export * from './").Append(file).Append(".js';\n");
        }

        sb.Append('\n').Append(ManualSections.EmptyBlock);
        return sb.ToString();
    }

    private static void EmitType(StringBuilder sb, ContractType type, IReadOnlySet<string> typeNames)
    {
        switch (type.Kind)
        {
            case ContractKind.Enum:
                EmitEnum(sb, type);
                break;
            case ContractKind.Record:
                EmitInterface(sb, type);
                break;
            case ContractKind.Holder:
                if (type.Holder is not null)
                {
                    EmitHolder(sb, type.Holder, typeNames);
                }

                break;
            default:
                break;
        }
    }

    private static void EmitEnum(StringBuilder sb, ContractType type)
    {
        DocFormat.JsDoc(sb, type.Doc, string.Empty);
        sb.Append("export const ").Append(type.Name).Append(" = {\n");
        foreach (var member in type.Members)
        {
            DocFormat.JsDoc(sb, member.Doc, "  ");
            sb.Append("  ").Append(member.Name).Append(": ").Append(Quote(member.Wire)).Append(",\n");
        }

        sb.Append("} as const;\n");
        DocFormat.JsDoc(sb, type.Doc, string.Empty);
        sb.Append("export type ").Append(type.Name).Append(" = (typeof ").Append(type.Name).Append(")[keyof typeof ").Append(type.Name).Append("];\n");
    }

    private static void EmitInterface(StringBuilder sb, ContractType type)
    {
        DocFormat.JsDoc(sb, type.Doc, string.Empty);
        sb.Append("export interface ").Append(type.Name);
        if (type.TypeParams.Count > 0)
        {
            sb.Append('<').Append(string.Join(", ", type.TypeParams)).Append('>');
        }

        sb.Append(" {\n");
        foreach (var property in type.Properties)
        {
            DocFormat.JsDoc(sb, property.Doc, "  ");
            sb.Append("  ").Append(PropertyKey(property.JsonName)).Append(property.Optional ? "?: " : ": ").Append(TypeMap.MapTsType(property.Shape)).Append(";\n");
        }

        sb.Append("}\n");
        foreach (var constant in type.Constants)
        {
            DocFormat.JsDoc(sb, constant.Doc, string.Empty);
            sb.Append("export const ").Append(ConstantName(type.Name, constant.Name)).Append(" = ").Append(Literal(constant.Value)).Append(";\n");
        }
    }

    // Record constants become module-level consts; these are the names the mirror (and the Shell) already use.
    // Anything not listed is emitted as TYPE_NAME_CONSTANT.
    private static readonly Dictionary<string, string> ConstantNames = new(StringComparer.Ordinal)
    {
        ["Money.DefaultCurrency"] = "DEFAULT_CURRENCY",
        ["TopupIntentCreateRequest.MinAmountMinor"] = "TOPUP_MIN_AMOUNT_MINOR",
        ["GamesListRequest.DefaultPageSize"] = "GAMES_LIST_DEFAULT_PAGE_SIZE",
        ["GamesListRequest.MaxPageSize"] = "GAMES_LIST_MAX_PAGE_SIZE",
        ["ChatHistoryRequest.DefaultLimit"] = "CHAT_HISTORY_DEFAULT_LIMIT",
        ["ChatHistoryRequest.MaxLimit"] = "CHAT_HISTORY_MAX_LIMIT",
        ["TelemetryBatch.MaxSamples"] = "TELEMETRY_MAX_SAMPLES",
        ["TelemetryBatch.MaxLogLines"] = "TELEMETRY_MAX_LOG_LINES",
        ["Session.OpenEnded"] = "SESSION_OPEN_ENDED",
        ["SessionEventsBatch.MaxEvents"] = "SESSION_EVENTS_MAX",
        ["OrderLineRequest.MaxQty"] = "ORDER_MAX_QTY",
        ["OrderLineRequest.MaxLines"] = "ORDER_MAX_LINES",
    };

    private static string ConstantName(string typeName, string constantName) =>
        ConstantNames.TryGetValue(typeName + "." + constantName, out var name)
            ? name
            : Naming.ToScreamingSnake(typeName) + "_" + Naming.ToScreamingSnake(constantName);

    private static void EmitHolder(StringBuilder sb, Holder holder, IReadOnlySet<string> typeNames)
    {
        var name = HolderName(holder.Name);
        DocFormat.JsDoc(sb, holder.Doc, string.Empty);
        sb.Append("export const ").Append(name).Append(" = {\n");
        EmitHolderMembers(sb, holder, "  ");
        sb.Append("} as const;\n");
        if (holder.Nested.Count == 0)
        {
            var singular = Naming.Singular(holder.Name);
            if (typeNames.Contains(singular))
            {
                singular += "Value";
            }

            sb.Append("/** One of the {@link ").Append(name).Append("} values. */\n");
            sb.Append("export type ").Append(singular).Append(" = (typeof ").Append(name).Append(")[keyof typeof ").Append(name).Append("];\n");
        }
    }

    private static void EmitHolderMembers(StringBuilder sb, Holder holder, string indent)
    {
        foreach (var constant in holder.Constants)
        {
            DocFormat.JsDoc(sb, constant.Doc, indent);
            sb.Append(indent).Append(constant.Name).Append(": ").Append(Literal(constant.Value)).Append(",\n");
        }

        foreach (var nested in holder.Nested)
        {
            DocFormat.JsDoc(sb, nested.Doc, indent);
            sb.Append(indent).Append(nested.Name).Append(": {\n");
            EmitHolderMembers(sb, nested, indent + "  ");
            sb.Append(indent).Append("},\n");
        }
    }

    /// <summary><c>IpcMessages</c> is exported as <c>IpcNames</c> (the name the Shell code already uses).</summary>
    private static string HolderName(string name) => name == "IpcMessages" ? "IpcNames" : name;

    private static string PropertyKey(string jsonName)
    {
        var plain = jsonName.Length > 0 && !char.IsDigit(jsonName[0]) && jsonName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$');
        return plain ? jsonName : Quote(jsonName);
    }

    private static string Literal(object value) => value switch
    {
        string s => Quote(s),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Quote(string text) =>
        "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "'";
}

// ---------------------------------------------------------------------------------------------------------------------
// Rust
// ---------------------------------------------------------------------------------------------------------------------

internal static class RsEmitter
{
    public static IReadOnlyDictionary<string, string> Emit(ContractModel model)
    {
        var groups = FileMap.Group(model, "rs");
        var moduleOf = model.Types.ToDictionary(t => t.Name, t => FileMap.FileOf(t, "rs"), StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (module, types) in groups)
        {
            files[module + ".rs"] = EmitModule(model, module, types, moduleOf);
        }

        // lib.rs (crate docs, protocol constants, the `wire_enum!` macro, the `wire` serde helpers and the prelude) is
        // hand-written; add a `pub mod` line there when a new module appears.
        return files;
    }

    private static string EmitModule(ContractModel model, string module, IReadOnlyList<ContractType> types, IReadOnlyDictionary<string, string> moduleOf)
    {
        var uses = new SortedSet<string>(StringComparer.Ordinal);
        if (types.Any(t => t.Kind == ContractKind.Record))
        {
            uses.Add("serde::Deserialize");
            uses.Add("serde::Serialize");
        }

        foreach (var type in types)
        {
            foreach (var property in type.Properties)
            {
                TypeMap.CollectRsUses(property.Shape, module, moduleOf, uses);
            }
        }

        var sb = new StringBuilder();
        sb.Append(FileHeader.Rs(model.Version)).Append('\n');
        EmitUses(sb, uses);
        foreach (var type in types)
        {
            EmitItem(sb, type, model);
            sb.Append('\n');
        }

        sb.Append(ManualSections.EmptyBlock);
        return sb.ToString();
    }

    private static void EmitUses(StringBuilder sb, SortedSet<string> uses)
    {
        var groups = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var use in uses)
        {
            var split = use.LastIndexOf("::", StringComparison.Ordinal);
            var path = use[..split];
            var item = use[(split + 2)..];
            if (!groups.TryGetValue(path, out var items))
            {
                items = new SortedSet<string>(StringComparer.Ordinal);
                groups[path] = items;
            }

            items.Add(item);
        }

        var sections = new[]
        {
            groups.Where(g => g.Key.StartsWith("std", StringComparison.Ordinal)),
            groups.Where(g => !g.Key.StartsWith("std", StringComparison.Ordinal) && !g.Key.StartsWith("crate", StringComparison.Ordinal)),
            groups.Where(g => g.Key.StartsWith("crate", StringComparison.Ordinal)),
        };
        foreach (var section in sections)
        {
            var any = false;
            foreach (var (path, items) in section)
            {
                any = true;
                sb.Append(UseLine(path, items));
            }

            if (any)
            {
                sb.Append('\n');
            }
        }
    }

    private static string UseLine(string path, SortedSet<string> items)
    {
        if (items.Count == 1)
        {
            return "use " + path + "::" + items.First() + ";\n";
        }

        var line = "use " + path + "::{" + string.Join(", ", items) + "};\n";
        if (line.Length <= 100)
        {
            return line;
        }

        // rustfmt "Mixed" layout: fill each line up to the width.
        var sb = new StringBuilder();
        sb.Append("use ").Append(path).Append("::{\n");
        var current = new StringBuilder();
        foreach (var item in items)
        {
            var piece = item + ",";
            if (current.Length > 0 && 4 + current.Length + 1 + piece.Length > 100)
            {
                sb.Append("    ").Append(current).Append('\n');
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(piece);
        }

        if (current.Length > 0)
        {
            sb.Append("    ").Append(current).Append('\n');
        }

        sb.Append("};\n");
        return sb.ToString();
    }

    private static void EmitItem(StringBuilder sb, ContractType type, ContractModel model)
    {
        switch (type.Kind)
        {
            case ContractKind.Enum:
                EmitEnum(sb, type);
                break;
            case ContractKind.Record:
                EmitStruct(sb, type, model);
                break;
            case ContractKind.Holder:
                if (type.Holder is not null)
                {
                    EmitHolderModule(sb, type.Holder, string.Empty);
                }

                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Enums use the crate's <c>wire_enum!</c> macro (lib.rs): exact wire literals, <c>ALL</c>, <c>wire_name()</c>,
    /// <c>parse()</c>, <c>Display</c> and <c>FromStr</c>.
    /// </summary>
    private static void EmitEnum(StringBuilder sb, ContractType type)
    {
        sb.Append("wire_enum! {\n");
        DocFormat.RustDoc(sb, type.Doc, "    ");
        sb.Append("    ").Append(type.Name).Append(" {\n");
        foreach (var member in type.Members)
        {
            DocFormat.RustDoc(sb, member.Doc, "        ");
            sb.Append("        ").Append(member.Name).Append(" = ").Append(RustString(member.Wire)).Append(",\n");
        }

        sb.Append("    }\n}\n");
    }

    private static void EmitStruct(StringBuilder sb, ContractType type, ContractModel model)
    {
        var generics = type.TypeParams.Count == 0 ? string.Empty : "<" + string.Join(", ", type.TypeParams) + ">";
        DocFormat.RustDoc(sb, type.Doc, string.Empty);
        sb.Append("#[derive(Serialize, Deserialize, Clone, Debug, PartialEq").Append(IsEq(type, model, new HashSet<string>(StringComparer.Ordinal)) ? ", Eq" : string.Empty).Append(")]\n");
        sb.Append("#[serde(rename_all = \"camelCase\")]\n");
        sb.Append("pub struct ").Append(type.Name).Append(generics).Append(" {\n");
        foreach (var property in type.Properties)
        {
            DocFormat.RustDoc(sb, property.Doc, "    ");
            var field = Naming.RustField(property.Name);
            var serdeName = Naming.SerdeCamelOfSnake(field.StartsWith("r#", StringComparison.Ordinal) ? field[2..] : field);
            var attributes = new List<string>();
            if (serdeName != property.JsonName)
            {
                attributes.Add("rename = " + RustString(property.JsonName));
            }

            var with = TypeMap.RsSerdeWith(property.Shape);
            if (with is not null)
            {
                // A `with` module disables serde's implicit None-for-missing on Option fields, hence `default`.
                if (property.Shape.Nullable)
                {
                    attributes.Add("default");
                }

                attributes.Add("with = \"" + with + "\"");
            }

            if (property.Optional)
            {
                attributes.Add("skip_serializing_if = \"Option::is_none\"");
            }

            if (attributes.Count > 0)
            {
                sb.Append("    #[serde(").Append(string.Join(", ", attributes)).Append(")]\n");
            }

            sb.Append("    pub ").Append(field).Append(": ").Append(TypeMap.MapRsType(property.Shape)).Append(",\n");
        }

        sb.Append("}\n");
        if (type.Constants.Count == 0)
        {
            return;
        }

        sb.Append("\nimpl").Append(generics).Append(' ').Append(type.Name).Append(generics).Append(" {\n");
        foreach (var constant in type.Constants)
        {
            DocFormat.RustDoc(sb, constant.Doc, "    ");
            var constType = UsizeConstants.Contains(type.Name + "." + constant.Name) ? "usize" : ConstType(constant.Value);
            sb.Append("    pub const ").Append(Naming.ToScreamingSnake(constant.Name)).Append(": ").Append(constType)
                .Append(" = ").Append(Literal(constant.Value)).Append(";\n");
        }

        sb.Append("}\n");
    }

    // C# `int` limits that Rust code compares with lengths / byte counts; every other integer constant stays i32.
    private static readonly HashSet<string> UsizeConstants = new(StringComparer.Ordinal)
    {
        "SessionEventsBatch.MaxEvents", "OrderLineRequest.MaxLines", "TelemetryBatch.MaxSamples", "TelemetryBatch.MaxLogLines",
        "WsFrame.MaxFrameBytes",
    };

    private static void EmitHolderModule(StringBuilder sb, Holder holder, string indent)
    {
        var inner = indent + "    ";
        DocFormat.RustDoc(sb, holder.Doc, indent);
        sb.Append(indent).Append("pub mod ").Append(ModuleName(holder.Name)).Append(" {\n");
        foreach (var constant in holder.Constants)
        {
            DocFormat.RustDoc(sb, constant.Doc, inner);
            sb.Append(inner).Append("pub const ").Append(Naming.ToScreamingSnake(constant.Name)).Append(": ").Append(ConstType(constant.Value))
                .Append(" = ").Append(Literal(constant.Value)).Append(";\n");
        }

        if (holder.All)
        {
            sb.Append('\n').Append(inner).Append("/// All values.\n");
            sb.Append(inner).Append("pub const ALL: &[&str] = &[\n");
            foreach (var constant in holder.Constants.Where(c => c.Value is string))
            {
                sb.Append(inner).Append("    ").Append(Naming.ToScreamingSnake(constant.Name)).Append(",\n");
            }

            sb.Append(inner).Append("];\n");
        }

        var any = holder.Constants.Count > 0;
        foreach (var nested in holder.Nested)
        {
            if (any)
            {
                sb.Append('\n');
            }

            any = true;
            EmitHolderModule(sb, nested, inner);
        }

        sb.Append(indent).Append("}\n");
    }

    /// <summary><c>IpcMessages</c> becomes the <c>names</c> module (the path the Agent/Shell code already uses).</summary>
    private static string ModuleName(string holderName) => holderName == "IpcMessages" ? "names" : Naming.ToSnakeCase(holderName);

    /// <summary><c>Eq</c> is derivable when no field (transitively) holds a float or an opaque JSON value.</summary>
    private static bool IsEq(ContractType type, ContractModel model, HashSet<string> visiting)
    {
        if (type.Kind == ContractKind.Enum)
        {
            return true;
        }

        if (!visiting.Add(type.Name))
        {
            return false;
        }

        var eq = type.Properties.All(p => IsEq(p.Shape, model, visiting));
        visiting.Remove(type.Name);
        return eq;
    }

    private static bool IsEq(TypeShape shape, ContractModel model, HashSet<string> visiting)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Primitive:
                return shape.Name is not ("double" or "float" or "decimal" or "json");
            case ShapeKind.List:
            case ShapeKind.Map:
                return IsEq(shape.Args[0], model, visiting);
            case ShapeKind.Contract:
                var target = model.Find(shape.Name);
                return (target is null || IsEq(target, model, visiting)) && shape.Args.All(a => IsEq(a, model, visiting));
            case ShapeKind.TypeParam:
                return true;
            default:
                return false;
        }
    }

    private static string ConstType(object value) => value switch
    {
        string => "&str",
        bool => "bool",
        int => "i32",
        long => "i64",
        double => "f64",
        _ => "i64",
    };

    private static string Literal(object value)
    {
        switch (value)
        {
            case string s:
                return RustString(s);
            case bool b:
                return b ? "true" : "false";
            case double d:
                var text = d.ToString("R", CultureInfo.InvariantCulture);
                return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.OrdinalIgnoreCase) ? text : text + ".0";
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    private static string RustString(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}

// ---------------------------------------------------------------------------------------------------------------------
// Output
// ---------------------------------------------------------------------------------------------------------------------

internal static class OutputWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static int Run(IReadOnlyDictionary<string, string> files, CliOptions options)
    {
        var outOfDate = 0;
        foreach (var (name, generated) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var path = Path.Combine(options.OutDir, name);
            var existing = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
            var content = ManualSections.Splice(generated, existing);

            if (options.Check)
            {
                if (existing is null)
                {
                    Console.Error.WriteLine($"missing: {path}");
                    outOfDate++;
                }
                else if (!string.Equals(existing, content, StringComparison.Ordinal))
                {
                    outOfDate++;
                    PrintDiff(path, existing, content);
                }
                else if (options.Verbose)
                {
                    Console.WriteLine($"up to date: {path}");
                }
            }
            else if (!string.Equals(existing, content, StringComparison.Ordinal))
            {
                Directory.CreateDirectory(options.OutDir);
                File.WriteAllText(path, content, Utf8NoBom);
                if (options.Verbose)
                {
                    Console.WriteLine($"wrote: {path}");
                }
            }
            else if (options.Verbose)
            {
                Console.WriteLine($"unchanged: {path}");
            }
        }

        if (options.Check && outOfDate > 0)
        {
            Console.Error.WriteLine($"{outOfDate} file(s) out of date; run: ContractsGen --target {options.Target} --out {options.OutDir}");
            return 1;
        }

        return 0;
    }

    private static void PrintDiff(string path, string existing, string generated)
    {
        Console.Error.WriteLine($"out of date: {path}");
        var left = existing.Split('\n');
        var right = generated.Split('\n');
        var shown = 0;
        for (var i = 0; i < Math.Max(left.Length, right.Length) && shown < 40; i++)
        {
            var a = i < left.Length ? left[i] : null;
            var b = i < right.Length ? right[i] : null;
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                continue;
            }

            shown++;
            if (a is not null)
            {
                Console.Error.WriteLine($"  -{i + 1}: {a}");
            }

            if (b is not null)
            {
                Console.Error.WriteLine($"  +{i + 1}: {b}");
            }
        }
    }
}
