using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace ShellLang;

public sealed record ShellError(string Message);
public sealed record EmptyCollectionError(string Message = "The collection is empty.");
public sealed record CollectionCardinalityError(int ActualCount, string Message);

public sealed class CoreTypeCatalog
{
    internal CoreTypeCatalog()
    {
    }
    public ShellTypeId Any
    {
        get; internal init;
    }
    public ShellTypeId Void
    {
        get; internal init;
    }
    public ShellTypeId Bool
    {
        get; internal init;
    }
    public ShellTypeId Int32
    {
        get; internal init;
    }
    public ShellTypeId Int64
    {
        get; internal init;
    }
    public ShellTypeId UInt32
    {
        get; internal init;
    }
    public ShellTypeId UInt64
    {
        get; internal init;
    }
    public ShellTypeId Float32
    {
        get; internal init;
    }
    public ShellTypeId Float64
    {
        get; internal init;
    }
    public ShellTypeId String
    {
        get; internal init;
    }
    public ShellTypeId Error
    {
        get; internal init;
    }
    public ShellTypeId EmptyCollectionError
    {
        get; internal init;
    }
    public ShellTypeId CollectionCardinalityError
    {
        get; internal init;
    }
}

internal enum ShellTypeKind
{
    Core, Host, Enum, Error, Array, Result, OutputRecord, ReservedStream
}

internal sealed class TypeEntry
{
    public required ShellTypeId Id
    {
        get; init;
    }
    public required string Name
    {
        get; set;
    }
    public required string Description
    {
        get; init;
    }
    public required ShellTypeKind Kind
    {
        get; init;
    }
    public Type? ClrType
    {
        get; init;
    }
    public ValueAdapter? Adapter
    {
        get; init;
    }
    public IReadOnlyList<ShellTypeId> Bases { get; init; } = Array.Empty<ShellTypeId>();
    public IReadOnlyList<MemberDescriptor> Members { get; init; } = Array.Empty<MemberDescriptor>();
    public IReadOnlyList<QueryDescriptor> Queries { get; init; } = Array.Empty<QueryDescriptor>();
    public IReadOnlyList<EnumMemberDescriptor> EnumMembers { get; init; } = Array.Empty<EnumMemberDescriptor>();
    public EqualityDescriptor? Equality
    {
        get; init;
    }
    public OrderingDescriptor? Ordering
    {
        get; init;
    }
    public ShellTypeId? ElementType
    {
        get; init;
    }
    public ShellTypeId? SuccessType
    {
        get; init;
    }
    public ShellTypeId? ErrorType
    {
        get; init;
    }
    public IReadOnlyDictionary<string, ShellTypeId>? OutputFields
    {
        get; init;
    }
    public string? DefaultOutput
    {
        get; init;
    }
}

public sealed class DescriptorCatalog
{
    private readonly ShellEngine _engine;
    internal DescriptorCatalog(ShellEngine engine) => _engine = engine;
    public CoreTypeCatalog Core => _engine.Core;
    public IReadOnlyList<TypeDescriptor> Types => _engine.Types.ToArray();
    public IReadOnlyList<EnumTypeDescriptor> Enums => _engine.Enums.ToArray();
    public IReadOnlyList<ErrorTypeDescriptor> Errors => _engine.Errors.ToArray();
    public IReadOnlyList<GlobalDescriptor> Globals => _engine.Globals.Values.OrderBy(static x => x.Name).ToArray();
    public IReadOnlyList<CommandDescriptor> Commands => _engine.Commands.Values.OrderBy(static x => x.Name).ToArray();
    public IReadOnlyList<RuntimeFaultDescriptor> RuntimeFaults => _engine.RuntimeFaults.Values.OrderBy(static x => x.Code.Value).ToArray();
    public IReadOnlyList<IntrinsicDescriptor> Intrinsics => _engine.Intrinsics.Values.OrderBy(static x => x.Name).ToArray();
    public string GetTypeName(ShellTypeId type) => _engine.GetTypeEntry(type).Name;
    public ShellTypeId ArrayOf(ShellTypeId element) => _engine.ArrayOf(element);
    public ShellTypeId ResultOf(ShellTypeId success, ShellTypeId error) => _engine.ResultOf(success, error);
    public bool IsAssignable(ShellTypeId actual, ShellTypeId expected) => _engine.IsAssignable(actual, expected);
}

