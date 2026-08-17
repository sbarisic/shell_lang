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
	public ShellTypeId ConversionError
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
	public IReadOnlyList<ResolvedTypeSymbol> ResolvedSymbols { get; init; } = Array.Empty<ResolvedTypeSymbol>();
	public IReadOnlyList<EnumMemberDescriptor> EnumMembers { get; init; } = Array.Empty<EnumMemberDescriptor>();
	public EqualityDescriptor? Equality
	{
		get; init;
	}
	public OrderingDescriptor? Ordering
	{
		get; init;
	}
	public ConstructorDescriptor? Constructor
	{
		get; init;
	}
	public IReadOnlyList<TypeValueDescriptor> TypeValues { get; init; } = Array.Empty<TypeValueDescriptor>();
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

internal sealed record ResolvedTypeSymbol(ShellTypeId DeclaringType, MemberDescriptor? Member, QueryDescriptor? Query)
{
	public string Name => Member?.Name ?? Query!.Name;
	public string Kind => Member is null ? "query" : "member";
	public object Descriptor => (object?)Member ?? Query!;
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
