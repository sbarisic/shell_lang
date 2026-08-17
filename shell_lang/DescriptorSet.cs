namespace ShellLang;

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