public sealed partial class ShellEngine
{
    private static readonly Regex IdentifierPattern = IdentifierRegex();
    private static readonly Regex FaultPattern = FaultRegex();
    private readonly Dictionary<ShellTypeId, TypeEntry> _typeEntries = [];
    private readonly Dictionary<string, TypeEntry> _typesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<(ShellTypeId, ShellTypeId), ShellTypeId> _resultTypes = [];
    private readonly Dictionary<ShellTypeId, ShellTypeId> _arrayTypes = [];
    private readonly Dictionary<SymbolId, object> _symbols = [];
    private int _nextSymbol;

    internal List<TypeDescriptor> Types { get; } = [];
    internal List<EnumTypeDescriptor> Enums { get; } = [];
    internal List<ErrorTypeDescriptor> Errors { get; } = [];
    internal Dictionary<string, GlobalDescriptor> Globals { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, CommandDescriptor> Commands { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, RuntimeFaultDescriptor> RuntimeFaults { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, IntrinsicDescriptor> Intrinsics { get; } = new(StringComparer.Ordinal);

    private SymbolId NextSymbol(object value)
    {
        var id = new SymbolId(Interlocked.Increment(ref _nextSymbol));
        _symbols.Add(id, value);
        return id;
    }

    private void InitializeCoreTypes()
    {
        ShellTypeId Add<T>(string name, ShellTypeKind kind = ShellTypeKind.Core,
            ShellTypeId? baseType = null, EqualityDescriptor? equality = null, OrderingDescriptor? ordering = null) where T : notnull
        {
            var id = IdentitySource.NextType();
            var adapter = new ValueAdapter<T> { TypeId = id };
            var entry = new TypeEntry
            {
                Id = id,
                Name = name,
                Description = $"Core {name} type.",
                Kind = kind,
                ClrType = typeof(T),
                Adapter = adapter,
                Bases = baseType is null ? [] : [baseType.Value],
                Equality = equality,
                Ordering = ordering
            };
            _typeEntries.Add(id, entry);
            _typesByName.Add(name, entry);
            return id;
        }

        var any = Add<object>("Any");
        var voidType = IdentitySource.NextType();
        AddEntry(new TypeEntry { Id = voidType, Name = "Void", Description = "Terminal absence of a value.", Kind = ShellTypeKind.Core });
        var boolean = Add<bool>("Bool", equality: new((a, b) => (bool)a == (bool)b));
        var int32 = Add<int>("Int32", equality: new((a, b) => (int)a == (int)b), ordering: new((a, b) => ((int)a).CompareTo((int)b)));
        var int64 = Add<long>("Int64", equality: new((a, b) => (long)a == (long)b), ordering: new((a, b) => ((long)a).CompareTo((long)b)));
        var uint32 = Add<uint>("UInt32", equality: new((a, b) => (uint)a == (uint)b), ordering: new((a, b) => ((uint)a).CompareTo((uint)b)));
        var uint64 = Add<ulong>("UInt64", equality: new((a, b) => (ulong)a == (ulong)b), ordering: new((a, b) => ((ulong)a).CompareTo((ulong)b)));
        var float32 = Add<float>("Float32", equality: new((a, b) => (float)a == (float)b), ordering: new((a, b) => ((float)a).CompareTo((float)b)));
        var float64 = Add<double>("Float64", equality: new((a, b) => (double)a == (double)b), ordering: new((a, b) => ((double)a).CompareTo((double)b)));
        var str = Add<string>("String", equality: new((a, b) => StringComparer.Ordinal.Equals(a, b)), ordering: new((a, b) => StringComparer.Ordinal.Compare((string)a, (string)b)));
        var error = Add<ShellError>("Error", ShellTypeKind.Error);
        var empty = Add<EmptyCollectionError>("EmptyCollectionError", ShellTypeKind.Error, error);
        var cardinality = Add<CollectionCardinalityError>("CollectionCardinalityError", ShellTypeKind.Error, error);
        Core = new CoreTypeCatalog
        {
            Any = any,
            Void = voidType,
            Bool = boolean,
            Int32 = int32,
            Int64 = int64,
            UInt32 = uint32,
            UInt64 = uint64,
            Float32 = float32,
            Float64 = float64,
            String = str,
            Error = error,
            EmptyCollectionError = empty,
            CollectionCardinalityError = cardinality
        };
        foreach (var name in IntrinsicNames)
        {
            var placeholder = new IntrinsicDescriptor(default, name, IntrinsicDescriptions[name]);
            var id = NextSymbol(placeholder);
            var descriptor = new IntrinsicDescriptor(id, name, placeholder.Description);
            _symbols[id] = descriptor;
            Intrinsics.Add(name, descriptor);
        }
    }

    private void AddEntry(TypeEntry entry)
    {
        _typeEntries.Add(entry.Id, entry);
        _typesByName.Add(entry.Name, entry);
    }

    internal TypeEntry GetTypeEntry(ShellTypeId id) => _typeEntries.TryGetValue(id, out var entry)
        ? entry : throw new KeyNotFoundException($"Unknown Shell type id {id}.");
    internal bool TryGetType(string name, out TypeEntry entry) => _typesByName.TryGetValue(name, out entry!);

    internal ShellTypeId ArrayOf(ShellTypeId element)
    {
        if (element == Core.Void)
            throw new ArgumentException("Array<Void> is not valid.", nameof(element));
        if (_arrayTypes.TryGetValue(element, out var existing))
            return existing;
        var id = IdentitySource.NextType();
        var entry = new TypeEntry
        {
            Id = id,
            Name = $"Array<#{element.Value}>",
            Description = "Immutable array.",
            Kind = ShellTypeKind.Array,
            ClrType = typeof(ShellArrayValue),
            ElementType = element
        };
        _typeEntries.Add(id, entry);
        _arrayTypes.Add(element, id);
        return id;
    }

    internal ShellTypeId ResultOf(ShellTypeId success, ShellTypeId error)
    {
        _ = GetTypeEntry(success);
        if (!IsAssignable(error, Core.Error))
            throw new ArgumentException("Result error type must derive from Error.", nameof(error));
        if (_resultTypes.TryGetValue((success, error), out var existing))
            return existing;
        var id = IdentitySource.NextType();
        var entry = new TypeEntry
        {
            Id = id,
            Name = $"Result<{GetTypeEntry(success).Name},{GetTypeEntry(error).Name}>",
            Description = "Typed result.",
            Kind = ShellTypeKind.Result,
            ClrType = typeof(ShellResultValue),
            SuccessType = success,
            ErrorType = error
        };
        _typeEntries.Add(id, entry);
        _resultTypes.Add((success, error), id);
        return id;
    }

    internal bool IsAssignable(ShellTypeId actual, ShellTypeId expected)
    {
        if (actual == expected || expected == Core.Any)
            return actual != Core.Void;
        var a = GetTypeEntry(actual);
        var e = GetTypeEntry(expected);
        if (a.Kind == ShellTypeKind.Array && e.Kind == ShellTypeKind.Array)
            return IsAssignable(a.ElementType!.Value, e.ElementType!.Value);
        if (a.Kind == ShellTypeKind.Result && e.Kind == ShellTypeKind.Result)
            return IsAssignable(a.SuccessType!.Value, e.SuccessType!.Value) && IsAssignable(a.ErrorType!.Value, e.ErrorType!.Value);
        var visited = new HashSet<ShellTypeId>();
        var queue = new Queue<ShellTypeId>(a.Bases);
        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current))
                continue;
            if (current == expected)
                return true;
            foreach (var parent in GetTypeEntry(current).Bases)
                queue.Enqueue(parent);
        }
        return false;
    }

