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

internal sealed partial class Evaluator
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
				BoundContextExpression context => EvaluateContext(context),
				BoundArrayExpression array => EvaluateArray(array, path),
				BoundUnaryExpression unary => EvaluateUnary(unary, path),
				BoundBinaryExpression binary => EvaluateBinary(binary, path),
				BoundApplyExpression apply => EvaluateApply(apply, path),
				BoundConstructorExpression constructor => EvaluateConstructor(constructor, path),
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
}
