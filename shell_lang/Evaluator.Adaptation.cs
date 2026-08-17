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
			direct!.Value is bool boolean && ((logical.Operator == TokenKind.AndAnd && !boolean) ||
			(logical.Operator == TokenKind.OrOr && boolean)))
		{
			var shortCircuit = ApplyPlan(expression.Adaptation, primary.Value, logical,
				new Dictionary<string, ShellValue>(), new Dictionary<string, ShellValue>(), path, []);
			if (shortCircuit.Failed || expression.Type == expression.Adaptation.OutputType)
				return shortCircuit;
			var shortEntry = _engine.GetTypeEntry(expression.Adaptation.OutputType);
			return shortEntry.Kind == ShellTypeKind.Result
				? EvalOutcome.Success(RetypeResult(shortCircuit.Value!, expression.Type))
				: EvalOutcome.Success(new ShellValue(expression.Type, new ShellResultValue.Success(shortCircuit.Value!)));
		}

		var inputs = new Dictionary<string, ShellValue>(StringComparer.Ordinal);
		var arguments = new Dictionary<string, ShellValue>(StringComparer.Ordinal);
		foreach (var secondary in expression.Secondary.Where(x => x.ContextScopeId is null))
		{
			var secondaryOutcome = EvaluateSecondary(secondary, inputs, arguments, path);
			if (secondaryOutcome is not null)
				return NormalizeSecondaryFailure(secondaryOutcome, expression.Type);
		}
		AddDefaults(expression.Operation, arguments);
		var contextual = expression.Secondary.Where(x => x.ContextScopeId is not null).ToArray();
		var applied = ApplyPlan(expression.Adaptation, primary.Value, expression.Operation,
			inputs, arguments, path, contextual);
		if (applied.Failed || expression.Type == expression.Adaptation.OutputType)
			return applied;
		var baseEntry = _engine.GetTypeEntry(expression.Adaptation.OutputType);
		if (baseEntry.Kind == ShellTypeKind.Result)
			return EvalOutcome.Success(RetypeResult(applied.Value!, expression.Type));
		return EvalOutcome.Success(expression.Adaptation.OutputType == _engine.Core.Void
			? new ShellValue(expression.Type, new ShellResultValue.VoidSuccess())
			: new ShellValue(expression.Type, new ShellResultValue.Success(applied.Value!)));
	}

	private EvalOutcome? EvaluateSecondary(BoundSecondary secondary, IDictionary<string, ShellValue> inputs,
		IDictionary<string, ShellValue> arguments, IReadOnlyList<int> path)
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
			return adapted;
		(secondary.IsInput ? inputs : arguments).Add(secondary.Name, adapted.Value);
		return null;
	}

	private EvalOutcome NormalizeSecondaryFailure(EvalOutcome outcome, ShellTypeId outputType) =>
		outcome.Value is not null && _engine.GetTypeEntry(outcome.Value.Type).Kind == ShellTypeKind.Result &&
		outcome.Value.Value is ShellResultValue.Error
			? EvalOutcome.Success(RetypeResult(outcome.Value, outputType))
			: outcome;

	private static void AddDefaults(BoundOperation operation, IDictionary<string, ShellValue> arguments)
	{
		IEnumerable<ArgumentDescriptor> defaults = operation switch
		{
			BoundCommandOperation command => command.Command.Arguments,
			BoundQueryOperation query => query.Query.Arguments,
			_ => []
		};
		foreach (var argument in defaults)
			if (!arguments.ContainsKey(argument.Name) && argument.DefaultValue is not null)
				arguments.Add(argument.Name, argument.DefaultValue);
	}

	private bool HasBlockingOuterError(AdaptationPlan plan, ShellValue value, out ShellValue? error)
	{
		error = null;
		if (plan.Kind == AdaptationKind.Array || plan.Kind == AdaptationKind.Direct)
			return false;
		if (plan.Kind == AdaptationKind.DefaultOutput)
			return HasBlockingOuterError(plan.Inner!, ((ShellOutputRecordValue)value.Value).Fields[plan.OutputField!], out error);
		var result = (ShellResultValue)value.Value;
		if (result is ShellResultValue.Error)
		{
			error = value;
			return true;
		}
		return result is ShellResultValue.Success success && HasBlockingOuterError(plan.Inner!, success.Value, out error);
	}

	private static bool ContainsArray(AdaptationPlan plan) =>
		plan.Kind == AdaptationKind.Array || plan.Inner is not null && ContainsArray(plan.Inner);

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
		IReadOnlyDictionary<string, ShellValue> inputs, IReadOnlyDictionary<string, ShellValue> arguments,
		IReadOnlyList<int> path, IReadOnlyList<BoundSecondary> contextual)
	{
		switch (plan.Kind)
		{
			case AdaptationKind.Direct:
				return InvokeDirect(plan, value, operation, inputs, arguments, path, contextual);
			case AdaptationKind.DefaultOutput:
				return ApplyPlan(plan.Inner!, ((ShellOutputRecordValue)value.Value).Fields[plan.OutputField!],
					operation, inputs, arguments, path, contextual);
			case AdaptationKind.Result:
				{
					var result = (ShellResultValue)value.Value;
					if (result is ShellResultValue.Error)
						return EvalOutcome.Success(RetypeResult(value, plan.OutputType));
					if (result is ShellResultValue.VoidSuccess)
						return EvalOutcome.Host(new HostFault("SL5015", "VoidSuccess cannot feed an operation.", operation.Span));
					var inner = ApplyPlan(plan.Inner!, ((ShellResultValue.Success)result).Value,
						operation, inputs, arguments, path, contextual);
					if (inner.Failed)
						return inner;
					return EvalOutcome.Success(WrapPropagated(inner.Value, plan.Inner!.OutputType, plan.OutputType));
				}
			case AdaptationKind.Array:
				return ApplyArray(plan, value, operation, inputs, arguments, path, contextual);
			default:
				throw new InvalidOperationException();
		}
	}

	private EvalOutcome InvokeDirect(AdaptationPlan plan, ShellValue primary, BoundOperation operation,
		IReadOnlyDictionary<string, ShellValue> suppliedInputs,
		IReadOnlyDictionary<string, ShellValue> suppliedArguments, IReadOnlyList<int> path,
		IReadOnlyList<BoundSecondary> contextual)
	{
		EvalOutcome Invoke()
		{
			var inputs = new Dictionary<string, ShellValue>(suppliedInputs, StringComparer.Ordinal);
			var arguments = new Dictionary<string, ShellValue>(suppliedArguments, StringComparer.Ordinal);
			foreach (var secondary in contextual)
			{
				var secondaryOutcome = EvaluateSecondary(secondary, inputs, arguments, path);
				if (secondaryOutcome is not null)
				{
					if (secondaryOutcome.RuntimeFault is { Context.Count: 0 } runtime &&
						operation is BoundCommandOperation)
						return EvalOutcome.Runtime(new RuntimeFault(runtime.Code, runtime.Message, runtime.Source,
							path.Select(index => new ErrorContextFrame("array", index.ToString(), runtime.Source, index)).ToArray()));
					return NormalizeSecondaryFailure(secondaryOutcome, plan.OutputType);
				}
			}
			AddDefaults(operation, arguments);
			var outcome = InvokeOperation(operation, primary, inputs, arguments, path);
			if (outcome.Failed || plan.OutputType == operation.DirectOutput)
				return outcome;
			if (_engine.GetTypeEntry(operation.DirectOutput).Kind == ShellTypeKind.Result)
				return EvalOutcome.Success(RetypeResult(outcome.Value!, plan.OutputType));
			return EvalOutcome.Success(operation.DirectOutput == _engine.Core.Void
				? new ShellValue(plan.OutputType, new ShellResultValue.VoidSuccess())
				: new ShellValue(plan.OutputType, new ShellResultValue.Success(outcome.Value!)));
		}

		return contextual.FirstOrDefault()?.ContextScopeId is { } scopeId
			? WithContext(scopeId, primary, Invoke)
			: Invoke();
	}

	private EvalOutcome ApplyArray(AdaptationPlan plan, ShellValue value, BoundOperation operation,
		IReadOnlyDictionary<string, ShellValue> inputs, IReadOnlyDictionary<string, ShellValue> arguments,
		IReadOnlyList<int> path, IReadOnlyList<BoundSecondary> contextual)
	{
		var array = (ShellArrayValue)value.Value;
		var collected = new List<ShellValue>();
		for (var i = 0; i < array.Items.Count; i++)
		{
			var childPath = Append(path, i);
			var outcome = ApplyPlan(plan.Inner!, array.Items[i], operation, inputs, arguments, childPath, contextual);
			if (outcome.RuntimeFault is { } runtime)
				return EvalOutcome.Runtime(operation is BoundCommandOperation ? runtime : AddIndex(runtime, i));
			if (outcome.HostFault is { } host)
				return EvalOutcome.Host(AddIndex(host, i));
			if (plan.Inner!.OutputType == _engine.Core.Void)
				continue;
			var innerEntry = _engine.GetTypeEntry(plan.Inner.OutputType);
			if (innerEntry.Kind == ShellTypeKind.Result)
			{
				var result = (ShellResultValue)outcome.Value!.Value;
				if (result is ShellResultValue.Error error)
				{
					var frames = new[] { new ErrorContextFrame("array", i.ToString(), operation.Span, i) }
						.Concat(error.Frames).ToArray();
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
			return EvalOutcome.Success(new ShellValue(plan.OutputType,
				new ShellResultValue.Success(new ShellValue(arrayType, new ShellArrayValue(collected)))));
		}
		return EvalOutcome.Success(new ShellValue(plan.OutputType, new ShellArrayValue(collected)));
	}
}