    internal ShellTypeId CommonError(ShellTypeId left, ShellTypeId right)
    {
        var ancestors = new HashSet<ShellTypeId>();
        for (var current = left; ;)
        {
            ancestors.Add(current);
            var bases = GetTypeEntry(current).Bases;
            if (bases.Count == 0)
                break;
            current = bases[0];
        }
        for (var current = right; ;)
        {
            if (ancestors.Contains(current))
                return current;
            var bases = GetTypeEntry(current).Bases;
            if (bases.Count == 0)
                return Core.Error;
            current = bases[0];
        }
    }

    internal TypeEntry? FindMemberOwner(ShellTypeId receiver, string name, out MemberDescriptor? member, out QueryDescriptor? query)
    {
        member = null;
        query = null;
        var queue = new Queue<ShellTypeId>();
        queue.Enqueue(receiver);
        var seen = new HashSet<ShellTypeId>();
        while (queue.TryDequeue(out var id))
        {
            if (!seen.Add(id))
                continue;
            var entry = GetTypeEntry(id);
            member = entry.Members.FirstOrDefault(x => x.Name == name);
            query = entry.Queries.FirstOrDefault(x => x.Name == name);
            if (member is not null || query is not null)
                return entry;
            foreach (var parent in entry.Bases)
                queue.Enqueue(parent);
        }
        return null;
    }

