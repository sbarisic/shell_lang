using System.Collections.ObjectModel;

namespace ShellLang;

internal static class IdentitySource
{
	private static int _nextType;
	public static ShellTypeId NextType() => new(Interlocked.Increment(ref _nextType));
}

public delegate ShellValue GlobalValueProvider(InvocationContext context);
public delegate ShellValue MemberGetter(InvocationContext context, ShellValue receiver);
public delegate QueryOutcome QueryInvoker(InvocationContext context, ShellValue receiver, InvocationValues values);
public delegate CommandOutcome CommandInvoker(InvocationContext context, InvocationValues values);

public abstract class ValueAdapter
{
	internal ShellTypeId TypeId
	{
		get; set;
	}
	public abstract Type ClrType
	{
		get;
	}
	public abstract bool IsValid(object value);
	public abstract object GetClrValue(ShellValue value);
	public abstract ShellValue CreateShellValue(object value);
}

public sealed class ValueAdapter<T> : ValueAdapter where T : notnull
{
	public override Type ClrType => typeof(T);
	public override bool IsValid(object value) => value is T;
	public override object GetClrValue(ShellValue value)
	{
		if (value.Type != TypeId || value.Value is not T typed)
			throw new InvalidCastException($"Expected {typeof(T).Name} for Shell type {TypeId}.");
		return typed;
	}
	public override ShellValue CreateShellValue(object value)
	{
		if (value is not T)
			throw new ArgumentException($"Expected {typeof(T).Name}.", nameof(value));
		return new ShellValue(TypeId, value);
	}
	public ShellValue Create(T value) => CreateShellValue(value);
}

public sealed class EqualityDescriptor
{
	public EqualityDescriptor(Func<object, object, bool> equals) => CompareEqual = equals ?? throw new ArgumentNullException(nameof(equals));
	public Func<object, object, bool> CompareEqual
	{
		get;
	}
}

public sealed class OrderingDescriptor
{
	public OrderingDescriptor(Func<object, object, int> compare) => Compare = compare ?? throw new ArgumentNullException(nameof(compare));
	public Func<object, object, int> Compare
	{
		get;
	}
}

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
		OrderingDescriptor? ordering = null)
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

