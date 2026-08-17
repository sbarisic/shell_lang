namespace ShellLang;

internal sealed partial class Evaluator
{
	private EvalOutcome EvaluateApply(BoundApplyExpression expression, IReadOnlyList<int> path)
	{
		var primary = Evaluate(expression.Primary, path);
		if (primary.Failed)
			return primary;
		if (primary.Value is null)
			return EvalOutcome.Host(new HostFault("SL5013", "Void cannot feed an operation.", expression.Span));
		if (HasBlockingOuterError(expression.Adaptation, primary.Value, out var propagated))
			return EvalOutcome.Success(RetypeResult(propagated!, expression.Type));

		if (expression.Operation is BoundPrimitiveOperation { Operator: TokenKind.AndAnd or TokenKind.OrOr } logical &&
			!ContainsArray(expression.Adaptation) && TryDirectValue(expression.Adaptation, primary.Value, out var direct) &&
			direct!.Value is bool boolean && ((logical.Operator == TokenKind.AndAnd && !boolean) || (logical.Operator == TokenKind.OrOr && boolean)))
		{
			var shortCircuit = ApplyPlan(expression.Adaptation, primary.Value, logical, new Dictionary<string, ShellValue>(), new Dictionary<string, ShellValue>(), path);
			if (shortCircuit.Failed || expression.Type == expression.Adaptation.OutputType)
				return shortCircuit;
			var shortEntry = _engine.GetTypeEntry(expression.Adaptation.OutputType);
			return shortEntry.Kind == ShellTypeKind.Result
				? EvalOutcome.Success(RetypeResult(shortCircuit.Value!, expression.Type))
				: EvalOutcome.Success(new ShellValue(expression.Type, new ShellResultValue.Success(shortCircuit.Value!)));
		}

		var inputs = new Dictionary<string, ShellValue>(StringComparer.Ordinal);
		var arguments = new Dictionary<string, ShellValue>(StringComparer.Ordinal);
		foreach (var secondary in expression.Secondary)
		{
			var evaluated = Evaluate(secondary.Expression, path);
			if (evaluated.Failed)
				return evaluated;
			if (evaluated.Value is null)
				return EvalOutcome.Host(new HostFault("SL5014", $"Secondary '{secondary.Name}' produced Void.", secondary.Span));
			var adapted = AdaptSecondary(evaluated.Value, secondary.Adaptation, secondary.Span);
			if (adapted.Failed)
				return adapted;
			if (adapted.Value is null)
				return EvalOutcome.Host(new HostFault("SL5014", $"Secondary '{secondary.Name}' produced Void.", secondary.Span));
			var adaptedEntry = _engine.GetTypeEntry(adapted.Value.Type);
			if (adaptedEntry.Kind == ShellTypeKind.Result && adapted.Value.Value is ShellResultValue.Error)
				return EvalOutcome.Success(RetypeResult(adapted.Value, expression.Type));
			(secondary.IsInput ? inputs : arguments).Add(secondary.Name, adapted.Value);
		}
		if (expression.Operation is BoundCommandOperation command)
			foreach (var argument in command.Command.Arguments)
				if (!arguments.ContainsKey(argument.Name) && argument.DefaultValue is not null)
					arguments.Add(argument.Name, argument.DefaultValue);
		if (expression.Operation is BoundQueryOperation query)
			foreach (var argument in query.Query.Arguments)
				if (!arguments.ContainsKey(argument.Name) && argument.DefaultValue is not null)
					arguments.Add(argument.Name, argument.DefaultValue);
		var applied = ApplyPlan(expression.Adaptation, primary.Value, expression.Operation, inputs, arguments, path);
		if (applied.Failed || expression.Type == expression.Adaptation.OutputType)
			return applied;
		var baseEntry = _engine.GetTypeEntry(expression.Adaptation.OutputType);
		if (baseEntry.Kind == ShellTypeKind.Result)
			return EvalOutcome.Success(RetypeResult(applied.Value!, expression.Type));
		return EvalOutcome.Success(expression.Adaptation.OutputType == _engine.Core.Void
			? new ShellValue(expression.Type, new ShellResultValue.VoidSuccess())
			: new ShellValue(expression.Type, new ShellResultValue.Success(applied.Value!)));
	}

	private bool HasBlockingOuterError(AdaptationPlan plan, ShellValue value, out ShellValue? error)
	{
		error = null;
		if (plan.Kind == AdaptationKind.Array || plan.Kind == AdaptationKind.Direct)
			return false;
		if (plan.Kind == AdaptationKind.DefaultOutput)
		{
			var record = (ShellOutputRecordValue)value.Value;
			return HasBlockingOuterError(plan.Inner!, record.Fields[plan.OutputField!], out error);
		}
		var result = (ShellResultValue)value.Value;
		if (result is ShellResultValue.Error)
		{
			error = value;
			return true;
		}
		if (result is ShellResultValue.Success success)
			return HasBlockingOuterError(plan.Inner!, success.Value, out error);
		return false;
	}

	private static bool ContainsArray(AdaptationPlan plan) => plan.Kind == AdaptationKind.Array || (plan.Inner is not null && ContainsArray(plan.Inner));