    private List<HostingDiagnostic> Validate(DescriptorSet set)
    {
        var d = new List<HostingDiagnostic>();
        var newTypeNames = new HashSet<string>(StringComparer.Ordinal);
        void Name(string name, string kind)
        {
            if (!IdentifierPattern.IsMatch(name))
                d.Add(new("SL3001", $"Invalid {kind} name '{name}'."));
        }
        foreach (var item in set.Types.Cast<object>().Concat(set.Enums).Concat(set.Errors))
        {
            var name = item switch
            {
                TypeDescriptor x => x.Name,
                EnumTypeDescriptor x => x.Name,
                ErrorTypeDescriptor x => x.Name,
                _ => ""
            };
            Name(name, "type");
            if (name == "Stream" || name.StartsWith("Stream_", StringComparison.Ordinal))
                d.Add(new("SL3016", "Stream<T> is reserved and cannot be registered in 0.1."));
            if (_typesByName.ContainsKey(name) || !newTypeNames.Add(name))
                d.Add(new("SL3002", $"Duplicate type '{name}'."));
        }
        var available = _typeEntries.Keys.Concat(set.Types.Select(x => x.Id)).Concat(set.Enums.Select(x => x.Id)).Concat(set.Errors.Select(x => x.Id))
            .Concat(set.Commands.Where(x => x.OutputRecordType is not null).Select(x => x.OutputRecordType!.Value)).ToHashSet();
        var newErrorIds = set.Errors.Select(x => x.Id).ToHashSet();
        foreach (var type in set.Types)
        {
            if (string.IsNullOrWhiteSpace(type.Description))
                d.Add(new("SL3003", $"Type '{type.Name}' needs a description."));
            if (type.Adapter is null || type.ClrType != type.Adapter.ClrType)
                d.Add(new("SL3004", $"Type '{type.Name}' has an invalid adapter."));
            foreach (var b in type.DirectBases)
            if (!available.Contains(b))
                d.Add(new("SL3005", $"Type '{type.Name}' has unknown base {b}."));
            ValidateLocalNames(type.Name, type.Members.Select(x => x.Name).Concat(type.Queries.Select(x => x.Name)), d);
            foreach (var member in type.Members)
            {
                Name(member.Name, "member");
                if (string.IsNullOrWhiteSpace(member.Description))
                    d.Add(new("SL3003", $"Member '{type.Name}.{member.Name}' needs a description."));
                if (!IsKnownTypeReference(member.ValueType, available))
                    d.Add(new("SL3005", $"Member '{type.Name}.{member.Name}' has unknown type."));
            }
            foreach (var query in type.Queries)
            {
                Name(query.Name, "query");
                if (string.IsNullOrWhiteSpace(query.Description))
                    d.Add(new("SL3003", $"Query '{type.Name}.{query.Name}' needs a description."));
                if (!IsKnownTypeReference(query.OutputType, available))
                    d.Add(new("SL3005", $"Query '{type.Name}.{query.Name}' has unknown output type."));
                ValidateLocalNames($"{type.Name}.{query.Name}", query.Arguments.Select(x => x.Name), d);
                foreach (var argument in query.Arguments)
                {
                    Name(argument.Name, "argument");
                    if (!IsKnownTypeReference(argument.Type, available))
                        d.Add(new("SL3005", $"Query argument '{type.Name}.{query.Name}.{argument.Name}' has unknown type."));
                    if ((!argument.Required && argument.DefaultValue is null) || (argument.DefaultValue is not null && argument.DefaultValue.Type != argument.Type))
                        d.Add(new("SL3010", $"Query argument '{type.Name}.{query.Name}.{argument.Name}' has an invalid default."));
                }
                if (query.Arguments.Select(x => x.Position).Distinct().Count() != query.Arguments.Count || query.Arguments.Any(x => x.Position < 0))
                    d.Add(new("SL3020", $"Query '{type.Name}.{query.Name}' has invalid positional argument indices."));
                if (query.ErrorType is { } queryError && !(newErrorIds.Contains(queryError) || (_typeEntries.TryGetValue(queryError, out var queryErrorEntry) && queryErrorEntry.Kind == ShellTypeKind.Error)))
                    d.Add(new("SL3017", $"Query '{type.Name}.{query.Name}' has a non-error ErrorType."));
            }
        }
        foreach (var type in set.Enums)
        {
            if (type.Members.Count == 0)
                d.Add(new("SL3006", $"Enum '{type.Name}' has no members."));
            ValidateLocalNames(type.Name, type.Members.Select(x => x.Name), d);
        }
        foreach (var error in set.Errors)
        {
            var knownError = newErrorIds.Contains(error.BaseType) || (_typeEntries.TryGetValue(error.BaseType, out var baseEntry) && baseEntry.Kind == ShellTypeKind.Error);
            if (!knownError)
                d.Add(new("SL3017", $"Error '{error.Name}' must have one error base rooted at Error."));
        }
        ValidateTypeCycles(set.Types, d);
        ValidateErrorCycles(set.Errors, d);
        var commandNames = new HashSet<string>(Commands.Keys, StringComparer.Ordinal);
        foreach (var command in set.Commands)
        {
            Name(command.Name, "command");
            if (!commandNames.Add(command.Name) || IntrinsicNames.Contains(command.Name))
                d.Add(new("SL3007", $"Duplicate or reserved command '{command.Name}'."));
            if (string.IsNullOrWhiteSpace(command.Description))
                d.Add(new("SL3003", $"Command '{command.Name}' needs a description."));
            if (command.Inputs.Count(x => x.IsDefault) > 1)
                d.Add(new("SL3008", $"Command '{command.Name}' has more than one default input."));
            if (command.Outputs.Count(x => x.IsDefault) > 1)
                d.Add(new("SL3009", $"Command '{command.Name}' has more than one default output."));
            ValidateLocalNames(command.Name, command.Inputs.Select(x => x.Name).Concat(command.Arguments.Select(x => x.Name)), d);
            ValidateLocalNames(command.Name, command.Outputs.Select(x => x.Name), d);
            foreach (var port in command.Inputs)
            {
                Name(port.Name, "input");
                if (string.IsNullOrWhiteSpace(port.Description))
                    d.Add(new("SL3003", $"Input '{command.Name}.{port.Name}' needs a description."));
            }
            foreach (var argument in command.Arguments)
            {
                Name(argument.Name, "argument");
                if (string.IsNullOrWhiteSpace(argument.Description))
                    d.Add(new("SL3003", $"Argument '{command.Name}.{argument.Name}' needs a description."));
            }
            foreach (var output in command.Outputs)
            {
                Name(output.Name, "output");
                if (string.IsNullOrWhiteSpace(output.Description))
                    d.Add(new("SL3003", $"Output '{command.Name}.{output.Name}' needs a description."));
            }
            foreach (var item in command.Inputs.Select(x => x.Type).Concat(command.Arguments.Select(x => x.Type)).Concat(command.Outputs.Select(x => x.Type)))
                if (!IsKnownTypeReference(item, available))
                    d.Add(new("SL3005", $"Command '{command.Name}' references unknown type {item}."));
            foreach (var arg in command.Arguments)
                if ((!arg.Required && arg.DefaultValue is null) || (arg.DefaultValue is not null && arg.DefaultValue.Type != arg.Type))
                    d.Add(new("SL3010", $"Argument '{command.Name}.{arg.Name}' has an invalid default."));
            if (command.Arguments.Select(x => x.Position).Distinct().Count() != command.Arguments.Count || command.Arguments.Any(x => x.Position < 0))
                d.Add(new("SL3020", $"Command '{command.Name}' has invalid positional argument indices."));
            if (command.ErrorType is { } et && !(newErrorIds.Contains(et) || (_typeEntries.TryGetValue(et, out var errorEntry) && errorEntry.Kind == ShellTypeKind.Error)))
                d.Add(new("SL3017", $"Command '{command.Name}' has a non-error ErrorType."));
            foreach (var fault in command.RuntimeFaults)
                if (!RuntimeFaults.ContainsKey(fault.Value) && !set.RuntimeFaults.Any(x => x.Code == fault))
                    d.Add(new("SL3011", $"Command '{command.Name}' references unknown fault {fault}."));
        }
        foreach (var global in set.Globals)
        {
            Name(global.Name, "global");
            if (Globals.ContainsKey(global.Name) || set.Globals.Count(x => x.Name == global.Name) > 1)
                d.Add(new("SL3012", $"Duplicate global '{global.Name}'."));
            if (!IsKnownTypeReference(global.Type, available))
                d.Add(new("SL3005", $"Global '{global.Name}' has unknown type."));
            if (string.IsNullOrWhiteSpace(global.Description))
                d.Add(new("SL3003", $"Global '{global.Name}' needs a description."));
        }
        foreach (var fault in set.RuntimeFaults)
        {
            if (!FaultPattern.IsMatch(fault.Code.Value) || fault.Code.Value.StartsWith("SL", StringComparison.Ordinal))
                d.Add(new("SL3013", $"Invalid runtime fault code '{fault.Code}'."));
            Name(fault.Name, "runtime fault");
            if (RuntimeFaults.ContainsKey(fault.Code.Value) || set.RuntimeFaults.Count(x => x.Code == fault.Code) > 1)
                d.Add(new("SL3014", $"Duplicate runtime fault '{fault.Code}'."));
            if (RuntimeFaults.Values.Any(x => x.Name == fault.Name) || set.RuntimeFaults.Count(x => x.Name == fault.Name) > 1)
                d.Add(new("SL3014", $"Duplicate runtime fault name '{fault.Name}'."));
            if (string.IsNullOrWhiteSpace(fault.Description))
                d.Add(new("SL3003", $"Runtime fault '{fault.Code}' needs a description."));
        }
        return d;
    }

