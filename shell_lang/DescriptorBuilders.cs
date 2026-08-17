namespace ShellLang;

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
	private ConstructorDescriptor? _constructor;
	private readonly List<TypeValueDescriptor> _typeValues = [];
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
	public TypeDescriptorBuilder<T> Constructor(IEnumerable<ArgumentDescriptor>? arguments,
		Func<InvocationContext, InvocationValues, T> invoke)
	{
		ArgumentNullException.ThrowIfNull(invoke);
		_constructor = new ConstructorDescriptor(arguments, (context, values) =>
			new ConstructorOutcome.Success(context.Engine.CreateValue(_constructor!.ConstructedType, invoke(context, values))));
		return this;
	}
	public TypeDescriptorBuilder<T> FallibleConstructor(IEnumerable<ArgumentDescriptor>? arguments,
		ShellTypeId errorType, Func<InvocationContext, InvocationValues, ConstructorOutcome<T>> invoke)
	{
		ArgumentNullException.ThrowIfNull(invoke);
		_constructor = new ConstructorDescriptor(arguments, (context, values) => invoke(context, values) switch
		{
			ConstructorOutcome<T>.Success success => new ConstructorOutcome.Success(
				context.Engine.CreateValue(_constructor!.ConstructedType, success.Value)),
			ConstructorOutcome<T>.Error error => new ConstructorOutcome.Error(error.Value),
			_ => throw new InvalidOperationException("Constructor returned an unknown outcome.")
		}, errorType);
		return this;
	}
	public TypeDescriptorBuilder<T> Value<TValue>(string name, string description, ShellTypeId type, TValue value)
		where TValue : notnull
	{
		_typeValues.Add(new TypeValueDescriptor(name, description, type, new ShellValue(type, value)));
		return this;
	}
	public TypeDescriptorBuilder<T> Value(string name, string description, T value)
	{
		_typeValues.Add(new TypeValueDescriptor(name, description, value));
		return this;
	}
	public TypeDescriptorBuilder<T> ProvidedValue<TValue>(string name, string description, ShellTypeId type,
		Func<InvocationContext, TValue> getValue) where TValue : notnull
	{
		ArgumentNullException.ThrowIfNull(getValue);
		_typeValues.Add(new TypeValueDescriptor(name, description, type,
			context => context.Engine.CreateValue(type, getValue(context))));
		return this;
	}
	public TypeDescriptorBuilder<T> ProvidedValue(string name, string description,
		Func<InvocationContext, T> getValue)
	{
		ArgumentNullException.ThrowIfNull(getValue);
		_typeValues.Add(new TypeValueDescriptor(name, description, context => getValue(context)));
		return this;
	}
	public TypeDescriptor Build() => new(_name, _description, typeof(T), new ValueAdapter<T>(), _bases, _members,
		_queries, _equality, _ordering, _constructor, _typeValues);
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
	private string? _namespace;
	private string? _category;
	private readonly List<string> _examples = [];
	private readonly List<CommandAliasDescriptor> _aliases = [];
	private string? _introducedVersion;
	private CommandDeprecation? _deprecation;
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
	public CommandBuilder Namespace(string value) { _namespace = value; return this; }
	public CommandBuilder Category(string value) { _category = value; return this; }
	public CommandBuilder Example(string value) { _examples.Add(value); return this; }
	public CommandBuilder Alias(string name, CommandDeprecation? deprecation = null)
	{
		_aliases.Add(new(name, deprecation));
		return this;
	}
	public CommandBuilder IntroducedIn(string version) { _introducedVersion = version; return this; }
	public CommandBuilder Deprecated(string message, string sinceVersion, string? replacement = null)
	{
		_deprecation = new(message, sinceVersion, replacement);
		return this;
	}
	public CommandBuilder Invoke(CommandInvoker invoke)
	{
		_invoke = invoke;
		return this;
	}
	public CommandDescriptor Build() => new(_name, _description, _inputs, _arguments, _outputs,
		_invoke ?? throw new InvalidOperationException("A command invoker is required."), _error, _faults,
		_namespace, _category, _examples, _aliases, _introducedVersion, _deprecation);
}
