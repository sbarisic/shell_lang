using System.Collections.ObjectModel;

namespace ShellLang;

internal sealed class EvalOutcome
{
	private EvalOutcome(ShellValue? value, RuntimeFault? runtimeFault, HostFault? hostFault)
	{
		Value = value;
		RuntimeFault = runtimeFault;
		HostFault = hostFault;
	}
	public ShellValue? Value
	{
		get;
	}
	public RuntimeFault? RuntimeFault
	{
		get;
	}
	public HostFault? HostFault
	{
		get;
	}
	public bool Failed => RuntimeFault is not null || HostFault is not null;
	public static EvalOutcome Success(ShellValue? value) => new(value, null, null);
	public static EvalOutcome Runtime(RuntimeFault value) => new(null, value, null);
	public static EvalOutcome Host(HostFault value) => new(null, null, value);
}

internal sealed class Evaluator
{
	private readonly ShellEngine _engine;
	private readonly ShellSession _session;
	private readonly IServiceProvider _services;
	private readonly ExecutionOptions? _options;
	private ShellValue? _contextValue;

	public Evaluator(ShellEngine engine, ShellSession session, IServiceProvider services, ExecutionOptions? options)
	{
		_engine = engine;
		_session = session;
		_services = services;
		_options = options;
	}

	public ExecutionResult Execute(BoundProgram program)
	{
		ShellValue? final = null;
		var completed = 0;
		for (var i = 0; i < program.Statements.Count; i++)
		{
			var statement = program.Statements[i];
			var expression = statement switch
			{
				BoundAssignment a => a.Expression,
				BoundExpressionStatement e => e.Expression,
				_ => throw new InvalidOperationException()
			};
			var outcome = Evaluate(expression, []);
			if (outcome.RuntimeFault is { } runtime)
				return new(ExecutionStatus.RuntimeFault, null, runtime, null, completed);
			if (outcome.HostFault is { } host)
				return new(ExecutionStatus.HostFault, null, null, host, completed);
			if (statement is BoundAssignment assignment)
			{
				if (outcome.Value is null)
					return HostResult("SL5006", "An assignment produced Void.", statement.Span, completed);
				_session.CommitBinding(assignment.Name, outcome.Value);
				final = null;
			}
			else
				final = outcome.Value;
			completed++;
			if (_options?.Observer is { } observer)
			{
				try
				{
					observer.StatementCompleted(i, statement.Span, statement is BoundAssignment ? null : final);
				}
				catch (Exception ex) { return HostResult("SL5007", "The execution observer failed.", statement.Span, completed, ex); }
			}
		}
		return new ExecutionResult(ExecutionStatus.Completed, final, null, null, completed);
	}

	private EvalOutcome Evaluate(BoundExpression expression, IReadOnlyList<int> path)
	{
		try
		{
			return expression switch
			{
				BoundErrorExpression => EvalOutcome.Host(new HostFault("SL5008", "An invalid bound node reached execution.", expression.Span)),
				BoundLiteralExpression literal => EvalOutcome.Success(literal.Value),
				BoundNameExpression name => EvaluateName(name, path),
				BoundArrayExpression array => EvaluateArray(array, path),
				BoundUnaryExpression unary => EvaluateUnary(unary, path),
				BoundBinaryExpression binary => EvaluateBinary(binary, path),
				BoundApplyExpression apply => EvaluateApply(apply, path),
				_ => EvalOutcome.Host(new HostFault("SL5008", "Unknown bound expression.", expression.Span))
			};
		}
		catch (Exception ex)
		{
			return EvalOutcome.Host(new HostFault("SL5009", "Unexpected runtime implementation failure.", expression.Span, ex));
		}
	}

	private EvalOutcome EvaluateName(BoundNameExpression expression, IReadOnlyList<int> path)
	{
		if (expression.Name == ".")
			return _contextValue is null
			? EvalOutcome.Host(new HostFault("SL5010", "No contextual element is active.", expression.Span))
			: EvalOutcome.Success(_contextValue);
		if (!expression.IsGlobal)
			return _session.TryGetBinding(expression.Name, out var sessionValue)
				? EvalOutcome.Success(sessionValue)
				: EvalOutcome.Host(new HostFault("SL5004", $"Session binding '{expression.Name}' is missing.", expression.Span));
		if (!_engine.Globals.TryGetValue(expression.Name, out var global))
			return EvalOutcome.Host(new HostFault("SL5011", $"Global '{expression.Name}' is missing.", expression.Span));
		try
		{
			var context = Context(expression.Span, path);
			var value = global.GetValue(context);
			return ValidateValue(value, global.Type, expression.Span, $"global '{global.Name}'");
		}
		catch (Exception ex) { return EvalOutcome.Host(new HostFault("SL5101", $"Global '{global.Name}' failed.", expression.Span, ex)); }
	}