    private static void ValidateLocalNames(string owner, IEnumerable<string> names, List<HostingDiagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (!seen.Add(name))
                diagnostics.Add(new("SL3015", $"Duplicate local name '{owner}.{name}'."));
    }

    private bool IsKnownTypeReference(ShellTypeId type, HashSet<ShellTypeId> available)
    {
        if (!available.Contains(type))
            return false;
        if (!_typeEntries.TryGetValue(type, out var entry))
            return true;
        return entry.Kind switch
        {
            ShellTypeKind.Array => available.Contains(entry.ElementType!.Value),
            ShellTypeKind.Result => available.Contains(entry.SuccessType!.Value) && available.Contains(entry.ErrorType!.Value),
            ShellTypeKind.OutputRecord => entry.OutputFields!.Values.All(available.Contains),
            _ => true
        };
    }

    private void ValidateTypeCycles(IReadOnlyList<TypeDescriptor> types, List<HostingDiagnostic> diagnostics)
    {
        var map = types.ToDictionary(x => x.Id, x => x.DirectBases);
        var visiting = new HashSet<ShellTypeId>();
        var visited = new HashSet<ShellTypeId>();
        bool Visit(ShellTypeId id)
        {
            if (visiting.Contains(id))
                return true;
            if (!visited.Add(id) || !map.TryGetValue(id, out var bases))
                return false;
            visiting.Add(id);
            foreach (var parent in bases)
            if (Visit(parent))
                return true;
            visiting.Remove(id);
            return false;
        }
        foreach (var type in types)
        if (Visit(type.Id))
        {
            diagnostics.Add(new("SL3018", $"Nominal type graph containing '{type.Name}' is cyclic."));
            break;
        }
    }

