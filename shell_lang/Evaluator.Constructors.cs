using System.Collections.ObjectModel;

namespace ShellLang;

internal sealed partial class Evaluator
{
	private EvalOutcome EvaluateConstructor(BoundConstructorExpression expression, IReadOnlyList<int> path)
	{
		var arguments = new Dictionary<string, ShellValue>(StringComparer.Ordinal);
		foreach (var argument in expression.Arguments)
		{
			var evaluated = Evaluate(argument.Expression, path);
			if (evaluated.Failed)
				return evaluated;
			if (evaluated.Value is null)
				return EvalOutcome.Host(new HostFault("SL5014", $"Constructor argument '{argument.Name}' produced Void.", argument.Span));
			var adapted = AdaptSecondary(evaluated.Value, argument.Adaptation, argument.Span);
			if (adapted.Failed)
				return adapted;
			if (adapted.Value is null)
				return EvalOutcome.Host(new HostFault("SL5014", $"Constructor argument '{argument.Name}' produced Void.", argument.Span));
			var adaptedType = _engine.GetTypeEntry(adapted.Value.Type);
			if (adaptedType.Kind == ShellTypeKind.Result && adapted.Value.Value is ShellResultValue.Error)
				return EvalOutcome.Success(RetypeResult(adapted.Value, expression.Type));
			arguments.Add(argument.Name, adapted.Value);
		}
		foreach (var argument in expression.Constructor.Arguments)
			if (!arguments.ContainsKey(argument.Name) && argument.DefaultValue is not null)
				arguments.Add(argument.Name, argument.DefaultValue);

		try
		{
			var values = new InvocationValues(
				new ReadOnlyDictionary<string, ShellValue>(new Dictionary<string, ShellValue>()),
				new ReadOnlyDictionary<string, ShellValue>(arguments));
			var outcome = expression.Constructor.Invoke(Context(expression.Span, path), values);
			if (outcome is ConstructorOutcome.Success success)
			{
				var valid = ValidateConstructorSuccess(success.Value, expression);
				if (valid.Failed)
					return valid;
				var declaredType = expression.Constructor.ErrorType is { } declaredError
					? _engine.ResultOf(expression.Constructor.ConstructedType, declaredError)
					: expression.Constructor.ConstructedType;
				ShellValue result = expression.Constructor.ErrorType is not null
					? new ShellValue(declaredType, new ShellResultValue.Success(valid.Value!))
					: valid.Value!;
				if (declaredType == expression.Type)
					return EvalOutcome.Success(result);
				return EvalOutcome.Success(_engine.GetTypeEntry(declaredType).Kind == ShellTypeKind.Result
					? RetypeResult(result, expression.Type)
					: new ShellValue(expression.Type, new ShellResultValue.Success(result)));
			}
			if (outcome is not ConstructorOutcome.Error error)
				return EvalOutcome.Host(new HostFault("SL5117", "Constructor returned null or an unknown outcome.", expression.Span));
			if (expression.Constructor.ErrorType is not { } declared ||
				!_engine.IsAssignable(error.Value.Type, declared))
				return EvalOutcome.Host(new HostFault("SL5119", "Constructor returned an undeclared or wrong error type.", expression.Span));
			return EvalOutcome.Success(new ShellValue(expression.Type, new ShellResultValue.Error(error.Value)));
		}
		catch (Exception ex)
		{
			return EvalOutcome.Host(new HostFault("SL5117", "Constructor delegate failed.", expression.Span, ex));
		}
	}

	private EvalOutcome ValidateConstructorSuccess(ShellValue? value, BoundConstructorExpression expression)
	{
		if (value is null || value.Type != expression.Constructor.ConstructedType)
			return EvalOutcome.Host(new HostFault("SL5118", "Constructor returned an invalid constructed value.", expression.Span));
		var entry = _engine.GetTypeEntry(value.Type);
		if (entry.Adapter is null || !entry.Adapter.IsValid(value.Value))
			return EvalOutcome.Host(new HostFault("SL5118", "Constructor returned an invalid CLR value.", expression.Span));
		return EvalOutcome.Success(value);
	}
}