public sealed class InputPortDescriptor
{
	public InputPortDescriptor(string name, string description, ShellTypeId type, bool isDefault = false)
	{
		Name = name;
		Description = description;
		Type = type;
		IsDefault = isDefault;
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
	public bool IsDefault
	{
		get;
	}
}

public sealed class ArgumentDescriptor
{
	public ArgumentDescriptor(string name, string description, ShellTypeId type, int position,
		bool required = true, ShellValue? defaultValue = null)
	{
		Name = name;
		Description = description;
		Type = type;
		Position = position;
		Required = required;
		DefaultValue = defaultValue;
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
	public int Position
	{
		get;
	}
	public bool Required
	{
		get;
	}
	public ShellValue? DefaultValue
	{
		get;
	}
}

public sealed class OutputPortDescriptor
{
	public OutputPortDescriptor(string name, string description, ShellTypeId type, bool isDefault = false)
	{
		Name = name;
		Description = description;
		Type = type;
		IsDefault = isDefault;
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
	public bool IsDefault
	{
		get;
	}
}

public sealed class RuntimeFaultDescriptor
{
	public RuntimeFaultDescriptor(RuntimeFaultCode code, string name, string description)
	{
		Code = code;
		Name = name;
		Description = description;
	}
	public RuntimeFaultCode Code
	{
		get;
	}
	public string Name
	{
		get;
	}
	public string Description
	{
		get;
	}
}

public sealed class CommandDescriptor
{
	public CommandDescriptor(string name, string description, IEnumerable<InputPortDescriptor>? inputs,
		IEnumerable<ArgumentDescriptor>? arguments, IEnumerable<OutputPortDescriptor>? outputs,
		CommandInvoker invoke, ShellTypeId? errorType = null,
		IEnumerable<RuntimeFaultCode>? runtimeFaults = null)
	{
		Name = name;
		Description = description;
		Inputs = Array.AsReadOnly((inputs ?? []).ToArray());
		Arguments = Array.AsReadOnly((arguments ?? []).ToArray());
		Outputs = Array.AsReadOnly((outputs ?? []).ToArray());
		ErrorType = errorType;
		RuntimeFaults = Array.AsReadOnly((runtimeFaults ?? []).ToArray());
		Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
		if (Outputs.Count > 1)
			OutputRecordType = IdentitySource.NextType();
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
	public IReadOnlyList<InputPortDescriptor> Inputs
	{
		get;
	}
	public IReadOnlyList<ArgumentDescriptor> Arguments
	{
		get;
	}
	public IReadOnlyList<OutputPortDescriptor> Outputs
	{
		get;
	}
	public ShellTypeId? ErrorType
	{
		get;
	}
	public IReadOnlyList<RuntimeFaultCode> RuntimeFaults
	{
		get;
	}
	public CommandInvoker Invoke
	{
		get;
	}
	public ShellTypeId? OutputRecordType
	{
		get;
	}
}

public abstract record QueryOutcome
{
	private QueryOutcome()
	{
	}
	public sealed record Success(ShellValue Value) : QueryOutcome;
	public sealed record Error(ShellValue Value) : QueryOutcome;
}

public abstract record CommandOutcome
{
	private CommandOutcome()
	{
	}
	public sealed record Success(IReadOnlyDictionary<string, ShellValue> Outputs) : CommandOutcome
	{
		public static Success Empty { get; } = new(new ReadOnlyDictionary<string, ShellValue>(new Dictionary<string, ShellValue>()));
		public static Success Single(string name, ShellValue value) => new(
			new ReadOnlyDictionary<string, ShellValue>(new Dictionary<string, ShellValue>(StringComparer.Ordinal) { [name] = value }));
	}
	public sealed record Error(ShellValue Value) : CommandOutcome;
	public sealed record Fault(RuntimeFaultCode Code, string Message) : CommandOutcome;
}

public sealed class InvocationValues
{
	private readonly IReadOnlyDictionary<string, ShellValue> _inputs;
	private readonly IReadOnlyDictionary<string, ShellValue> _arguments;
	internal InvocationValues(IReadOnlyDictionary<string, ShellValue> inputs, IReadOnlyDictionary<string, ShellValue> arguments)
	{
		_inputs = inputs;
		_arguments = arguments;
	}
	public ShellValue GetInput(string name) => _inputs.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"Unknown input '{name}'.");
	public ShellValue GetArgument(string name) => _arguments.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"Unknown argument '{name}'.");
	public T GetInput<T>(string name) => GetInput(name).Get<T>();
	public T GetArgument<T>(string name) => GetArgument(name).Get<T>();
}

public sealed class InvocationContext
{
	internal InvocationContext(ShellEngine engine, ShellSession session, IServiceProvider services,
		SourceSpan source, IReadOnlyList<int> arrayIndexPath)
	{
		Engine = engine;
		Session = session;
		Services = services;
		Source = source;
		ArrayIndexPath = arrayIndexPath;
	}
	public ShellEngine Engine
	{
		get;
	}
	public ShellSession Session
	{
		get;
	}
	public IServiceProvider Services
	{
		get;
	}
	public SourceSpan Source
	{
		get;
	}
	public IReadOnlyList<int> ArrayIndexPath
	{
		get;
	}
}

public sealed class DescriptorSet
{
	public DescriptorSet(IEnumerable<TypeDescriptor>? types = null, IEnumerable<EnumTypeDescriptor>? enums = null,
		IEnumerable<ErrorTypeDescriptor>? errors = null, IEnumerable<GlobalDescriptor>? globals = null,
		IEnumerable<CommandDescriptor>? commands = null, IEnumerable<RuntimeFaultDescriptor>? runtimeFaults = null)
	{
		Types = Array.AsReadOnly((types ?? []).ToArray());
		Enums = Array.AsReadOnly((enums ?? []).ToArray());
		Errors = Array.AsReadOnly((errors ?? []).ToArray());
		Globals = Array.AsReadOnly((globals ?? []).ToArray());
		Commands = Array.AsReadOnly((commands ?? []).ToArray());
		RuntimeFaults = Array.AsReadOnly((runtimeFaults ?? []).ToArray());
	}
	public IReadOnlyList<TypeDescriptor> Types
	{
		get;
	}
	public IReadOnlyList<EnumTypeDescriptor> Enums
	{
		get;
	}
	public IReadOnlyList<ErrorTypeDescriptor> Errors
	{
		get;
	}
	public IReadOnlyList<GlobalDescriptor> Globals
	{
		get;
	}
	public IReadOnlyList<CommandDescriptor> Commands
	{
		get;
	}
	public IReadOnlyList<RuntimeFaultDescriptor> RuntimeFaults
	{
		get;
	}
}

public static class TypeDescriptorBuilder
{
	public static TypeDescriptorBuilder<T> For<T>(string name) where T : notnull => new(name);
}

public sealed class TypeDescriptorBuilder<T> where T : notnull
{
	private readonly string _name;
	private string _description = "Registered host type.";
	private readonly List<ShellTypeId> _bases = [];
	private readonly List<MemberDescriptor> _members = [];
	private readonly List<QueryDescriptor> _queries = [];
	private EqualityDescriptor? _equality;
	private OrderingDescriptor? _ordering;
	internal TypeDescriptorBuilder(string name) => _name = name;
	public TypeDescriptorBuilder<T> Description(string value)
	{
		_description = value;
		return this;
	}
	public TypeDescriptorBuilder<T> Base(ShellTypeId value)
	{
		_bases.Add(value);
		return this;
	}
	public TypeDescriptorBuilder<T> Member<TValue>(string name, string description, ShellTypeId type, Func<T, TValue> getter) where TValue : notnull
	{
		_members.Add(new MemberDescriptor(name, description, default, type, (context, receiver) =>
			context.Engine.CreateValue(type, getter(receiver.Get<T>()))));
		return this;
	}
	public TypeDescriptorBuilder<T> Query<TValue>(string name, string description, IEnumerable<ArgumentDescriptor>? arguments,
		ShellTypeId outputType, Func<InvocationContext, T, InvocationValues, TValue> invoke) where TValue : notnull
	{
		_queries.Add(new QueryDescriptor(name, description, default, arguments, outputType,
			(context, receiver, values) => new QueryOutcome.Success(context.Engine.CreateValue(outputType, invoke(context, receiver.Get<T>(), values)))));
		return this;
	}
	public TypeDescriptorBuilder<T> FallibleQuery(string name, string description, IEnumerable<ArgumentDescriptor>? arguments,
		ShellTypeId outputType, ShellTypeId errorType, Func<InvocationContext, T, InvocationValues, QueryOutcome> invoke)
	{
		_queries.Add(new QueryDescriptor(name, description, default, arguments, outputType,
			(context, receiver, values) => invoke(context, receiver.Get<T>(), values), errorType));
		return this;
	}
	public TypeDescriptorBuilder<T> Equality(Func<T, T, bool> equals)
	{
		_equality = new EqualityDescriptor((a, b) => equals((T)a, (T)b));
		return this;
	}
	public TypeDescriptorBuilder<T> Ordering(Func<T, T, int> compare)
	{
		_ordering = new OrderingDescriptor((a, b) => compare((T)a, (T)b));
		return this;
	}
	public TypeDescriptor Build() => new(_name, _description, typeof(T), new ValueAdapter<T>(), _bases, _members, _queries, _equality, _ordering);
}

public static class CommandDescriptorBuilder
{
	public static CommandBuilder Create(string name) => new(name);
}

public sealed class CommandBuilder
{
	private readonly string _name;
	private string _description = "Registered command.";
	private readonly List<InputPortDescriptor> _inputs = [];
	private readonly List<ArgumentDescriptor> _arguments = [];
	private readonly List<OutputPortDescriptor> _outputs = [];
	private readonly List<RuntimeFaultCode> _faults = [];
	private ShellTypeId? _error;
	private CommandInvoker? _invoke;
	internal CommandBuilder(string name) => _name = name;
	public CommandBuilder Description(string value)
	{
		_description = value;
		return this;
	}
	public CommandBuilder Input(string name, ShellTypeId type, bool isDefault = false, string description = "Input port.")
	{
		_inputs.Add(new InputPortDescriptor(name, description, type, isDefault));
		return this;
	}
	public CommandBuilder Argument(string name, ShellTypeId type, bool required = true, ShellValue? defaultValue = null, string description = "Argument.")
	{
		_arguments.Add(new ArgumentDescriptor(name, description, type, _arguments.Count, required, defaultValue));
		return this;
	}
	public CommandBuilder Output(string name, ShellTypeId type, bool isDefault = false, string description = "Output port.")
	{
		_outputs.Add(new OutputPortDescriptor(name, description, type, isDefault));
		return this;
	}
	public CommandBuilder Error(ShellTypeId type)
	{
		_error = type;
		return this;
	}
	public CommandBuilder RuntimeFault(RuntimeFaultCode code)
	{
		_faults.Add(code);
		return this;
	}
	public CommandBuilder Invoke(CommandInvoker invoke)
	{
		_invoke = invoke;
		return this;
	}
	public CommandDescriptor Build() => new(_name, _description, _inputs, _arguments, _outputs,
		_invoke ?? throw new InvalidOperationException("A command invoker is required."), _error, _faults);
}
