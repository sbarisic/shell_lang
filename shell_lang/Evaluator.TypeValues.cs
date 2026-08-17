namespace ShellLang;

internal sealed partial class Evaluator
{
	private EvalOutcome EvaluateTypeValue(BoundTypeValueExpression expression, IReadOnlyList<int> path)
	{
		if (expression.IsEnumValues)
		{
			var owner = _engine.GetTypeEntry(expression.OwnerType);
			var values = owner.EnumMembers.Select(member => _engine.CreateValue(owner.Id, member.Value));
			return EvalOutcome.Success(_engine.CreateArray(owner.Id, values));
		}
		var descriptor = expression.Descriptor!;
		try
		{
			var value = descriptor.GetValue!(Context(expression.Span, path));
			if (value is null || !_engine.IsAssignable(value.Type, descriptor.ValueType))
				return EvalOutcome.Host(new HostFault("SL5120",
					$"Type value '{_engine.TypeName(expression.OwnerType)}.{expression.Name}' returned null or the wrong type.", expression.Span));
			var entry = _engine.GetTypeEntry(value.Type);
			var validPayload = entry.Adapter?.IsValid(value.Value) ?? entry.Kind switch
			{
				ShellTypeKind.Array => value.Value is ShellArrayValue,
				ShellTypeKind.Result => value.Value is ShellResultValue,
				ShellTypeKind.OutputRecord => value.Value is ShellOutputRecordValue,
				_ => false
			};
			if (!validPayload)
				return EvalOutcome.Host(new HostFault("SL5121",
					$"Type value '{_engine.TypeName(expression.OwnerType)}.{expression.Name}' returned an invalid CLR value.", expression.Span));
			return EvalOutcome.Success(value);
		}
		catch (Exception ex)
		{
			return EvalOutcome.Host(new HostFault("SL5122",
				$"Type value provider '{_engine.TypeName(expression.OwnerType)}.{expression.Name}' failed.", expression.Span, ex));
		}
	}
}