    private void ValidateErrorCycles(IReadOnlyList<ErrorTypeDescriptor> errors, List<HostingDiagnostic> diagnostics)
    {
        var map = errors.ToDictionary(x => x.Id, x => x.BaseType);
        foreach (var error in errors)
        {
            var seen = new HashSet<ShellTypeId>();
            var current = error.Id;
            while (map.TryGetValue(current, out var parent))
            {
                if (!seen.Add(current))
                {
                    diagnostics.Add(new("SL3019", $"Error chain containing '{error.Name}' is cyclic."));
                    return;
                }
                current = parent;
            }
        }
    }

    public RegistrationResult Register(DescriptorSet descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var diagnostics = Validate(descriptors);
        if (diagnostics.Count > 0)
            return new RegistrationResult(diagnostics);
        foreach (var type in descriptors.Types)
        {
            type.SymbolId = NextSymbol(type);
            foreach (var member in type.Members)
            {
                member.ReceiverType = type.Id;
                member.Id = NextSymbol(member);
            }
            foreach (var query in type.Queries)
            {
                query.ReceiverType = type.Id;
                query.Id = NextSymbol(query);
            }
            AddEntry(new TypeEntry
            {
                Id = type.Id,
                Name = type.Name,
                Description = type.Description,
                Kind = ShellTypeKind.Host,
                ClrType = type.ClrType,
                Adapter = type.Adapter,
                Bases = type.DirectBases,
                Members = type.Members,
                Queries = type.Queries,
                Equality = type.Equality,
                Ordering = type.Ordering
            });
            Types.Add(type);
            RefreshConstructedTypeNames(type.Id, type.Name);
        }
        foreach (var type in descriptors.Enums)
        {
            type.SymbolId = NextSymbol(type);
            AddEntry(new TypeEntry
            {
                Id = type.Id,
                Name = type.Name,
                Description = type.Description,
                Kind = ShellTypeKind.Enum,
                ClrType = type.ClrType,
                Adapter = type.Adapter,
                EnumMembers = type.Members,
                Ordering = type.Ordering,
                Equality = new((a, b) => Equals(a, b))
            });
            Enums.Add(type);
            RefreshConstructedTypeNames(type.Id, type.Name);
        }
        foreach (var error in descriptors.Errors)
        {
            error.SymbolId = NextSymbol(error);
            AddEntry(new TypeEntry
            {
                Id = error.Id,
                Name = error.Name,
                Description = error.Description,
                Kind = ShellTypeKind.Error,
                ClrType = error.ClrType,
                Adapter = error.Adapter,
                Bases = [error.BaseType]
            });
            Errors.Add(error);
            RefreshConstructedTypeNames(error.Id, error.Name);
        }
        foreach (var fault in descriptors.RuntimeFaults)
            RuntimeFaults.Add(fault.Code.Value, fault);
        foreach (var global in descriptors.Globals)
        {
            global.Id = NextSymbol(global);
            Globals.Add(global.Name, global);
        }
        foreach (var command in descriptors.Commands)
        {
            command.Id = NextSymbol(command);
            if (command.Outputs.Count > 1)
            {
                var id = command.OutputRecordType!.Value;
                _typeEntries.Add(id, new TypeEntry
                {
                    Id = id,
                    Name = $"{ToPascal(command.Name)}.Output",
                    Description = $"Outputs of {command.Name}.",
                    Kind = ShellTypeKind.OutputRecord,
                    ClrType = typeof(ShellOutputRecordValue),
                    OutputFields = new ReadOnlyDictionary<string, ShellTypeId>(command.Outputs.ToDictionary(x => x.Name, x => x.Type, StringComparer.Ordinal)),
                    DefaultOutput = command.Outputs.FirstOrDefault(x => x.IsDefault)?.Name
                });
            }
            Commands.Add(command.Name, command);
        }
        CatalogRevision++;
        return new RegistrationResult(Array.Empty<HostingDiagnostic>());
    }

