namespace ShellLang;

internal sealed partial class Binder
{
	private BoundExpression BindConversion(TypeEntry target, InvocationSyntax syntax)
	{
		InvocationEntrySyntax? supplied = null;
		var sawNamed = false;
		foreach (var entry in syntax.Entries)
		{
			if (entry.Kind == InvocationEntryKind.ExplicitInput)
			{
				Error("SL2503", $"Conversion '{target.Name}' does not accept explicit input entries.", entry.Span);
				continue;
			}
			if (entry.Kind == InvocationEntryKind.NamedArgument && entry.Name != "value")
			{
				Error("SL2208", $"Unknown argument in conversion '{target.Name}'.", entry.Span);
				continue;
			}
			if (entry.Kind == InvocationEntryKind.NamedArgument)
				sawNamed = true;
			else if (sawNamed)
				Error("SL2207", "Positional arguments must precede named arguments.", entry.Span);
			if (supplied is not null)
			{
				Error("SL2209", "Argument 'value' is supplied more than once.", entry.Span);
				continue;
			}
			supplied = entry;
		}
		if (supplied is null)
			return ErrorExpression("SL2211", $"Required argument '{target.Name}.value' is missing.", syntax.Span);

		var operand = BindExpression(supplied.Expression);
		var source = operand.Type;
		ShellTypeId? operandError = null;
		var sourceEntry = _engine.GetTypeEntry(source);
		if (sourceEntry.Kind == ShellTypeKind.Result)
		{
			source = sourceEntry.SuccessType!.Value;
			operandError = sourceEntry.ErrorType;
		}
		if (!_engine.TryGetConversion(source, target.Id, out var conversion))
			return ErrorExpression("SL2501",
				$"Type '{target.Name}' does not support conversion from '{_engine.TypeName(source)}'.", syntax.Span);

		var result = target.Id;
		if (conversion.IsFallible)
			result = _engine.ResultOf(target.Id, _engine.Core.ConversionError);
		if (operandError is { } error)
		{
			var combined = conversion.IsFallible
				? _engine.CommonError(error, _engine.Core.ConversionError)
				: error;
			result = _engine.ResultOf(target.Id, combined);
		}
		return new BoundConversionExpression(conversion, operand, result, syntax.Span);
	}

	private BoundExpression BindTypeValue(TypeEntry owner, string name,
		IReadOnlyList<InvocationEntrySyntax>? arguments, SourceSpan span)
	{
		if (arguments is not null)
			Error("SL2302", "Type-scoped values cannot be invoked.", span);
		if (owner.Kind == ShellTypeKind.Enum)
		{
			if (name == "values")
				return new BoundTypeValueExpression(null, owner.Id, name, _engine.ArrayOf(owner.Id), true, span);
			var member = owner.EnumMembers.FirstOrDefault(x => x.Name == name);
			if (member is not null)
				return new BoundLiteralExpression(_engine.CreateValue(owner.Id, member.Value), span);
		}
		var descriptor = owner.TypeValues.FirstOrDefault(x => x.Name == name);
		if (descriptor is not null)
			return descriptor.FixedValue is { } fixedValue
				? new BoundLiteralExpression(fixedValue, span)
				: new BoundTypeValueExpression(descriptor, owner.Id, name, descriptor.ValueType, false, span);
		return ErrorExpression("SL2301", $"Type '{owner.Name}' has no scoped value '{name}'.", span);
	}
}
