namespace ShellLang;

public delegate ConstructorOutcome ConstructorInvoker(InvocationContext context, InvocationValues values);

public abstract record ConstructorOutcome
{
	private ConstructorOutcome()
	{
	}

	public sealed record Success(ShellValue Value) : ConstructorOutcome;
	public sealed record Error(ShellValue Value) : ConstructorOutcome;
}

public abstract record ConstructorOutcome<T> where T : notnull
{
	private ConstructorOutcome()
	{
	}

	public sealed record Success(T Value) : ConstructorOutcome<T>;
	public sealed record Error(ShellValue Value) : ConstructorOutcome<T>;
}

public sealed class ConstructorDescriptor
{
	public ConstructorDescriptor(IEnumerable<ArgumentDescriptor>? arguments, ConstructorInvoker invoke,
		ShellTypeId? errorType = null)
	{
		Arguments = Array.AsReadOnly((arguments ?? []).ToArray());
		Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
		ErrorType = errorType;
	}

	public IReadOnlyList<ArgumentDescriptor> Arguments
	{
		get;
	}

	public ShellTypeId? ErrorType
	{
		get;
	}

	public ConstructorInvoker Invoke
	{
		get;
	}

	internal ShellTypeId ConstructedType
	{
		get; set;
	}
}
