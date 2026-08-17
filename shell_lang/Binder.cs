using System.Globalization;

namespace ShellLang;

internal sealed partial class Binder
{
	private readonly ShellEngine _engine;
	private readonly ShellSession _session;
	private readonly string _source;
	private readonly List<CompilationDiagnostic> _diagnostics;
	private readonly Dictionary<string, (ShellTypeId Type, bool External)> _locals = new(StringComparer.Ordinal);
	private readonly Dictionary<string, SessionRequirement> _requirements = new(StringComparer.Ordinal);

	public Binder(ShellEngine engine, ShellSession session, string source, List<CompilationDiagnostic> diagnostics)
	{
		_engine = engine;
		_session = session;
		_source = source;
		_diagnostics = diagnostics;
		foreach (var binding in session.GetBindings())
			_locals.Add(binding.Name, (binding.Type, true));
	}

	public (BoundProgram Program, ShellTypeId? ResultType, IReadOnlyList<SessionRequirement> Requirements) Bind(ScriptSyntax script)
	{
		var statements = new List<BoundStatement>();
		ShellTypeId? result = null;
		foreach (var statement in script.Statements)
		{
			if (statement is AssignmentSyntax assignment)
			{
				var expression = BindExpression(assignment.Expression);
				if (expression.Type == _engine.Core.Void)
					Error("SL2001", "Void cannot be assigned.", assignment.Expression.Span);
				_locals[assignment.Name] = (expression.Type, false);
				statements.Add(new BoundAssignment(assignment.Name, expression, assignment.Span));
				result = null;
			}
			else
			{
				var expressionSyntax = ((ExpressionStatementSyntax)statement).Expression;
				var expression = BindExpression(expressionSyntax);
				statements.Add(new BoundExpressionStatement(expression, statement.Span));
				result = expression.Type == _engine.Core.Void ? null : expression.Type;
			}
		}
		return (new BoundProgram(statements), result, _requirements.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray());
	}

	private BoundExpression BindExpression(ExpressionSyntax syntax, ShellTypeId? expected = null)
	{
		return syntax switch
		{
			LiteralSyntax literal => BindLiteral(literal, expected),
			NameSyntax name => BindName(name, expected),
			ThisSyntax @this => BindThis(@this),
			ArraySyntax array => BindArray(array, expected),
			UnarySyntax unary => BindUnary(unary),
			BinarySyntax binary => BindBinary(binary),
			ParenthesizedSyntax parenthesized => BindExpression(parenthesized.Expression, expected),
			InvocationSyntax invocation => BindInvocation(invocation, null),
			MemberSyntax member => BindMember(member),
			ContextMemberSyntax context => BindContextMember(context),
			PipelineSyntax pipeline => BindPipeline(pipeline),
			_ => new BoundErrorExpression(_engine.Core.Int32, syntax.Span)
		};
	}

	private BoundExpression BindLiteral(LiteralSyntax syntax, ShellTypeId? expected)
	{
		var token = syntax.Token;
		if (token.Kind == TokenKind.String)
			return new BoundLiteralExpression(_engine.CreateValue(_engine.Core.String, token.Value!), token.Span);
		if (token.Kind is TokenKind.True or TokenKind.False)
			return new BoundLiteralExpression(_engine.CreateValue(_engine.Core.Bool, token.Value!), token.Span);
		var target = expected is { } e && IsNumeric(e) ? e : token.Kind == TokenKind.Integer ? _engine.Core.Int32 : _engine.Core.Float64;
		if (token.Kind == TokenKind.Fractional && target is var t && t != _engine.Core.Float32 && t != _engine.Core.Float64)
		{
			Error("SL2101", $"A fractional literal cannot use type {_engine.TypeName(target)}.", token.Span);
			target = _engine.Core.Float64;
		}
		try
		{
			var text = (string)token.Value!;
			object value;
			if (target == _engine.Core.Int32)
				value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
			else if (target == _engine.Core.Int64)
				value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
			else if (target == _engine.Core.UInt32)
				value = uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
			else if (target == _engine.Core.UInt64)
				value = ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
			else if (target == _engine.Core.Float32)
				value = ParseFloat32(text);
			else if (target == _engine.Core.Float64)
				value = ParseFloat64(text);
			else
				throw new InvalidOperationException();
			return new BoundLiteralExpression(_engine.CreateValue(target, value), token.Span);
		}
		catch (Exception ex) when (ex is FormatException or OverflowException)
		{
			Error("SL2102", $"Numeric literal is not representable as {_engine.TypeName(target)}.", token.Span);
			return new BoundLiteralExpression(_engine.CreateValue(target, NumericZero(target)), token.Span);
		}
	}