	private EvalOutcome EvaluateArray(BoundArrayExpression expression, IReadOnlyList<int> path)
	{
		var values = new List<ShellValue>();
		for (var i = 0; i < expression.Items.Count; i++)
		{
			var outcome = Evaluate(expression.Items[i], Append(path, i));
			if (outcome.Failed)
				return outcome;
			if (outcome.Value is null)
				return EvalOutcome.Host(new HostFault("SL5012", "An array item produced Void.", expression.Items[i].Span));
			values.Add(outcome.Value);
		}
		var element = _engine.GetTypeEntry(expression.Type).ElementType!.Value;
		return EvalOutcome.Success(new ShellValue(expression.Type, new ShellArrayValue(values)));
	}

	private EvalOutcome EvaluateUnary(BoundUnaryExpression expression, IReadOnlyList<int> path)
	{
		var operand = Evaluate(expression.Operand, path);
		if (operand.Failed)
			return operand;
		try
		{
			object value = expression.Operator switch
			{
				TokenKind.Bang => !(bool)operand.Value!.Value,
				TokenKind.Minus when expression.Type == _engine.Core.Int32 => checked(-(int)operand.Value!.Value),
				TokenKind.Minus when expression.Type == _engine.Core.Int64 => checked(-(long)operand.Value!.Value),
				TokenKind.Minus when expression.Type == _engine.Core.Float32 => -(float)operand.Value!.Value,
				TokenKind.Minus => -(double)operand.Value!.Value,
				_ => throw new InvalidOperationException()
			};
			return EvalOutcome.Success(_engine.CreateValue(expression.Type, value));
		}
		catch (OverflowException) { return CoreFault("SL4002", "Integer overflow.", expression.Span); }
	}