	private bool TryDirectValue(AdaptationPlan plan, ShellValue value, out ShellValue? direct)
	{
		direct = null;
		if (plan.Kind == AdaptationKind.Direct)
		{
			direct = value;
			return true;
		}
		if (plan.Kind == AdaptationKind.DefaultOutput)
			return TryDirectValue(plan.Inner!, ((ShellOutputRecordValue)value.Value).Fields[plan.OutputField!], out direct);
		if (plan.Kind == AdaptationKind.Result && value.Value is ShellResultValue.Success success)
			return TryDirectValue(plan.Inner!, success.Value, out direct);
		return false;
	}

	private EvalOutcome AdaptSecondary(ShellValue value, AdaptationPlan plan, SourceSpan span)
	{
		switch (plan.Kind)
		{
			case AdaptationKind.Direct:
				return EvalOutcome.Success(value);
			case AdaptationKind.DefaultOutput:
				return AdaptSecondary(((ShellOutputRecordValue)value.Value).Fields[plan.OutputField!], plan.Inner!, span);
			case AdaptationKind.Result:
				var result = (ShellResultValue)value.Value;
				if (result is ShellResultValue.Error)
					return EvalOutcome.Success(RetypeResult(value, plan.OutputType));
				if (result is ShellResultValue.Success success)
					return AdaptSecondary(success.Value, plan.Inner!, span);
				return EvalOutcome.Host(new HostFault("SL5015", "VoidSuccess cannot feed a value parameter.", span));
			default:
				return EvalOutcome.Host(new HostFault("SL5016", "Secondary array lifting is forbidden.", span));
		}
	}

	private EvalOutcome ApplyPlan(AdaptationPlan plan, ShellValue value, BoundOperation operation,
		IReadOnlyDictionary<string, ShellValue> inputs, IReadOnlyDictionary<string, ShellValue> arguments, IReadOnlyList<int> path)
	{
		switch (plan.Kind)
		{
			case AdaptationKind.Direct:
				return InvokeOperation(operation, value, inputs, arguments, path);
			case AdaptationKind.DefaultOutput:
				return ApplyPlan(plan.Inner!, ((ShellOutputRecordValue)value.Value).Fields[plan.OutputField!], operation, inputs, arguments, path);
			case AdaptationKind.Result:
				{
					var result = (ShellResultValue)value.Value;
					if (result is ShellResultValue.Error)
						return EvalOutcome.Success(RetypeResult(value, plan.OutputType));
					if (result is ShellResultValue.VoidSuccess)
						return EvalOutcome.Host(new HostFault("SL5015", "VoidSuccess cannot feed an operation.", operation.Span));
					var inner = ApplyPlan(plan.Inner!, ((ShellResultValue.Success)result).Value, operation, inputs, arguments, path);
					if (inner.Failed)
						return inner;
					return EvalOutcome.Success(WrapPropagated(inner.Value, plan.Inner!.OutputType, plan.OutputType));
				}
			case AdaptationKind.Array:
				return ApplyArray(plan, value, operation, inputs, arguments, path);
			default:
				throw new InvalidOperationException();
		}
	}

	private EvalOutcome ApplyArray(AdaptationPlan plan, ShellValue value, BoundOperation operation,
		IReadOnlyDictionary<string, ShellValue> inputs, IReadOnlyDictionary<string, ShellValue> arguments, IReadOnlyList<int> path)
	{
		var array = (ShellArrayValue)value.Value;
		var collected = new List<ShellValue>();
		for (var i = 0; i < array.Items.Count; i++)
		{
			var childPath = Append(path, i);
			var outcome = ApplyPlan(plan.Inner!, array.Items[i], operation, inputs, arguments, childPath);
			if (outcome.RuntimeFault is { } rf)
				return EvalOutcome.Runtime(operation is BoundCommandOperation ? rf : AddIndex(rf, i));
			if (outcome.HostFault is { } hf)
				return EvalOutcome.Host(AddIndex(hf, i));
			if (plan.Inner!.OutputType == _engine.Core.Void)
				continue;
			var innerEntry = _engine.GetTypeEntry(plan.Inner.OutputType);
			if (innerEntry.Kind == ShellTypeKind.Result)
			{
				var result = (ShellResultValue)outcome.Value!.Value;
				if (result is ShellResultValue.Error error)
				{
					var frames = new[] { new ErrorContextFrame("array", i.ToString(), operation.Span, i) }.Concat(error.Frames).ToArray();
					return EvalOutcome.Success(new ShellValue(plan.OutputType, new ShellResultValue.Error(error.Value, frames)));
				}
				if (result is ShellResultValue.Success success)
					collected.Add(success.Value);
			}
			else if (outcome.Value is not null)
				collected.Add(outcome.Value);
		}
		if (plan.OutputType == _engine.Core.Void)
			return EvalOutcome.Success(null);
		var outputEntry = _engine.GetTypeEntry(plan.OutputType);
		if (outputEntry.Kind == ShellTypeKind.Result)
		{
			if (outputEntry.SuccessType == _engine.Core.Void)
				return EvalOutcome.Success(new ShellValue(plan.OutputType, new ShellResultValue.VoidSuccess()));
			var arrayType = outputEntry.SuccessType!.Value;
			return EvalOutcome.Success(new ShellValue(plan.OutputType, new ShellResultValue.Success(new ShellValue(arrayType, new ShellArrayValue(collected)))));
		}
		return EvalOutcome.Success(new ShellValue(plan.OutputType, new ShellArrayValue(collected)));
	}
}