	private static float ParseFloat32(string text)
	{
		var value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
		if (float.IsInfinity(value))
			throw new OverflowException();
		return value;
	}
	private static double ParseFloat64(string text)
	{
		var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
		if (double.IsInfinity(value))
			throw new OverflowException();
		return value;
	}
	private object NumericZero(ShellTypeId type)
	{
		if (type == _engine.Core.Int32)
			return 0;
		if (type == _engine.Core.Int64)
			return 0L;
		if (type == _engine.Core.UInt32)
			return 0U;
		if (type == _engine.Core.UInt64)
			return 0UL;
		if (type == _engine.Core.Float32)
			return 0F;
		return 0D;
	}

	private BoundExpression BindName(NameSyntax syntax, ShellTypeId? expected)
	{
		if (expected is { } expectedType)
		{
			var entry = _engine.GetTypeEntry(expectedType);
			if (entry.Kind == ShellTypeKind.Enum)
			{
				var member = entry.EnumMembers.FirstOrDefault(x => x.Name == syntax.Name);
				if (member is not null)
					return new BoundLiteralExpression(_engine.CreateValue(expectedType, member.Value), syntax.Span);
			}
		}
		if (_locals.TryGetValue(syntax.Name, out var local))
		{
			if (local.External)
				_requirements[syntax.Name] = new SessionRequirement(syntax.Name, local.Type);
			return new BoundNameExpression(syntax.Name, false, local.Type, syntax.Span);
		}
		if (_engine.Globals.TryGetValue(syntax.Name, out var global))
			return new BoundNameExpression(syntax.Name, true, global.Type, syntax.Span);
		Error("SL2002", $"Unknown value '{syntax.Name}'.", syntax.Span);
		return new BoundErrorExpression(expected ?? _engine.Core.Int32, syntax.Span);
	}

	private BoundExpression BindArray(ArraySyntax syntax, ShellTypeId? expected)
	{
		ShellTypeId? elementExpected = null;
		if (expected is { } et)
		{
			var e = _engine.GetTypeEntry(et);
			if (e.Kind == ShellTypeKind.Array)
				elementExpected = e.ElementType;
		}
		if (syntax.Items.Count == 0 && elementExpected is null)
		{
			Error("SL2103", "An empty array requires an expected Array<T> type.", syntax.Span);
			elementExpected = _engine.Core.Any;
		}
		var items = new List<BoundExpression>();
		ShellTypeId? element = elementExpected;
		foreach (var itemSyntax in syntax.Items)
		{
			var item = BindExpression(itemSyntax, element);
			items.Add(item);
			if (element is null)
				element = item.Type;
			else if (!_engine.IsAssignable(item.Type, element.Value))
				Error("SL2104", $"Array item type {_engine.TypeName(item.Type)} is not assignable to {_engine.TypeName(element.Value)}.", item.Span,
					element.Value, item.Type);
		}
		var arrayType = _engine.ArrayOf(element ?? _engine.Core.Any);
		return new BoundArrayExpression(items, arrayType, syntax.Span);
	}

