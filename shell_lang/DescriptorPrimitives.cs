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