	private EvalOutcome EvaluateBinary(BoundBinaryExpression expression, IReadOnlyList<int> path)
	{
		var left = Evaluate(expression.Left, path);
		if (left.Failed)
			return left;
		if (expression.Operator == TokenKind.AndAnd && left.Value!.Value is false)
			return EvalOutcome.Success(_engine.CreateValue(_engine.Core.Bool, false));
		if (expression.Operator == TokenKind.OrOr && left.Value!.Value is true)
			return EvalOutcome.Success(_engine.CreateValue(_engine.Core.Bool, true));
		var right = Evaluate(expression.Right, path);
		if (right.Failed)
			return right;
		try
		{
			var l = left.Value!;
			var r = right.Value!;
			object value;
			if (expression.Operator is TokenKind.EqualEqual or TokenKind.BangEqual)
			{
				var equality = _engine.GetTypeEntry(l.Type).Equality!;
				var equal = equality.CompareEqual(l.Value, r.Value);
				value = expression.Operator == TokenKind.EqualEqual ? equal : !equal;
			}
			else if (expression.Operator is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
			{
				var compare = _engine.GetTypeEntry(l.Type).Ordering!.Compare(l.Value, r.Value);
				value = expression.Operator switch
				{
					TokenKind.Less => compare < 0,
					TokenKind.LessEqual => compare <= 0,
					TokenKind.Greater => compare > 0,
					_ => compare >= 0
				};
			}
			else if (expression.Operator is TokenKind.AndAnd or TokenKind.OrOr)
				value = expression.Operator == TokenKind.AndAnd ? (bool)l.Value && (bool)r.Value : (bool)l.Value || (bool)r.Value;
			else
				value = Arithmetic(expression.Operator, l.Type, l.Value, r.Value, expression.Span);
			return value is EvalOutcome fault ? fault : EvalOutcome.Success(_engine.CreateValue(expression.Type, value));
		}
		catch (OverflowException) { return CoreFault("SL4002", "Integer overflow.", expression.Span); }
		catch (DivideByZeroException) { return CoreFault("SL4003", "Division by zero.", expression.Span); }
		catch (Exception ex) { return EvalOutcome.Host(new HostFault("SL5102", "A registered comparison failed.", expression.Span, ex)); }
	}

	private object Arithmetic(TokenKind op, ShellTypeId type, object l, object r, SourceSpan span)
	{
		if ((op is TokenKind.Slash or TokenKind.Percent) && IsZero(type, r))
			return CoreFault("SL4003", "Division by zero.", span);
		checked
		{
			if (type == _engine.Core.Int32)
				return op switch
				{
					TokenKind.Plus => (int)l + (int)r,
					TokenKind.Minus => (int)l - (int)r,
					TokenKind.Star => (int)l * (int)r,
					TokenKind.Slash => (int)l / (int)r,
					_ => (int)l % (int)r
				};
			if (type == _engine.Core.Int64)
				return op switch
				{
					TokenKind.Plus => (long)l + (long)r,
					TokenKind.Minus => (long)l - (long)r,
					TokenKind.Star => (long)l * (long)r,
					TokenKind.Slash => (long)l / (long)r,
					_ => (long)l % (long)r
				};
			if (type == _engine.Core.UInt32)
				return op switch
				{
					TokenKind.Plus => (uint)l + (uint)r,
					TokenKind.Minus => (uint)l - (uint)r,
					TokenKind.Star => (uint)l * (uint)r,
					TokenKind.Slash => (uint)l / (uint)r,
					_ => (uint)l % (uint)r
				};
			if (type == _engine.Core.UInt64)
				return op switch
				{
					TokenKind.Plus => (ulong)l + (ulong)r,
					TokenKind.Minus => (ulong)l - (ulong)r,
					TokenKind.Star => (ulong)l * (ulong)r,
					TokenKind.Slash => (ulong)l / (ulong)r,
					_ => (ulong)l % (ulong)r
				};
		}
		if (type == _engine.Core.Float32)
			return op switch
			{
				TokenKind.Plus => (float)l + (float)r,
				TokenKind.Minus => (float)l - (float)r,
				TokenKind.Star => (float)l * (float)r,
				_ => (float)l / (float)r
			};
		return op switch
		{
			TokenKind.Plus => (double)l + (double)r,
			TokenKind.Minus => (double)l - (double)r,
			TokenKind.Star => (double)l * (double)r,
			_ => (double)l / (double)r
		};
	}

	private bool IsZero(ShellTypeId type, object value) => type == _engine.Core.Int32 ? (int)value == 0 : type == _engine.Core.Int64 ? (long)value == 0 :
		type == _engine.Core.UInt32 ? (uint)value == 0 : type == _engine.Core.UInt64 ? (ulong)value == 0 :
		type == _engine.Core.Float32 ? (float)value == 0 : (double)value == 0;

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

	private EvalOutcome InvokeOperation(BoundOperation operation, ShellValue primary,
		IReadOnlyDictionary<string, ShellValue> suppliedInputs, IReadOnlyDictionary<string, ShellValue> suppliedArguments, IReadOnlyList<int> path)
	{
		return operation switch
		{
			BoundCommandOperation command => InvokeCommand(command, primary, suppliedInputs, suppliedArguments, path),
			BoundMemberOperation member => InvokeMember(member, primary, path),
			BoundQueryOperation query => InvokeQuery(query, primary, suppliedArguments, path),
			BoundPrimitiveOperation primitive => InvokePrimitive(primitive, primary, suppliedArguments),
			BoundIntrinsicOperation intrinsic => InvokeIntrinsic(intrinsic, primary, suppliedArguments, path),
			_ => EvalOutcome.Host(new HostFault("SL5017", "Unknown operation.", operation.Span))
		};
	}

	private EvalOutcome InvokePrimitive(BoundPrimitiveOperation operation, ShellValue left, IReadOnlyDictionary<string, ShellValue> arguments)
	{
		if (!arguments.TryGetValue("right", out var right))
		{
			try
			{
				object unary = operation.Operator switch
				{
					TokenKind.AndAnd or TokenKind.OrOr => left.Get<bool>(),
					TokenKind.Bang => !left.Get<bool>(),
					TokenKind.Minus when left.Type == _engine.Core.Int32 => checked(-left.Get<int>()),
					TokenKind.Minus when left.Type == _engine.Core.Int64 => checked(-left.Get<long>()),
					TokenKind.Minus when left.Type == _engine.Core.Float32 => -left.Get<float>(),
					TokenKind.Minus => -left.Get<double>(),
					_ => throw new InvalidOperationException()
				};
				return EvalOutcome.Success(_engine.CreateValue(operation.DirectOutput, unary));
			}
			catch (OverflowException) { return CoreFault("SL4002", "Integer overflow.", operation.Span); }
		}
		try
		{
			object value;
			if (operation.Operator is TokenKind.EqualEqual or TokenKind.BangEqual)
			{
				var equal = _engine.GetTypeEntry(left.Type).Equality!.CompareEqual(left.Value, right.Value);
				value = operation.Operator == TokenKind.EqualEqual ? equal : !equal;
			}
			else if (operation.Operator is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
			{
				var compare = _engine.GetTypeEntry(left.Type).Ordering!.Compare(left.Value, right.Value);
				value = operation.Operator switch
				{
					TokenKind.Less => compare < 0,
					TokenKind.LessEqual => compare <= 0,
					TokenKind.Greater => compare > 0,
					_ => compare >= 0
				};
			}
			else if (operation.Operator is TokenKind.AndAnd or TokenKind.OrOr)
				value = operation.Operator == TokenKind.AndAnd ? left.Get<bool>() && right.Get<bool>() : left.Get<bool>() || right.Get<bool>();
			else
				value = Arithmetic(operation.Operator, left.Type, left.Value, right.Value, operation.Span);
			return value is EvalOutcome fault ? fault : EvalOutcome.Success(_engine.CreateValue(operation.DirectOutput, value));
		}
		catch (OverflowException) { return CoreFault("SL4002", "Integer overflow.", operation.Span); }
		catch (DivideByZeroException) { return CoreFault("SL4003", "Division by zero.", operation.Span); }
		catch (Exception ex) { return EvalOutcome.Host(new HostFault("SL5102", "A registered primitive operation failed.", operation.Span, ex)); }
	}

	private EvalOutcome InvokeMember(BoundMemberOperation operation, ShellValue receiver, IReadOnlyList<int> path)
	{
		if (operation.OutputField is { } field)
			return EvalOutcome.Success(((ShellOutputRecordValue)receiver.Value).Fields[field]);
		try
		{
			var value = operation.Member!.GetValue(Context(operation.Span, path), receiver);
			return ValidateValue(value, operation.Member.ValueType, operation.Span, $"member '{operation.Member.Name}'");
		}
		catch (Exception ex) { return EvalOutcome.Host(new HostFault("SL5103", $"Member '{operation.Member!.Name}' failed.", operation.Span, ex)); }
	}

	private EvalOutcome InvokeQuery(BoundQueryOperation operation, ShellValue receiver,
		IReadOnlyDictionary<string, ShellValue> arguments, IReadOnlyList<int> path)
	{
		try
		{
			var values = new InvocationValues(new ReadOnlyDictionary<string, ShellValue>(new Dictionary<string, ShellValue>()), arguments);
			var outcome = operation.Query.Invoke(Context(operation.Span, path), receiver, values);
			if (outcome is QueryOutcome.Success success)
			{
				var valid = ValidateValue(success.Value, operation.Query.OutputType, operation.Span, $"query '{operation.Query.Name}'");
				if (valid.Failed)
					return valid;
				return operation.Query.ErrorType is { } error
					? EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Success(valid.Value!))) : valid;
			}
			if (operation.Query.ErrorType is not { } declared)
				return EvalOutcome.Host(new HostFault("SL5104", $"Query '{operation.Query.Name}' returned an undeclared error.", operation.Span));
			var errorValue = ((QueryOutcome.Error)outcome).Value;
			if (!_engine.IsAssignable(errorValue.Type, declared))
				return EvalOutcome.Host(new HostFault("SL5105", $"Query '{operation.Query.Name}' returned the wrong error type.", operation.Span));
			return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Error(errorValue)));
		}
		catch (Exception ex) { return EvalOutcome.Host(new HostFault("SL5106", $"Query '{operation.Query.Name}' failed.", operation.Span, ex)); }
	}

	private EvalOutcome InvokeCommand(BoundCommandOperation operation, ShellValue primary,
		IReadOnlyDictionary<string, ShellValue> suppliedInputs, IReadOnlyDictionary<string, ShellValue> arguments, IReadOnlyList<int> path)
	{
		var inputs = new Dictionary<string, ShellValue>(suppliedInputs, StringComparer.Ordinal);
		if (operation.PrimaryPort is not null)
			inputs[operation.PrimaryPort] = primary;
		try
		{
			var values = new InvocationValues(new ReadOnlyDictionary<string, ShellValue>(inputs), arguments);
			var outcome = operation.Command.Invoke(Context(operation.Span, path), values);
			switch (outcome)
			{
				case CommandOutcome.Fault fault:
					if (!operation.Command.RuntimeFaults.Contains(fault.Code) || string.IsNullOrWhiteSpace(fault.Message))
						return EvalOutcome.Host(new HostFault("SL5107", $"Command '{operation.Command.Name}' returned an undeclared or invalid runtime fault.", operation.Span));
					return EvalOutcome.Runtime(new RuntimeFault(fault.Code, fault.Message, operation.Span,
						path.Select(i => new ErrorContextFrame("array", i.ToString(), operation.Span, i)).ToArray()));
				case CommandOutcome.Error error:
					if (operation.Command.ErrorType is not { } declared || !_engine.IsAssignable(error.Value.Type, declared))
						return EvalOutcome.Host(new HostFault("SL5108", $"Command '{operation.Command.Name}' returned an undeclared or invalid typed error.", operation.Span));
					return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Error(error.Value)));
				case CommandOutcome.Success success:
					return ValidateCommandSuccess(operation, success);
				default:
					return EvalOutcome.Host(new HostFault("SL5109", $"Command '{operation.Command.Name}' returned null or an unknown outcome.", operation.Span));
			}
		}
		catch (Exception ex) { return EvalOutcome.Host(new HostFault("SL5110", $"Command '{operation.Command.Name}' threw an exception.", operation.Span, ex)); }
	}

	private EvalOutcome ValidateCommandSuccess(BoundCommandOperation operation, CommandOutcome.Success success)
	{
		if (success.Outputs.Count != operation.Command.Outputs.Count || operation.Command.Outputs.Any(x => !success.Outputs.ContainsKey(x.Name)) || success.Outputs.Keys.Any(x => operation.Command.Outputs.All(o => o.Name != x)))
			return EvalOutcome.Host(new HostFault("SL5111", $"Command '{operation.Command.Name}' returned an invalid output set.", operation.Span));
		foreach (var output in operation.Command.Outputs)
		{
			var valid = ValidateValue(success.Outputs[output.Name], output.Type, operation.Span, $"output '{operation.Command.Name}.{output.Name}'");
			if (valid.Failed)
				return valid;
		}
		ShellValue? value = operation.Command.Outputs.Count switch
		{
			0 => null,
			1 => success.Outputs[operation.Command.Outputs[0].Name],
			_ => new ShellValue(operation.Command.OutputRecordType!.Value, new ShellOutputRecordValue(success.Outputs))
		};
		if (operation.Command.ErrorType is not { })
			return EvalOutcome.Success(value);
		return value is null
			? EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.VoidSuccess()))
			: EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Success(value)));
	}

	private EvalOutcome InvokeIntrinsic(BoundIntrinsicOperation operation, ShellValue primary,
		IReadOnlyDictionary<string, ShellValue> arguments, IReadOnlyList<int> path)
	{
		if (operation.Intrinsic is IntrinsicKind.Require or IntrinsicKind.ValueOr or IntrinsicKind.Error or IntrinsicKind.IsOk)
			return InvokeResultIntrinsic(operation, primary, arguments);
		var array = (ShellArrayValue)primary.Value;
		switch (operation.Intrinsic)
		{
			case IntrinsicKind.Count:
				return EvalOutcome.Success(_engine.CreateValue(_engine.Core.Int32, array.Items.Count));
			case IntrinsicKind.Take:
				var count = arguments["count"].Get<int>();
				if (count < 0)
					return CoreFault("SL4004", "take count cannot be negative.", operation.Span);
				return EvalOutcome.Success(new ShellValue(primary.Type, new ShellArrayValue(array.Items.Take(count))));
			case IntrinsicKind.Skip:
				count = arguments["count"].Get<int>();
				if (count < 0)
					return CoreFault("SL4004", "skip count cannot be negative.", operation.Span);
				return EvalOutcome.Success(new ShellValue(primary.Type, new ShellArrayValue(array.Items.Skip(count))));
			case IntrinsicKind.At:
				return At(operation, array, arguments["index"].Get<int>());
			case IntrinsicKind.Slice:
				return Slice(operation, primary, array, arguments["start"].Get<int>(), arguments["count"].Get<int>());
			case IntrinsicKind.First:
				return EmptyOrValue(operation, array, array.Items.FirstOrDefault());
			case IntrinsicKind.Last:
				return EmptyOrValue(operation, array, array.Items.LastOrDefault());
			case IntrinsicKind.Single:
				return Single(operation, array);
			case IntrinsicKind.Reverse:
				return EvalOutcome.Success(new ShellValue(primary.Type, new ShellArrayValue(array.Items.Reverse())));
			case IntrinsicKind.Concat:
				var other = (ShellArrayValue)arguments["other"].Value;
				return EvalOutcome.Success(new ShellValue(primary.Type, new ShellArrayValue(array.Items.Concat(other.Items))));
			case IntrinsicKind.Contains:
				return Contains(operation, primary, array, arguments["value"]);
			case IntrinsicKind.Distinct when operation.ContextExpression is null:
				return Distinct(operation, primary, array);
			case IntrinsicKind.Min:
				return EmptyOrValue(operation, array, Extreme(array, false, operation.Span));
			case IntrinsicKind.Max:
				return EmptyOrValue(operation, array, Extreme(array, true, operation.Span));
			case IntrinsicKind.Sum:
				try
				{
					return EvalOutcome.Success(Sum(array, _engine.GetTypeEntry(primary.Type).ElementType!.Value));
				}
				catch (OverflowException) { return CoreFault("SL4002", "Integer overflow in sum.", operation.Span); }
			case IntrinsicKind.Average:
				return Average(operation, array);
			case IntrinsicKind.Where:
				return ContextWhere(operation, primary, array, path);
			case IntrinsicKind.Sort:
				return ContextSort(operation, primary, array, path);
			case IntrinsicKind.Any:
				return ContextBoolean(operation, array, path, any: true);
			case IntrinsicKind.All:
				return ContextBoolean(operation, array, path, any: false);
			case IntrinsicKind.Select:
				return ContextSelect(operation, array, path);
			case IntrinsicKind.Distinct:
				return ContextDistinct(operation, primary, array, path);
			default:
				return EvalOutcome.Host(new HostFault("SL5112", "Unknown intrinsic.", operation.Span));
		}
	}

	private EvalOutcome InvokeResultIntrinsic(BoundIntrinsicOperation operation, ShellValue primary, IReadOnlyDictionary<string, ShellValue> arguments)
	{
		var result = (ShellResultValue)primary.Value;
		switch (operation.Intrinsic)
		{
			case IntrinsicKind.IsOk:
				return EvalOutcome.Success(_engine.CreateValue(_engine.Core.Bool, result is not ShellResultValue.Error));
			case IntrinsicKind.Require:
				if (result is ShellResultValue.Error error)
					return CoreFault("SL4001", error.Value.ToString(), operation.Span);
				return result is ShellResultValue.VoidSuccess ? EvalOutcome.Success(null) : EvalOutcome.Success(((ShellResultValue.Success)result).Value);
			case IntrinsicKind.ValueOr:
				return EvalOutcome.Success(result is ShellResultValue.Error ? arguments["default"] : ((ShellResultValue.Success)result).Value);
			case IntrinsicKind.Error:
				return result is ShellResultValue.Error e ? EvalOutcome.Success(e.Value) : CoreFault("SL4005", "error requires an Err value.", operation.Span);
			default:
				throw new InvalidOperationException();
		}
	}

	private EvalOutcome EmptyOrValue(BoundIntrinsicOperation operation, ShellArrayValue array, ShellValue? value)
	{
		var resultType = operation.DirectOutput;
		if (array.Items.Count == 0)
		{
			var error = _engine.CreateValue(_engine.Core.EmptyCollectionError, new EmptyCollectionError());
			return EvalOutcome.Success(new ShellValue(resultType, new ShellResultValue.Error(error)));
		}
		return EvalOutcome.Success(new ShellValue(resultType, new ShellResultValue.Success(value!)));
	}

	private EvalOutcome At(BoundIntrinsicOperation operation, ShellArrayValue array, int index)
	{
		var normalized = index < 0 ? array.Items.Count + index : index;
		if (normalized < 0 || normalized >= array.Items.Count)
			return CoreFault("SL4006", $"Array index {index} is outside an array of length {array.Items.Count}.", operation.Span);
		var item = array.Items[normalized];
		return EvalOutcome.Success(item.Type == operation.DirectOutput
			? item
			: _engine.CreateValue(operation.DirectOutput, item.Value));
	}

	private EvalOutcome Slice(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array, int start, int count)
	{
		if (count < 0)
			return CoreFault("SL4004", "slice count cannot be negative.", operation.Span);
		var normalized = start < 0 ? array.Items.Count + start : start;
		if (normalized < 0 || normalized > array.Items.Count || count > array.Items.Count - normalized)
			return CoreFault("SL4007", $"Array slice ({start}, {count}) is outside an array of length {array.Items.Count}.", operation.Span);
		return EvalOutcome.Success(new ShellValue(primary.Type, new ShellArrayValue(array.Items.Skip(normalized).Take(count))));
	}

	private EvalOutcome Single(BoundIntrinsicOperation operation, ShellArrayValue array)
	{
		if (array.Items.Count == 1)
			return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Success(array.Items[0])));
		var message = $"Expected exactly one element, but the collection contains {array.Items.Count}.";
		var error = _engine.CreateValue(_engine.Core.CollectionCardinalityError,
			new CollectionCardinalityError(array.Items.Count, message));
		return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Error(error)));
	}

	private EvalOutcome Contains(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array, ShellValue expected)
	{
		var element = _engine.GetTypeEntry(primary.Type).ElementType!.Value;
		var equality = _engine.GetTypeEntry(element).Equality!;
		try
		{
			foreach (var item in array.Items)
				if (equality.CompareEqual(item.Value, expected.Value))
					return EvalOutcome.Success(_engine.CreateValue(_engine.Core.Bool, true));
			return EvalOutcome.Success(_engine.CreateValue(_engine.Core.Bool, false));
		}
		catch (Exception ex)
		{
			return EvalOutcome.Host(new HostFault("SL5102", "A registered comparison failed.", operation.Span, ex));
		}
	}

	private EvalOutcome Distinct(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array)
	{
		var element = _engine.GetTypeEntry(primary.Type).ElementType!.Value;
		var equality = _engine.GetTypeEntry(element).Equality!;
		var result = new List<ShellValue>();
		try
		{
			foreach (var item in array.Items)
				if (!result.Any(existing => equality.CompareEqual(existing.Value, item.Value)))
					result.Add(item);
			return EvalOutcome.Success(new ShellValue(primary.Type, new ShellArrayValue(result)));
		}
		catch (Exception ex)
		{
			return EvalOutcome.Host(new HostFault("SL5102", "A registered comparison failed.", operation.Span, ex));
		}
	}

	private ShellValue? Extreme(ShellArrayValue array, bool max, SourceSpan span)
	{
		if (array.Items.Count == 0)
			return null;
		var ordering = _engine.GetTypeEntry(array.Items[0].Type).Ordering!;
		var best = array.Items[0];
		try
		{
			foreach (var item in array.Items.Skip(1))
			{
				var compare = ordering.Compare(item.Value, best.Value);
				if (max ? compare > 0 : compare < 0)
					best = item;
			}
		}
		catch (Exception ex) { throw new InvalidOperationException("Registered ordering failed.", ex); }
		return best;
	}

	private ShellValue Sum(ShellArrayValue array, ShellTypeId type)
	{
		object sum;
		if (type == _engine.Core.Int32)
			sum = array.Items.Aggregate(0, (a, x) => checked(a + x.Get<int>()));
		else if (type == _engine.Core.Int64)
			sum = array.Items.Aggregate(0L, (a, x) => checked(a + x.Get<long>()));
		else if (type == _engine.Core.UInt32)
			sum = array.Items.Aggregate(0U, (a, x) => checked(a + x.Get<uint>()));
		else if (type == _engine.Core.UInt64)
			sum = array.Items.Aggregate(0UL, (a, x) => checked(a + x.Get<ulong>()));
		else if (type == _engine.Core.Float32)
			sum = array.Items.Aggregate(0F, (a, x) => a + x.Get<float>());
		else
			sum = array.Items.Aggregate(0D, (a, x) => a + x.Get<double>());
		return _engine.CreateValue(type, sum);
	}

	private EvalOutcome Average(BoundIntrinsicOperation operation, ShellArrayValue array)
	{
		if (array.Items.Count == 0)
			return EmptyOrValue(operation, array, null);
		var input = array.Items[0].Type;
		var resultEntry = _engine.GetTypeEntry(operation.DirectOutput);
		var output = resultEntry.SuccessType!.Value;
		object value = input == _engine.Core.Float32 ? array.Items.Average(x => x.Get<float>()) :
			input == _engine.Core.Float64 ? array.Items.Average(x => x.Get<double>()) :
			input == _engine.Core.Int32 ? array.Items.Average(x => (double)x.Get<int>()) :
			input == _engine.Core.Int64 ? array.Items.Average(x => (double)x.Get<long>()) :
			input == _engine.Core.UInt32 ? array.Items.Average(x => (double)x.Get<uint>()) : array.Items.Average(x => (double)x.Get<ulong>());
		var shell = _engine.CreateValue(output, value);
		return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Success(shell)));
	}

	private EvalOutcome ContextWhere(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array, IReadOnlyList<int> path)
	{
		var result = new List<ShellValue>();
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var predicateValue);
				if (stopped is not null)
					return stopped;
				if (predicateValue.Get<bool>())
					result.Add(array.Items[i]);
			}
		}
		finally { _contextValue = old; }
		var filtered = new ShellValue(primary.Type, new ShellArrayValue(result));
		return ContextSuccess(operation, filtered);
	}

	private EvalOutcome ContextSort(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array, IReadOnlyList<int> path)
	{
		var keyed = new List<(ShellValue Item, ShellValue Key, int Index)>();
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var keyValue);
				if (stopped is not null)
					return stopped;
				keyed.Add((array.Items[i], keyValue, i));
			}
		}
		finally { _contextValue = old; }
		try
		{
			var declaredKey = _engine.GetTypeEntry(operation.ContextExpression!.Type);
			var keyType = keyed.FirstOrDefault().Key?.Type ?? (declaredKey.Kind == ShellTypeKind.Result ? declaredKey.SuccessType!.Value : operation.ContextExpression.Type);
			var ordering = _engine.GetTypeEntry(keyType).Ordering!;
			var sorted = keyed.OrderBy(x => x, Comparer<(ShellValue Item, ShellValue Key, int Index)>.Create((a, b) =>
			{
				var compare = ordering.Compare(a.Key.Value, b.Key.Value);
				return compare != 0 ? compare : a.Index.CompareTo(b.Index);
			})).Select(x => x.Item);
			var sortedValue = new ShellValue(primary.Type, new ShellArrayValue(sorted));
			return ContextSuccess(operation, sortedValue);
		}
		catch (Exception ex) { return EvalOutcome.Host(new HostFault("SL5113", "Registered sort ordering failed.", operation.Span, ex)); }
	}

	private EvalOutcome ContextBoolean(BoundIntrinsicOperation operation, ShellArrayValue array,
		IReadOnlyList<int> path, bool any)
	{
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var predicate);
				if (stopped is not null)
					return stopped;
				if (predicate.Get<bool>() == any)
					return ContextSuccess(operation, _engine.CreateValue(_engine.Core.Bool, any));
			}
		}
		finally { _contextValue = old; }
		return ContextSuccess(operation, _engine.CreateValue(_engine.Core.Bool, !any));
	}

	private EvalOutcome ContextSelect(BoundIntrinsicOperation operation, ShellArrayValue array, IReadOnlyList<int> path)
	{
		var selected = new List<ShellValue>();
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var value);
				if (stopped is not null)
					return stopped;
				selected.Add(value);
			}
		}
		finally { _contextValue = old; }
		var outputEntry = _engine.GetTypeEntry(operation.DirectOutput);
		var arrayType = outputEntry.Kind == ShellTypeKind.Result ? outputEntry.SuccessType!.Value : operation.DirectOutput;
		return ContextSuccess(operation, new ShellValue(arrayType, new ShellArrayValue(selected)));
	}

	private EvalOutcome ContextDistinct(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array,
		IReadOnlyList<int> path)
	{
		var keys = new List<ShellValue>();
		var values = new List<ShellValue>();
		var declared = _engine.GetTypeEntry(operation.ContextExpression!.Type);
		var keyType = declared.Kind == ShellTypeKind.Result ? declared.SuccessType!.Value : operation.ContextExpression.Type;
		var equality = _engine.GetTypeEntry(keyType).Equality!;
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var key);
				if (stopped is not null)
					return stopped;
				bool duplicate;
				try
				{
					duplicate = keys.Any(existing => equality.CompareEqual(existing.Value, key.Value));
				}
				catch (Exception ex)
				{
					return EvalOutcome.Host(new HostFault("SL5102", "A registered comparison failed.", operation.Span, ex));
				}
				if (!duplicate)
				{
					keys.Add(key);
					values.Add(array.Items[i]);
				}
			}
		}
		finally { _contextValue = old; }
		return ContextSuccess(operation, new ShellValue(primary.Type, new ShellArrayValue(values)));
	}

	private EvalOutcome? EvaluateContextValue(BoundIntrinsicOperation operation, int index, IReadOnlyList<int> path,
		out ShellValue value)
	{
		var evaluated = Evaluate(operation.ContextExpression!, Append(path, index));
		if (evaluated.Failed)
		{
			value = null!;
			return evaluated;
		}
		value = evaluated.Value!;
		if (_engine.GetTypeEntry(value.Type).Kind != ShellTypeKind.Result)
			return null;
		var contextualResult = (ShellResultValue)value.Value;
		if (contextualResult is ShellResultValue.Error error)
		{
			var frames = error.Frames.Concat([new ErrorContextFrame("array", index.ToString(), operation.Span, index)]).ToArray();
			value = null!;
			return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Error(error.Value, frames)));
		}
		if (contextualResult is ShellResultValue.Success success)
		{
			value = success.Value;
			return null;
		}
		value = null!;
		return EvalOutcome.Host(new HostFault("SL5115", "A contextual expression produced Void.", operation.Span));
	}

	private EvalOutcome ContextSuccess(BoundIntrinsicOperation operation, ShellValue value) =>
		_engine.GetTypeEntry(operation.DirectOutput).Kind == ShellTypeKind.Result
			? EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Success(value)))
			: EvalOutcome.Success(value);

	private EvalOutcome ValidateValue(ShellValue? value, ShellTypeId expected, SourceSpan span, string boundary)
	{
		if (value is null)
			return EvalOutcome.Host(new HostFault("SL5114", $"The {boundary} returned null.", span));
		if (!_engine.IsAssignable(value.Type, expected))
			return EvalOutcome.Host(new HostFault("SL5115", $"The {boundary} returned {_engine.TypeName(value.Type)} instead of {_engine.TypeName(expected)}.", span));
		var entry = _engine.GetTypeEntry(value.Type);
		if (entry.Adapter is not null && !entry.Adapter.IsValid(value.Value))
			return EvalOutcome.Host(new HostFault("SL5116", $"The {boundary} returned an invalid CLR value.", span));
		return EvalOutcome.Success(value);
	}

	private ShellValue? WrapPropagated(ShellValue? inner, ShellTypeId innerType, ShellTypeId outputType)
	{
		var innerEntry = _engine.GetTypeEntry(innerType);
		if (innerEntry.Kind == ShellTypeKind.Result)
			return RetypeResult(inner!, outputType);
		if (innerType == _engine.Core.Void)
			return new ShellValue(outputType, new ShellResultValue.VoidSuccess());
		return new ShellValue(outputType, new ShellResultValue.Success(inner!));
	}

	private static ShellValue RetypeResult(ShellValue value, ShellTypeId outputType) => new(outputType, value.Value);
	private InvocationContext Context(SourceSpan span, IReadOnlyList<int> path) => new(_engine, _session, _services, span, path);
	private EvalOutcome CoreFault(string code, string message, SourceSpan span) => EvalOutcome.Runtime(new RuntimeFault(new RuntimeFaultCode(code), message, span));
	private static IReadOnlyList<int> Append(IReadOnlyList<int> path, int index) => path.Concat([index]).ToArray();
	private static RuntimeFault AddIndex(RuntimeFault fault, int index) => new(fault.Code, fault.Message, fault.Source,
		new[] { new ErrorContextFrame("array", index.ToString(), fault.Source, index) }.Concat(fault.Context).ToArray());
	private static HostFault AddIndex(HostFault fault, int index) => new(fault.Code, fault.Message, fault.Source, fault.Exception,
		new[] { new ErrorContextFrame("array", index.ToString(), fault.Source, index) }.Concat(fault.Context).ToArray());
	private static ExecutionResult HostResult(string code, string message, SourceSpan span, int completed, Exception? exception = null) =>
		new(ExecutionStatus.HostFault, null, null, new HostFault(code, message, span, exception), completed);
}