	private BoundExpression BindUnary(UnarySyntax syntax)
	{
		var operand = BindExpression(syntax.Operand);
		var scalar = PrimaryValueType(operand.Type);
		if (syntax.Operator == TokenKind.Bang)
		{
			if (scalar != _engine.Core.Bool)
				Error("SL2105", "Operator ! requires Bool.", syntax.Span, _engine.Core.Bool, scalar);
		}
		else if (!IsSignedNumeric(scalar))
			Error("SL2106", "Unary - requires a signed numeric type.", syntax.Span);
		var operation = new BoundPrimitiveOperation(syntax.Operator, scalar, scalar, syntax.Span);
		var plan = BuildAdaptation(operand.Type, scalar, true, syntax.Span, scalar);
		return new BoundApplyExpression(operand, operation, plan, [], plan.OutputType, syntax.Span);
	}

	private BoundExpression BindBinary(BinarySyntax syntax)
	{
		BoundExpression left;
		BoundExpression right;
		if (syntax.Left is LiteralSyntax && syntax.Right is not LiteralSyntax)
		{
			right = BindExpression(syntax.Right);
			var inferred = SecondaryValueType(right.Type);
			left = BindExpression(syntax.Left, inferred);
		}
		else
		{
			left = BindExpression(syntax.Left);
			var inferred = PrimaryValueType(left.Type);
			right = BindExpression(syntax.Right, inferred);
		}
		var scalarLeft = PrimaryValueType(left.Type);
		var scalarRight = SecondaryValueType(right.Type);
		var result = scalarLeft;
		if (syntax.Operator is TokenKind.AndAnd or TokenKind.OrOr)
		{
			if (scalarLeft != _engine.Core.Bool || scalarRight != _engine.Core.Bool)
				Error("SL2107", "Logical operators require Bool operands.", syntax.Span);
			result = _engine.Core.Bool;
		}
		else if (syntax.Operator is TokenKind.EqualEqual or TokenKind.BangEqual)
		{
			if (scalarLeft != scalarRight || !HasEquality(scalarLeft))
				Error("SL2108", "Equality requires matching comparable types.", syntax.Span);
			result = _engine.Core.Bool;
		}
		else if (syntax.Operator is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
		{
			if (scalarLeft != scalarRight || !HasOrdering(scalarLeft))
				Error("SL2109", "Ordering requires matching ordered types.", syntax.Span);
			result = _engine.Core.Bool;
		}
		else
		{
			if (scalarLeft != scalarRight || !IsNumeric(scalarLeft))
				Error("SL2110", "Arithmetic requires matching numeric types.", syntax.Span);
			if (syntax.Operator == TokenKind.Percent && !IsInteger(scalarLeft))
				Error("SL2111", "Operator % requires integers.", syntax.Span);
		}
		var operation = new BoundPrimitiveOperation(syntax.Operator, scalarLeft, result, syntax.Span);
		var primaryPlan = BuildAdaptation(left.Type, scalarLeft, true, syntax.Left.Span, result);
		var secondaryPlan = BuildAdaptation(right.Type, scalarLeft, false, syntax.Right.Span);
		var secondary = new BoundSecondary("right", false, right, secondaryPlan, syntax.Right.Span);
		return new BoundApplyExpression(left, operation, primaryPlan, [secondary], CombineSecondaryResults(primaryPlan.OutputType, [secondary]), syntax.Span);
	}

	private ShellTypeId PrimaryValueType(ShellTypeId type)
	{
		var entry = _engine.GetTypeEntry(type);
		if (entry.Kind == ShellTypeKind.Result)
			return PrimaryValueType(entry.SuccessType!.Value);
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field)
			return PrimaryValueType(entry.OutputFields![field]);
		if (entry.Kind == ShellTypeKind.Array)
			return PrimaryValueType(entry.ElementType!.Value);
		return type;
	}

