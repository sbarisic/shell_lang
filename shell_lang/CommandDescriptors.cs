using System.Collections.ObjectModel;

namespace ShellLang;

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
