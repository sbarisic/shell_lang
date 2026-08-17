using System.Collections.ObjectModel;

namespace ShellLang;

internal sealed partial class Evaluator
{
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
