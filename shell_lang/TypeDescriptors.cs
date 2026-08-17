namespace ShellLang;

public sealed class MemberDescriptor
{
	public MemberDescriptor(string name, string description, ShellTypeId receiverType,
		ShellTypeId valueType, MemberGetter getValue)
	{
		Name = name;
		Description = description;
		ReceiverType = receiverType;
		ValueType = valueType;
		GetValue = getValue ?? throw new ArgumentNullException(nameof(getValue));
	}
	public SymbolId Id
	{
		get; internal set;
	}
	public string Name
	{
		get;
	}
	public string Description
	{
		get;
	}
	public ShellTypeId ReceiverType
	{
		get; internal set;
	}
	public ShellTypeId ValueType
	{
		get;
	}
	public MemberGetter GetValue
	{
		get;
	}
}

public sealed class QueryDescriptor
{
	public QueryDescriptor(string name, string description, ShellTypeId receiverType,
		IEnumerable<ArgumentDescriptor>? arguments, ShellTypeId outputType,
		QueryInvoker invoke, ShellTypeId? errorType = null)
	{
		Name = name;
		Description = description;
		ReceiverType = receiverType;
		Arguments = Array.AsReadOnly((arguments ?? []).ToArray());
		OutputType = outputType;
		ErrorType = errorType;
		Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
	}
	public SymbolId Id
	{
		get; internal set;
	}
	public string Name
	{
		get;
	}
	public string Description
	{
		get;
	}
	public ShellTypeId ReceiverType
	{
		get; internal set;
	}
	public IReadOnlyList<ArgumentDescriptor> Arguments
	{
		get;
	}
	public ShellTypeId OutputType
	{
		get;
	}
	public ShellTypeId? ErrorType
	{
		get;
	}
	public QueryInvoker Invoke
	{
		get;
	}
}

public sealed class TypeDescriptor
{
	public TypeDescriptor(string name, string description, Type clrType, ValueAdapter adapter,
		IEnumerable<ShellTypeId>? directBases = null, IEnumerable<MemberDescriptor>? members = null,
		IEnumerable<QueryDescriptor>? queries = null, EqualityDescriptor? equality = null,
		OrderingDescriptor? ordering = null, ConstructorDescriptor? constructor = null)
	{
		Id = IdentitySource.NextType();
		Name = name;
		Description = description;
		ClrType = clrType;
		Adapter = adapter;
		adapter.TypeId = Id;
		DirectBases = Array.AsReadOnly((directBases ?? []).ToArray());
		Members = Array.AsReadOnly((members ?? []).ToArray());
		Queries = Array.AsReadOnly((queries ?? []).ToArray());
		Equality = equality;
		Ordering = ordering;
		Constructor = constructor;
	}
	public ShellTypeId Id
	{
		get;
	}
	public SymbolId SymbolId
	{
		get; internal set;
	}
	public string Name
	{
		get;
	}
	public string Description
	{
		get;
	}
	public Type ClrType
	{
		get;
	}
	public IReadOnlyList<ShellTypeId> DirectBases
	{
		get;
	}
	public ValueAdapter Adapter
	{
		get;
	}
	public IReadOnlyList<MemberDescriptor> Members
	{
		get;
	}
	public IReadOnlyList<QueryDescriptor> Queries
	{
		get;
	}
	public EqualityDescriptor? Equality
	{
		get;
	}
	public OrderingDescriptor? Ordering
	{
		get;
	}
	public ConstructorDescriptor? Constructor
	{
		get;
	}
}

public sealed record EnumMemberDescriptor(string Name, object Value, string Description = "Enum member.");

public sealed class EnumTypeDescriptor
{
	public EnumTypeDescriptor(string name, string description, Type clrType, ValueAdapter adapter,
		IEnumerable<EnumMemberDescriptor> members, OrderingDescriptor? ordering = null)
	{
		Id = IdentitySource.NextType();
		Name = name;
		Description = description;
		ClrType = clrType;
		Adapter = adapter;
		adapter.TypeId = Id;
		Members = Array.AsReadOnly(members.ToArray());
		Ordering = ordering;
	}
	public ShellTypeId Id
	{
		get;
	}
	public SymbolId SymbolId
	{
		get; internal set;
	}
	public string Name
	{
		get;
	}
	public string Description
	{
		get;
	}
	public Type ClrType
	{
		get;
	}
	public ValueAdapter Adapter
	{
		get;
	}
	public IReadOnlyList<EnumMemberDescriptor> Members
	{
		get;
	}
	public OrderingDescriptor? Ordering
	{
		get;
	}
}

public sealed class ErrorTypeDescriptor
{
	public ErrorTypeDescriptor(string name, string description, Type clrType, ValueAdapter adapter,
		ShellTypeId baseType)
	{
		Id = IdentitySource.NextType();
		Name = name;
		Description = description;
		ClrType = clrType;
		Adapter = adapter;
		BaseType = baseType;
		adapter.TypeId = Id;
	}
	public ShellTypeId Id
	{
		get;
	}
	public SymbolId SymbolId
	{
		get; internal set;
	}
	public string Name
	{
		get;
	}
	public string Description
	{
		get;
	}
	public Type ClrType
	{
		get;
	}
	public ValueAdapter Adapter
	{
		get;
	}
	public ShellTypeId BaseType
	{
		get;
	}
}

public sealed class GlobalDescriptor
{
	public GlobalDescriptor(string name, string description, ShellTypeId type, GlobalValueProvider getValue)
	{
		Name = name;
		Description = description;
		Type = type;
		GetValue = getValue ?? throw new ArgumentNullException(nameof(getValue));
	}
	public SymbolId Id
	{
		get; internal set;
	}
	public string Name
	{
		get;
	}
	public string Description
	{
		get;
	}
	public ShellTypeId Type
	{
		get;
	}
	public GlobalValueProvider GetValue
	{
		get;
	}
}