	private ShellTypeId SecondaryValueType(ShellTypeId type)
	{
		var entry = _engine.GetTypeEntry(type);
		if (entry.Kind == ShellTypeKind.Result)
			return SecondaryValueType(entry.SuccessType!.Value);
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field)
			return SecondaryValueType(entry.OutputFields![field]);
		return type;
	}

	private BoundExpression BindPipeline(PipelineSyntax syntax)
	{
		var current = BindExpression(syntax.Source);
		foreach (var stage in syntax.Stages)
		{
			current = stage switch
			{
				InvocationSyntax invocation => BindInvocation(invocation, current),
				NameSyntax name => BindStageName(name, current),
				MemberSyntax member => BindPipelineMember(member, current),
				_ => ErrorExpression("SL2201", "Invalid pipeline stage.", stage.Span)
			};
		}
		return current;
	}

	private BoundExpression BindPipelineMember(MemberSyntax syntax, BoundExpression pipelineInput)
	{
		var baseStage = syntax.Receiver switch
		{
			InvocationSyntax invocation => BindInvocation(invocation, pipelineInput),
			NameSyntax name => BindStageName(name, pipelineInput),
			_ => ErrorExpression("SL2201", "Invalid pipeline stage.", syntax.Receiver.Span)
		};
		return BindMemberOn(baseStage, syntax.Name, syntax.Arguments, syntax.Span);
	}

	private BoundExpression BindStageName(NameSyntax syntax, BoundExpression primary)
	{
		if (_engine.TryGetType(syntax.Name, out _))
			return ErrorExpression("SL2502", $"Type call '{syntax.Name}' cannot be used as a pipeline stage.", syntax.Span);
		if (ShellEngine.IntrinsicNames.Contains(syntax.Name))
			return BindIntrinsic(syntax.Name, [], primary, syntax.Span);
		if (!_engine.Commands.TryGetValue(syntax.Name, out var command))
			return ErrorExpression("SL2202", $"Unknown command or intrinsic '{syntax.Name}'.", syntax.Span);
		return BindCommand(command, [], primary, syntax.Span);
	}

	private BoundExpression BindInvocation(InvocationSyntax syntax, BoundExpression? primary)
	{
		if (_engine.TryGetType(syntax.Name, out var type))
		{
			if (primary is not null)
				return ErrorExpression("SL2502", $"Type call '{syntax.Name}' cannot be used as a pipeline stage.", syntax.Span);
			if (_engine.IsConversionTarget(type.Id))
				return BindConversion(type, syntax);
			return BindConstructor(type, syntax);
		}
		if (ShellEngine.IntrinsicNames.Contains(syntax.Name))
			return BindIntrinsic(syntax.Name, syntax.Entries, primary, syntax.Span);
		if (!_engine.Commands.TryGetValue(syntax.Name, out var command))
			return ErrorExpression("SL2202", $"Unknown command '{syntax.Name}'.", syntax.Span);
		return BindCommand(command, syntax.Entries, primary, syntax.Span);
	}

	private BoundExpression BindCommand(CommandDescriptor command, IReadOnlyList<InvocationEntrySyntax> entries,
		BoundExpression? primary, SourceSpan span)
	{
		var defaultInput = command.Inputs.FirstOrDefault(x => x.IsDefault);
		if (primary is not null && defaultInput is null)
			return ErrorExpression("SL2203", $"Command '{command.Name}' has no default input.", span);
		var directOutput = CommandReturnType(command);
		var preliminaryPlan = primary is null ? null :
			BuildAdaptation(primary.Type, defaultInput!.Type, true, span, directOutput);
		var operationScope = preliminaryPlan is null ? null : CreateContext(EffectiveContextType(preliminaryPlan));
		var enclosingScope = _contextScope;
		if (operationScope is not null)
			_contextScope = operationScope;
		var suppliedInputs = new HashSet<string>(StringComparer.Ordinal);
		var suppliedArgs = new HashSet<string>(StringComparer.Ordinal);
		var secondaries = new List<BoundSecondary>();
		var positional = 0;
		var sawNamed = false;
		foreach (var entry in entries)
		{
			if (entry.Kind == InvocationEntryKind.ExplicitInput)
			{
				sawNamed = true;
				var port = command.Inputs.FirstOrDefault(x => x.Name == entry.Name);
				if (port is null)
				{
					Error("SL2204", $"Command '{command.Name}' has no input '{entry.Name}'.", entry.Span);
					continue;
				}
				if (!suppliedInputs.Add(port.Name))
				{
					Error("SL2205", $"Input '{port.Name}' is supplied more than once.", entry.Span);
					continue;
				}
				if (primary is not null && port.IsDefault)
					Error("SL2206", "The default input is supplied by both the pipeline and an explicit port.", entry.Span);
				var expression = operationScope is null
					? BindExpression(entry.Expression, port.Type)
					: InContext(operationScope, () => BindExpression(entry.Expression, port.Type));
				var adaptation = BuildAdaptation(expression.Type, port.Type, false, entry.Span);
				secondaries.Add(new(port.Name, true, expression, adaptation, entry.Span));
			}
			else
			{
				if (entry.Kind == InvocationEntryKind.NamedArgument)
					sawNamed = true;
				else if (sawNamed)
					Error("SL2207", "Positional arguments must precede named entries.", entry.Span);
				ArgumentDescriptor? argument;
				if (entry.Kind == InvocationEntryKind.NamedArgument)
					argument = command.Arguments.FirstOrDefault(x => x.Name == entry.Name);
				else
					argument = command.Arguments.OrderBy(x => x.Position).ElementAtOrDefault(positional++);
				if (argument is null)
				{
					Error("SL2208", $"Unknown argument in '{command.Name}'.", entry.Span);
					continue;
				}
				if (!suppliedArgs.Add(argument.Name))
				{
					Error("SL2209", $"Argument '{argument.Name}' is supplied more than once.", entry.Span);
					continue;
				}
				var expression = operationScope is null
					? BindExpression(entry.Expression, argument.Type)
					: InContext(operationScope, () => BindExpression(entry.Expression, argument.Type));
				var adaptation = BuildAdaptation(expression.Type, argument.Type, false, entry.Span);
				secondaries.Add(new(argument.Name, false, expression, adaptation, entry.Span));
			}
		}
		foreach (var port in command.Inputs)
			if (!(port.IsDefault && primary is not null) && !suppliedInputs.Contains(port.Name))
				Error("SL2210", $"Required input '{command.Name}.{port.Name}' is missing.", span);
		foreach (var arg in command.Arguments)
			if (arg.Required && !suppliedArgs.Contains(arg.Name))
				Error("SL2211", $"Required argument '{command.Name}.{arg.Name}' is missing.", span);

		if (primary is null)
		{
			if (defaultInput is not null && !suppliedInputs.Contains(defaultInput.Name))
				Error("SL2210", $"Required input '{command.Name}.{defaultInput.Name}' is missing.", span);
			var dummy = new BoundLiteralExpression(_engine.CreateValue(_engine.Core.Bool, true), span);
			var op = new BoundCommandOperation(command, null, _engine.Core.Bool, directOutput, span);
			var invocationType = CombineSecondaryResults(directOutput, secondaries);
			return new BoundApplyExpression(dummy, op, new(AdaptationKind.Direct, _engine.Core.Bool, directOutput), secondaries, invocationType, span);
		}
		secondaries = MarkContextual(secondaries, operationScope!).ToList();
		var operation = new BoundCommandOperation(command, defaultInput!.Name, defaultInput.Type, directOutput, span);
		var contextualOutput = CombineContextualDirectOutput(directOutput, secondaries, operationScope!.Id);
		var plan = BuildAdaptation(primary.Type, operation.ExpectedInput, true, span, contextualOutput);
		var ordinary = secondaries.Where(x => x.ContextScopeId != operationScope.Id).ToArray();
		var result = new BoundApplyExpression(primary, operation, plan, secondaries,
			CombineSecondaryResults(plan.OutputType, ordinary), span);
		_contextScope = enclosingScope;
		return result;
	}

	private ShellTypeId CommandReturnType(CommandDescriptor command)
	{
		var success = command.Outputs.Count switch
		{
			0 => _engine.Core.Void,
			1 => command.Outputs[0].Type,
			_ => command.OutputRecordType ?? throw new InvalidOperationException("Output record type was not generated.")
		};
		return command.ErrorType is { } error ? _engine.ResultOf(success, error) : success;
	}

	private BoundExpression BindMember(MemberSyntax syntax)
	{
		if (syntax.Receiver is NameSyntax typeName && _engine.TryGetType(typeName.Name, out var typeEntry))
		{
			return BindTypeValue(typeEntry, syntax.Name, syntax.Arguments, syntax.Span);
		}
		var receiver = BindExpression(syntax.Receiver);
		return BindMemberOn(receiver, syntax.Name, syntax.Arguments, syntax.Span);
	}

	private BoundExpression BindMemberOn(BoundExpression receiver, string name, IReadOnlyList<InvocationEntrySyntax>? arguments, SourceSpan span)
	{
		if (!TryFindMember(receiver.Type, name, out var expectedReceiver, out var member, out var query, out var outputField, out var outputType))
			return ErrorExpression("SL2303", $"Type '{_engine.TypeName(receiver.Type)}' has no accessible member '{name}'.", span);
		BoundOperation operation;
		IReadOnlyList<BoundSecondary> secondaries = [];
		if (outputField is not null)
		{
			if (arguments is not null)
				Error("SL2304", "Output fields cannot be invoked.", span);
			operation = new BoundMemberOperation(null, outputField, expectedReceiver, outputType, span);
		}
		else if (member is not null)
		{
			if (arguments is not null)
				Error("SL2305", $"Member '{name}' is not a query.", span);
			operation = new BoundMemberOperation(member, null, expectedReceiver, member.ValueType, span);
		}
		else
		{
			if (arguments is null)
				Error("SL2306", $"Query '{name}' requires invocation syntax.", span);
			var direct = query!.ErrorType is { } error ? _engine.ResultOf(query.OutputType, error) : query.OutputType;
			operation = new BoundQueryOperation(query, expectedReceiver, direct, span);
			var preliminary = BuildAdaptation(receiver.Type, expectedReceiver, true, span, direct);
			var scope = CreateContext(EffectiveContextType(preliminary));
			secondaries = MarkContextual(InContext(scope,
				() => BindQueryArguments(query, arguments ?? [], span, scope)), scope);
			var contextualOutput = CombineContextualDirectOutput(direct, secondaries, scope.Id);
			var queryPlan = BuildAdaptation(receiver.Type, expectedReceiver, true, span, contextualOutput);
			var ordinary = secondaries.Where(x => x.ContextScopeId != scope.Id).ToArray();
			return new BoundApplyExpression(receiver, operation, queryPlan, secondaries,
				CombineSecondaryResults(queryPlan.OutputType, ordinary), span);
		}
		var plan = BuildAdaptation(receiver.Type, operation.ExpectedInput, true, span, operation.DirectOutput);
		return new BoundApplyExpression(receiver, operation, plan, secondaries, CombineSecondaryResults(plan.OutputType, secondaries), span);
	}

	private IReadOnlyList<BoundSecondary> BindQueryArguments(QueryDescriptor query,
		IReadOnlyList<InvocationEntrySyntax> entries, SourceSpan span, ContextScope scope)
	{
		var result = new List<BoundSecondary>();
		var supplied = new HashSet<string>(StringComparer.Ordinal);
		var positional = 0;
		var named = false;
		foreach (var entry in entries)
		{
			if (entry.Kind == InvocationEntryKind.ExplicitInput)
			{
				Error("SL2307", "Queries cannot have explicit input ports.", entry.Span);
				continue;
			}
			if (entry.Kind == InvocationEntryKind.NamedArgument)
				named = true;
			else if (named)
				Error("SL2207", "Positional arguments must precede named arguments.", entry.Span);
			var argument = entry.Kind == InvocationEntryKind.NamedArgument
				? query.Arguments.FirstOrDefault(x => x.Name == entry.Name)
				: query.Arguments.OrderBy(x => x.Position).ElementAtOrDefault(positional++);
			if (argument is null)
			{
				Error("SL2208", $"Unknown argument in query '{query.Name}'.", entry.Span);
				continue;
			}
			if (!supplied.Add(argument.Name))
			{
				Error("SL2209", $"Argument '{argument.Name}' is supplied more than once.", entry.Span);
				continue;
			}
			var expression = InContext(scope, () => BindExpression(entry.Expression, argument.Type));
			result.Add(new(argument.Name, false, expression, BuildAdaptation(expression.Type, argument.Type, false, entry.Span), entry.Span));
		}
		foreach (var argument in query.Arguments)
			if (argument.Required && !supplied.Contains(argument.Name))
				Error("SL2211", $"Required argument '{query.Name}.{argument.Name}' is missing.", span);
		return result;
	}

	private BoundExpression BindContextMember(ContextMemberSyntax syntax)
	{
		if (_contextScope is null)
			return ErrorExpression("SL2308", "Leading '.' is unavailable outside a contextual expression.", syntax.Span);
		var receiver = new BoundContextExpression(_contextScope.Id, _contextScope.Type, syntax.Span);
		return BindMemberOn(receiver, syntax.Name, syntax.Arguments, syntax.Span);
	}

	private bool TryFindMember(ShellTypeId actual, string name, out ShellTypeId expected, out MemberDescriptor? member,
		out QueryDescriptor? query, out string? outputField, out ShellTypeId outputType)
	{
		expected = default;
		member = null;
		query = null;
		outputField = null;
		outputType = default;
		var entry = _engine.GetTypeEntry(actual);
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.OutputFields!.TryGetValue(name, out outputType))
		{
			expected = actual;
			outputField = name;
			return true;
		}
		if (_engine.FindMemberOwner(actual, name, out member, out query) is { } owner)
		{
			expected = owner.Id;
			outputType = member?.ValueType ?? query!.OutputType;
			return true;
		}
		if (entry.Kind == ShellTypeKind.Result && TryFindMember(entry.SuccessType!.Value, name, out expected, out member, out query, out outputField, out outputType))
			return true;
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field &&
			TryFindMember(entry.OutputFields![field], name, out expected, out member, out query, out outputField, out outputType))
			return true;
		if (entry.Kind == ShellTypeKind.Array && TryFindMember(entry.ElementType!.Value, name, out expected, out member, out query, out outputField, out outputType))
			return true;
		return false;
	}

	private bool IsNumeric(ShellTypeId type) => type == _engine.Core.Int32 || type == _engine.Core.Int64 || type == _engine.Core.UInt32 ||
		type == _engine.Core.UInt64 || type == _engine.Core.Float32 || type == _engine.Core.Float64;
	private bool IsInteger(ShellTypeId type) => type == _engine.Core.Int32 || type == _engine.Core.Int64 || type == _engine.Core.UInt32 || type == _engine.Core.UInt64;
	private bool IsSignedNumeric(ShellTypeId type) => type == _engine.Core.Int32 || type == _engine.Core.Int64 || type == _engine.Core.Float32 || type == _engine.Core.Float64;
	private bool HasEquality(ShellTypeId type) => _engine.GetTypeEntry(type).Equality is not null;
	private bool HasOrdering(ShellTypeId type) => _engine.GetTypeEntry(type).Ordering is not null;
	private BoundExpression ErrorExpression(string code, string message, SourceSpan span)
	{
		Error(code, message, span);
		return new BoundErrorExpression(_engine.Core.Int32, span);
	}
	private void Error(string code, string message, SourceSpan span, ShellTypeId? expected = null, ShellTypeId? actual = null, IReadOnlyList<string>? attempts = null) =>
		_diagnostics.Add(new CompilationDiagnostic(code, message, span, expected, actual, attempts,
			contextType: _contextScope?.Type));
}