    private static string ToPascal(string value) => string.Concat(value.Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));

    private void RefreshConstructedTypeNames(ShellTypeId type, string name)
    {
        if (_arrayTypes.TryGetValue(type, out var array))
            _typeEntries[array].Name = $"Array<{name}>";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,15}[0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex FaultRegex();

    internal static readonly IReadOnlyDictionary<string, string> IntrinsicDescriptions =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["require"] = "Return an Ok value or fault on Err.",
            ["value_or"] = "Return an Ok value or a supplied default.",
            ["error"] = "Return the error from an Err value.",
            ["is_ok"] = "Report whether a Result is Ok.",
            ["where"] = "Keep elements that satisfy a contextual predicate.",
            ["sort"] = "Stable-sort elements by a contextual key.",
            ["take"] = "Return up to the first count elements.",
            ["count"] = "Return the number of elements.",
            ["sum"] = "Add all numeric elements.",
            ["first"] = "Return the first element or EmptyCollectionError.",
            ["min"] = "Return the least element or EmptyCollectionError.",
            ["max"] = "Return the greatest element or EmptyCollectionError.",
            ["average"] = "Return the numeric average or EmptyCollectionError.",
            ["at"] = "Return the element at a positive or end-relative index.",
            ["last"] = "Return the last element or EmptyCollectionError.",
            ["skip"] = "Return the elements after an initial count.",
            ["slice"] = "Return a strict contiguous array range.",
            ["any"] = "Report whether any element satisfies a contextual predicate.",
            ["all"] = "Report whether every element satisfies a contextual predicate.",
            ["select"] = "Transform each element with a contextual selector.",
            ["contains"] = "Report whether an equal element is present.",
            ["concat"] = "Append another assignable array.",
            ["distinct"] = "Keep the first element for each equal value or key.",
            ["reverse"] = "Return the elements in reverse order.",
            ["single"] = "Return the only element or CollectionCardinalityError."
        });

    internal static readonly HashSet<string> IntrinsicNames = new(IntrinsicDescriptions.Keys, StringComparer.Ordinal);
}
