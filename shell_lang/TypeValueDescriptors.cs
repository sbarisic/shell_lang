namespace ShellLang;

public delegate ShellValue TypeValueProvider(InvocationContext context);

public sealed class TypeValueDescriptor
{
	internal TypeValueDescriptor(string name, string description, object fixedValue)
	{
		Name = name;
		Description = description;
		PendingFixedValue = fixedValue ?? throw new ArgumentNullException(nameof(fixedValue));
	}

	internal TypeValueDescriptor(string name, string description, Func<InvocationContext, object> getValue)
	{
		Name = name;
		Description = description;
		PendingProvider = getValue ?? throw new ArgumentNullException(nameof(getValue));
	}

	public TypeValueDescriptor(string name, string description, ShellTypeId valueType, ShellValue value)
	{
		Name = name;
		Description = description;
		ValueType = valueType;
		FixedValue = value ?? throw new ArgumentNullException(nameof(value));
	}

	public TypeValueDescriptor(string name, string description, ShellTypeId valueType, TypeValueProvider getValue)
	{
		Name = name;
		Description = description;
		ValueType = valueType;
		GetValue = getValue ?? throw new ArgumentNullException(nameof(getValue));
	}

	public string Name { get; }
	public string Description { get; }
	public ShellTypeId ValueType { get; internal set; }
	public ShellValue? FixedValue { get; internal set; }
	public TypeValueProvider? GetValue { get; internal set; }
	public bool IsProviderBacked => GetValue is not null;
	internal object? PendingFixedValue { get; }
	internal Func<InvocationContext, object>? PendingProvider { get; }
	internal void SetOwner(TypeDescriptor owner)
	{
		if (ValueType != default)
			return;
		ValueType = owner.Id;
		if (PendingFixedValue is not null)
			FixedValue = new ShellValue(owner.Id, PendingFixedValue);
		else if (PendingProvider is not null)
			GetValue = context => context.Engine.CreateValue(owner.Id, PendingProvider(context));
	}
}
