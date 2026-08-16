using System.Globalization;

namespace ShellLang;

internal abstract record BoundStatement(SourceSpan Span);
internal sealed record BoundAssignment(string Name, BoundExpression Expression, SourceSpan Span) : BoundStatement(Span);
internal sealed record BoundExpressionStatement(BoundExpression Expression, SourceSpan Span) : BoundStatement(Span);
internal abstract record BoundExpression(ShellTypeId Type, SourceSpan Span);
internal sealed record BoundErrorExpression(ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundLiteralExpression(ShellValue Value, SourceSpan Span) : BoundExpression(Value.Type, Span);
internal sealed record BoundNameExpression(string Name, bool IsGlobal, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundArrayExpression(IReadOnlyList<BoundExpression> Items, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundUnaryExpression(TokenKind Operator, BoundExpression Operand, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundBinaryExpression(BoundExpression Left, TokenKind Operator, BoundExpression Right, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundApplyExpression(BoundExpression Primary, BoundOperation Operation, AdaptationPlan Adaptation,
    IReadOnlyList<BoundSecondary> Secondary, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);

internal sealed record BoundSecondary(string Name, bool IsInput, BoundExpression Expression, AdaptationPlan Adaptation, SourceSpan Span);
internal enum AdaptationKind { Direct, Result, DefaultOutput, Array }
internal sealed record AdaptationPlan(AdaptationKind Kind, ShellTypeId InputType, ShellTypeId OutputType,
    AdaptationPlan? Inner = null, string? OutputField = null);

internal abstract record BoundOperation(ShellTypeId ExpectedInput, ShellTypeId DirectOutput, SourceSpan Span);
internal sealed record BoundCommandOperation(CommandDescriptor Command, string? PrimaryPort, ShellTypeId ExpectedInput,
    ShellTypeId DirectOutput, SourceSpan Span) : BoundOperation(ExpectedInput, DirectOutput, Span);
internal sealed record BoundMemberOperation(MemberDescriptor? Member, string? OutputField, ShellTypeId ExpectedInput,
    ShellTypeId DirectOutput, SourceSpan Span) : BoundOperation(ExpectedInput, DirectOutput, Span);
internal sealed record BoundQueryOperation(QueryDescriptor Query, ShellTypeId ExpectedInput,
    ShellTypeId DirectOutput, SourceSpan Span) : BoundOperation(ExpectedInput, DirectOutput, Span);
internal sealed record BoundPrimitiveOperation(TokenKind Operator, ShellTypeId ExpectedInput,
    ShellTypeId DirectOutput, SourceSpan Span) : BoundOperation(ExpectedInput, DirectOutput, Span);
internal enum IntrinsicKind { Require, ValueOr, Error, IsOk, Where, Sort, Take, Count, Sum, First, Min, Max, Average }
internal sealed record BoundIntrinsicOperation(IntrinsicKind Intrinsic, ShellTypeId ExpectedInput,
    ShellTypeId DirectOutput, SourceSpan Span, BoundExpression? ContextExpression = null) : BoundOperation(ExpectedInput, DirectOutput, Span);

internal sealed class BoundProgram
{
    public BoundProgram(IReadOnlyList<BoundStatement> statements) => Statements = statements;
    public IReadOnlyList<BoundStatement> Statements { get; }
}

public sealed class ShellCompilation
{
    internal ShellCompilation(ShellEngine engine, string source, IReadOnlyList<CompilationDiagnostic> diagnostics,
        ShellTypeId? resultType, long catalogRevision, IReadOnlyList<SessionRequirement> requirements, BoundProgram? program)
    {
        Engine = engine; Source = source; Diagnostics = diagnostics; ResultType = resultType;
        CatalogRevision = catalogRevision; SessionRequirements = requirements; Program = program;
    }
    internal ShellEngine Engine { get; }
    internal BoundProgram? Program { get; }
    public string Source { get; }
    public bool IsValid => Diagnostics.Count == 0 && Program is not null;
    public IReadOnlyList<CompilationDiagnostic> Diagnostics { get; }
    public ShellTypeId? ResultType { get; }
    public long CatalogRevision { get; }
    public IReadOnlyList<SessionRequirement> SessionRequirements { get; }
}

internal sealed class Binder
{
    private readonly ShellEngine _engine;
    private readonly ShellSession _session;
    private readonly string _source;
    private readonly List<CompilationDiagnostic> _diagnostics;
    private readonly Dictionary<string, (ShellTypeId Type, bool External)> _locals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionRequirement> _requirements = new(StringComparer.Ordinal);
    private ShellTypeId? _contextType;

    public Binder(ShellEngine engine, ShellSession session, string source, List<CompilationDiagnostic> diagnostics)
    {
        _engine = engine; _session = session; _source = source; _diagnostics = diagnostics;
        foreach (var binding in session.GetBindings()) _locals.Add(binding.Name, (binding.Type, true));
    }

    public (BoundProgram Program, ShellTypeId? ResultType, IReadOnlyList<SessionRequirement> Requirements) Bind(ScriptSyntax script)
    {
        var statements = new List<BoundStatement>(); ShellTypeId? result = null;
        foreach (var statement in script.Statements)
        {
            if (statement is AssignmentSyntax assignment)
            {
                var expression = BindExpression(assignment.Expression);
                if (expression.Type == _engine.Core.Void) Error("SL2001", "Void cannot be assigned.", assignment.Expression.Span);
                _locals[assignment.Name] = (expression.Type, false);
                statements.Add(new BoundAssignment(assignment.Name, expression, assignment.Span)); result = null;
            }
            else
            {
                var expressionSyntax = ((ExpressionStatementSyntax)statement).Expression;
                var expression = BindExpression(expressionSyntax); statements.Add(new BoundExpressionStatement(expression, statement.Span));
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
        if (token.Kind == TokenKind.String) return new BoundLiteralExpression(_engine.CreateValue(_engine.Core.String, token.Value!), token.Span);
        if (token.Kind is TokenKind.True or TokenKind.False) return new BoundLiteralExpression(_engine.CreateValue(_engine.Core.Bool, token.Value!), token.Span);
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
            if (target == _engine.Core.Int32) value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (target == _engine.Core.Int64) value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (target == _engine.Core.UInt32) value = uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (target == _engine.Core.UInt64) value = ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (target == _engine.Core.Float32) value = ParseFloat32(text);
            else if (target == _engine.Core.Float64) value = ParseFloat64(text);
            else throw new InvalidOperationException();
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
        if (float.IsInfinity(value)) throw new OverflowException(); return value;
    }
    private static double ParseFloat64(string text)
    {
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsInfinity(value)) throw new OverflowException(); return value;
    }
    private object NumericZero(ShellTypeId type)
    {
        if (type == _engine.Core.Int32) return 0;
        if (type == _engine.Core.Int64) return 0L;
        if (type == _engine.Core.UInt32) return 0U;
        if (type == _engine.Core.UInt64) return 0UL;
        if (type == _engine.Core.Float32) return 0F;
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
                if (member is not null) return new BoundLiteralExpression(_engine.CreateValue(expectedType, member.Value), syntax.Span);
            }
        }
        if (_locals.TryGetValue(syntax.Name, out var local))
        {
            if (local.External) _requirements[syntax.Name] = new SessionRequirement(syntax.Name, local.Type);
            return new BoundNameExpression(syntax.Name, false, local.Type, syntax.Span);
        }
        if (_engine.Globals.TryGetValue(syntax.Name, out var global)) return new BoundNameExpression(syntax.Name, true, global.Type, syntax.Span);
        Error("SL2002", $"Unknown value '{syntax.Name}'.", syntax.Span);
        return new BoundErrorExpression(expected ?? _engine.Core.Int32, syntax.Span);
    }

    private BoundExpression BindArray(ArraySyntax syntax, ShellTypeId? expected)
    {
        ShellTypeId? elementExpected = null;
        if (expected is { } et)
        {
            var e = _engine.GetTypeEntry(et);
            if (e.Kind == ShellTypeKind.Array) elementExpected = e.ElementType;
        }
        if (syntax.Items.Count == 0 && elementExpected is null)
        {
            Error("SL2103", "An empty array requires an expected Array<T> type.", syntax.Span);
            elementExpected = _engine.Core.Any;
        }
        var items = new List<BoundExpression>(); ShellTypeId? element = elementExpected;
        foreach (var itemSyntax in syntax.Items)
        {
            var item = BindExpression(itemSyntax, element); items.Add(item);
            if (element is null) element = item.Type;
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
            if (scalar != _engine.Core.Bool) Error("SL2105", "Operator ! requires Bool.", syntax.Span, _engine.Core.Bool, scalar);
        }
        else if (!IsSignedNumeric(scalar)) Error("SL2106", "Unary - requires a signed numeric type.", syntax.Span);
        var operation = new BoundPrimitiveOperation(syntax.Operator, scalar, scalar, syntax.Span);
        var plan = BuildAdaptation(operand.Type, scalar, true, syntax.Span, scalar);
        return new BoundApplyExpression(operand, operation, plan, [], plan.OutputType, syntax.Span);
    }

    private BoundExpression BindBinary(BinarySyntax syntax)
    {
        BoundExpression left; BoundExpression right;
        if (syntax.Left is LiteralSyntax && syntax.Right is not LiteralSyntax)
        {
            right = BindExpression(syntax.Right); var inferred = SecondaryValueType(right.Type);
            left = BindExpression(syntax.Left, inferred);
        }
        else
        {
            left = BindExpression(syntax.Left); var inferred = PrimaryValueType(left.Type);
            right = BindExpression(syntax.Right, inferred);
        }
        var scalarLeft = PrimaryValueType(left.Type); var scalarRight = SecondaryValueType(right.Type); var result = scalarLeft;
        if (syntax.Operator is TokenKind.AndAnd or TokenKind.OrOr)
        {
            if (scalarLeft != _engine.Core.Bool || scalarRight != _engine.Core.Bool) Error("SL2107", "Logical operators require Bool operands.", syntax.Span);
            result = _engine.Core.Bool;
        }
        else if (syntax.Operator is TokenKind.EqualEqual or TokenKind.BangEqual)
        {
            if (scalarLeft != scalarRight || !HasEquality(scalarLeft)) Error("SL2108", "Equality requires matching comparable types.", syntax.Span);
            result = _engine.Core.Bool;
        }
        else if (syntax.Operator is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
        {
            if (scalarLeft != scalarRight || !HasOrdering(scalarLeft)) Error("SL2109", "Ordering requires matching ordered types.", syntax.Span);
            result = _engine.Core.Bool;
        }
        else
        {
            if (scalarLeft != scalarRight || !IsNumeric(scalarLeft)) Error("SL2110", "Arithmetic requires matching numeric types.", syntax.Span);
            if (syntax.Operator == TokenKind.Percent && !IsInteger(scalarLeft)) Error("SL2111", "Operator % requires integers.", syntax.Span);
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
        if (entry.Kind == ShellTypeKind.Result) return PrimaryValueType(entry.SuccessType!.Value);
        if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field) return PrimaryValueType(entry.OutputFields![field]);
        if (entry.Kind == ShellTypeKind.Array) return PrimaryValueType(entry.ElementType!.Value);
        return type;
    }

    private ShellTypeId SecondaryValueType(ShellTypeId type)
    {
        var entry = _engine.GetTypeEntry(type);
        if (entry.Kind == ShellTypeKind.Result) return SecondaryValueType(entry.SuccessType!.Value);
        if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field) return SecondaryValueType(entry.OutputFields![field]);
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
        if (ShellEngine.IntrinsicNames.Contains(syntax.Name)) return BindIntrinsic(syntax.Name, [], primary, syntax.Span);
        if (!_engine.Commands.TryGetValue(syntax.Name, out var command)) return ErrorExpression("SL2202", $"Unknown command or intrinsic '{syntax.Name}'.", syntax.Span);
        return BindCommand(command, [], primary, syntax.Span);
    }

    private BoundExpression BindInvocation(InvocationSyntax syntax, BoundExpression? primary)
    {
        if (ShellEngine.IntrinsicNames.Contains(syntax.Name)) return BindIntrinsic(syntax.Name, syntax.Entries, primary, syntax.Span);
        if (!_engine.Commands.TryGetValue(syntax.Name, out var command)) return ErrorExpression("SL2202", $"Unknown command '{syntax.Name}'.", syntax.Span);
        return BindCommand(command, syntax.Entries, primary, syntax.Span);
    }

    private BoundExpression BindCommand(CommandDescriptor command, IReadOnlyList<InvocationEntrySyntax> entries,
        BoundExpression? primary, SourceSpan span)
    {
        var defaultInput = command.Inputs.FirstOrDefault(x => x.IsDefault);
        if (primary is not null && defaultInput is null) return ErrorExpression("SL2203", $"Command '{command.Name}' has no default input.", span);
        var suppliedInputs = new HashSet<string>(StringComparer.Ordinal);
        var suppliedArgs = new HashSet<string>(StringComparer.Ordinal);
        var secondaries = new List<BoundSecondary>(); var positional = 0; var sawNamed = false;
        foreach (var entry in entries)
        {
            if (entry.Kind == InvocationEntryKind.ExplicitInput)
            {
                sawNamed = true; var port = command.Inputs.FirstOrDefault(x => x.Name == entry.Name);
                if (port is null) { Error("SL2204", $"Command '{command.Name}' has no input '{entry.Name}'.", entry.Span); continue; }
                if (!suppliedInputs.Add(port.Name)) { Error("SL2205", $"Input '{port.Name}' is supplied more than once.", entry.Span); continue; }
                if (primary is not null && port.IsDefault) Error("SL2206", "The default input is supplied by both the pipeline and an explicit port.", entry.Span);
                var expression = BindExpression(entry.Expression, port.Type);
                var adaptation = BuildAdaptation(expression.Type, port.Type, false, entry.Span);
                secondaries.Add(new(port.Name, true, expression, adaptation, entry.Span));
            }
            else
            {
                if (entry.Kind == InvocationEntryKind.NamedArgument) sawNamed = true;
                else if (sawNamed) Error("SL2207", "Positional arguments must precede named entries.", entry.Span);
                ArgumentDescriptor? argument;
                if (entry.Kind == InvocationEntryKind.NamedArgument) argument = command.Arguments.FirstOrDefault(x => x.Name == entry.Name);
                else argument = command.Arguments.OrderBy(x => x.Position).ElementAtOrDefault(positional++);
                if (argument is null) { Error("SL2208", $"Unknown argument in '{command.Name}'.", entry.Span); continue; }
                if (!suppliedArgs.Add(argument.Name)) { Error("SL2209", $"Argument '{argument.Name}' is supplied more than once.", entry.Span); continue; }
                var expression = BindExpression(entry.Expression, argument.Type);
                var adaptation = BuildAdaptation(expression.Type, argument.Type, false, entry.Span);
                secondaries.Add(new(argument.Name, false, expression, adaptation, entry.Span));
            }
        }
        foreach (var port in command.Inputs)
            if (!(port.IsDefault && primary is not null) && !suppliedInputs.Contains(port.Name)) Error("SL2210", $"Required input '{command.Name}.{port.Name}' is missing.", span);
        foreach (var arg in command.Arguments)
            if (arg.Required && !suppliedArgs.Contains(arg.Name)) Error("SL2211", $"Required argument '{command.Name}.{arg.Name}' is missing.", span);

        var directOutput = CommandReturnType(command);
        if (primary is null)
        {
            if (defaultInput is not null && !suppliedInputs.Contains(defaultInput.Name)) Error("SL2210", $"Required input '{command.Name}.{defaultInput.Name}' is missing.", span);
            var dummy = new BoundLiteralExpression(_engine.CreateValue(_engine.Core.Bool, true), span);
            var op = new BoundCommandOperation(command, null, _engine.Core.Bool, directOutput, span);
            var invocationType = CombineSecondaryResults(directOutput, secondaries);
            return new BoundApplyExpression(dummy, op, new(AdaptationKind.Direct, _engine.Core.Bool, directOutput), secondaries, invocationType, span);
        }
        var operation = new BoundCommandOperation(command, defaultInput!.Name, defaultInput.Type, directOutput, span);
        var plan = BuildAdaptation(primary.Type, operation.ExpectedInput, true, span, directOutput);
        return new BoundApplyExpression(primary, operation, plan, secondaries, CombineSecondaryResults(plan.OutputType, secondaries), span);
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
        if (syntax.Receiver is NameSyntax typeName && _engine.TryGetType(typeName.Name, out var enumEntry) && enumEntry.Kind == ShellTypeKind.Enum)
        {
            var enumMember = enumEntry.EnumMembers.FirstOrDefault(x => x.Name == syntax.Name);
            if (enumMember is null) return ErrorExpression("SL2301", $"Enum '{typeName.Name}' has no member '{syntax.Name}'.", syntax.Span);
            if (syntax.Arguments is not null) Error("SL2302", "Enum members cannot be invoked.", syntax.Span);
            return new BoundLiteralExpression(_engine.CreateValue(enumEntry.Id, enumMember.Value), syntax.Span);
        }
        var receiver = BindExpression(syntax.Receiver);
        return BindMemberOn(receiver, syntax.Name, syntax.Arguments, syntax.Span);
    }

    private BoundExpression BindMemberOn(BoundExpression receiver, string name, IReadOnlyList<InvocationEntrySyntax>? arguments, SourceSpan span)
    {
        if (!TryFindMember(receiver.Type, name, out var expectedReceiver, out var member, out var query, out var outputField, out var outputType))
            return ErrorExpression("SL2303", $"Type '{_engine.TypeName(receiver.Type)}' has no accessible member '{name}'.", span);
        BoundOperation operation; IReadOnlyList<BoundSecondary> secondaries = [];
        if (outputField is not null)
        {
            if (arguments is not null) Error("SL2304", "Output fields cannot be invoked.", span);
            operation = new BoundMemberOperation(null, outputField, expectedReceiver, outputType, span);
        }
        else if (member is not null)
        {
            if (arguments is not null) Error("SL2305", $"Member '{name}' is not a query.", span);
            operation = new BoundMemberOperation(member, null, expectedReceiver, member.ValueType, span);
        }
        else
        {
            if (arguments is null) Error("SL2306", $"Query '{name}' requires invocation syntax.", span);
            secondaries = BindQueryArguments(query!, arguments ?? [], span);
            var direct = query!.ErrorType is { } error ? _engine.ResultOf(query.OutputType, error) : query.OutputType;
            operation = new BoundQueryOperation(query, expectedReceiver, direct, span);
        }
        var plan = BuildAdaptation(receiver.Type, operation.ExpectedInput, true, span, operation.DirectOutput);
        return new BoundApplyExpression(receiver, operation, plan, secondaries, CombineSecondaryResults(plan.OutputType, secondaries), span);
    }

    private IReadOnlyList<BoundSecondary> BindQueryArguments(QueryDescriptor query, IReadOnlyList<InvocationEntrySyntax> entries, SourceSpan span)
    {
        var result = new List<BoundSecondary>(); var supplied = new HashSet<string>(StringComparer.Ordinal); var positional = 0; var named = false;
        foreach (var entry in entries)
        {
            if (entry.Kind == InvocationEntryKind.ExplicitInput) { Error("SL2307", "Queries cannot have explicit input ports.", entry.Span); continue; }
            if (entry.Kind == InvocationEntryKind.NamedArgument) named = true; else if (named) Error("SL2207", "Positional arguments must precede named arguments.", entry.Span);
            var argument = entry.Kind == InvocationEntryKind.NamedArgument
                ? query.Arguments.FirstOrDefault(x => x.Name == entry.Name)
                : query.Arguments.OrderBy(x => x.Position).ElementAtOrDefault(positional++);
            if (argument is null) { Error("SL2208", $"Unknown argument in query '{query.Name}'.", entry.Span); continue; }
            if (!supplied.Add(argument.Name)) { Error("SL2209", $"Argument '{argument.Name}' is supplied more than once.", entry.Span); continue; }
            var expression = BindExpression(entry.Expression, argument.Type);
            result.Add(new(argument.Name, false, expression, BuildAdaptation(expression.Type, argument.Type, false, entry.Span), entry.Span));
        }
        foreach (var argument in query.Arguments) if (argument.Required && !supplied.Contains(argument.Name)) Error("SL2211", $"Required argument '{query.Name}.{argument.Name}' is missing.", span);
        return result;
    }

    private BoundExpression BindContextMember(ContextMemberSyntax syntax)
    {
        if (_contextType is null) return ErrorExpression("SL2308", "Leading '.' is valid only inside a contextual collection intrinsic.", syntax.Span);
        var receiver = new BoundNameExpression(".", false, _contextType.Value, syntax.Span);
        return BindMemberOn(receiver, syntax.Name, syntax.Arguments, syntax.Span);
    }

    private bool TryFindMember(ShellTypeId actual, string name, out ShellTypeId expected, out MemberDescriptor? member,
        out QueryDescriptor? query, out string? outputField, out ShellTypeId outputType)
    {
        expected = default; member = null; query = null; outputField = null; outputType = default;
        var entry = _engine.GetTypeEntry(actual);
        if (entry.Kind == ShellTypeKind.OutputRecord && entry.OutputFields!.TryGetValue(name, out outputType))
        { expected = actual; outputField = name; return true; }
        if (_engine.FindMemberOwner(actual, name, out member, out query) is { } owner)
        { expected = owner.Id; outputType = member?.ValueType ?? query!.OutputType; return true; }
        if (entry.Kind == ShellTypeKind.Result && TryFindMember(entry.SuccessType!.Value, name, out expected, out member, out query, out outputField, out outputType)) return true;
        if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field &&
            TryFindMember(entry.OutputFields![field], name, out expected, out member, out query, out outputField, out outputType)) return true;
        if (entry.Kind == ShellTypeKind.Array && TryFindMember(entry.ElementType!.Value, name, out expected, out member, out query, out outputField, out outputType)) return true;
        return false;
    }

    private BoundExpression BindIntrinsic(string name, IReadOnlyList<InvocationEntrySyntax> entries, BoundExpression? primary, SourceSpan span)
    {
        if (primary is null) return ErrorExpression("SL2401", $"Intrinsic '{name}' requires a pipeline input.", span);
        var entry = _engine.GetTypeEntry(primary.Type);
        if (name is "require" or "value_or" or "error" or "is_ok")
        {
            if (entry.Kind != ShellTypeKind.Result) return ErrorExpression("SL2402", $"Intrinsic '{name}' requires Result<T,E>.", span);
            var success = entry.SuccessType!.Value; var error = entry.ErrorType!.Value;
            var kind = name switch { "require" => IntrinsicKind.Require, "value_or" => IntrinsicKind.ValueOr, "error" => IntrinsicKind.Error, _ => IntrinsicKind.IsOk };
            var resultOutput = kind switch { IntrinsicKind.Require or IntrinsicKind.ValueOr => success, IntrinsicKind.Error => error, _ => _engine.Core.Bool };
            var secondaries = new List<BoundSecondary>();
            if (kind == IntrinsicKind.ValueOr)
            {
                if (success == _engine.Core.Void) Error("SL2403", "value_or is unavailable for Result<Void,E>.", span);
                var arg = entries.SingleOrDefault();
                if (arg is null || (arg.Kind == InvocationEntryKind.NamedArgument && arg.Name != "default")) Error("SL2404", "value_or requires one default argument.", span);
                else
                {
                    var expression = BindExpression(arg.Expression, success);
                    secondaries.Add(new("default", false, expression, BuildAdaptation(expression.Type, success, false, arg.Span), arg.Span));
                }
            }
            else if (entries.Count != 0) Error("SL2405", $"Intrinsic '{name}' takes no arguments.", span);
            var op = new BoundIntrinsicOperation(kind, primary.Type, resultOutput, span);
            return new BoundApplyExpression(primary, op, new(AdaptationKind.Direct, primary.Type, resultOutput), secondaries, CombineSecondaryResults(resultOutput, secondaries), span);
        }
        if (entry.Kind != ShellTypeKind.Array) return ErrorExpression("SL2406", $"Intrinsic '{name}' requires Array<T>.", span);
        var element = entry.ElementType!.Value;
        if (name is "where" or "sort")
        {
            if (entries.Count != 1) return ErrorExpression("SL2407", $"Intrinsic '{name}' requires one contextual expression.", span);
            var arg = entries[0];
            if (name == "sort" && arg.Kind == InvocationEntryKind.NamedArgument && arg.Name != "by") Error("SL2408", "sort's named argument is 'by'.", arg.Span);
            var old = _contextType; _contextType = element; var contextual = BindExpression(arg.Expression); _contextType = old;
            var contextualEntry = _engine.GetTypeEntry(contextual.Type);
            var contextualValue = contextualEntry.Kind == ShellTypeKind.Result ? contextualEntry.SuccessType!.Value : contextual.Type;
            if (name == "where" && contextualValue != _engine.Core.Bool) Error("SL2409", "where predicate must produce Bool or Result<Bool,E>.", contextual.Span);
            if (name == "sort" && !HasOrdering(contextualValue)) Error("SL2410", "sort key must have registered ordering.", contextual.Span);
            var kind = name == "where" ? IntrinsicKind.Where : IntrinsicKind.Sort;
            var contextualOutput = contextualEntry.Kind == ShellTypeKind.Result ? _engine.ResultOf(primary.Type, contextualEntry.ErrorType!.Value) : primary.Type;
            var op = new BoundIntrinsicOperation(kind, primary.Type, contextualOutput, span, contextual);
            return new BoundApplyExpression(primary, op, new(AdaptationKind.Direct, primary.Type, contextualOutput), [], contextualOutput, span);
        }
        if (name == "take")
        {
            if (entries.Count != 1) return ErrorExpression("SL2411", "take requires count.", span);
            var arg = entries[0]; var count = BindExpression(arg.Expression, _engine.Core.Int32);
            if (count is BoundLiteralExpression { Value.Value: int literal } && literal < 0) Error("SL2412", "A literal take count cannot be negative.", count.Span);
            var secondary = new BoundSecondary("count", false, count, BuildAdaptation(count.Type, _engine.Core.Int32, false, count.Span), count.Span);
            var op = new BoundIntrinsicOperation(IntrinsicKind.Take, primary.Type, primary.Type, span);
            return new BoundApplyExpression(primary, op, new(AdaptationKind.Direct, primary.Type, primary.Type), [secondary], CombineSecondaryResults(primary.Type, [secondary]), span);
        }
        if (entries.Count != 0) Error("SL2405", $"Intrinsic '{name}' takes no arguments.", span);
        IntrinsicKind intrinsic; ShellTypeId output;
        switch (name)
        {
            case "count": intrinsic = IntrinsicKind.Count; output = _engine.Core.Int32; break;
            case "first": intrinsic = IntrinsicKind.First; output = _engine.ResultOf(element, _engine.Core.EmptyCollectionError); break;
            case "sum": intrinsic = IntrinsicKind.Sum; output = element; if (!IsNumeric(element)) Error("SL2413", "sum requires a numeric array.", span); break;
            case "min": intrinsic = IntrinsicKind.Min; output = _engine.ResultOf(element, _engine.Core.EmptyCollectionError); if (!HasOrdering(element)) Error("SL2414", "min requires ordered elements.", span); break;
            case "max": intrinsic = IntrinsicKind.Max; output = _engine.ResultOf(element, _engine.Core.EmptyCollectionError); if (!HasOrdering(element)) Error("SL2414", "max requires ordered elements.", span); break;
            case "average": intrinsic = IntrinsicKind.Average; var averageType = IsInteger(element) ? _engine.Core.Float64 : element; output = _engine.ResultOf(averageType, _engine.Core.EmptyCollectionError); if (!IsNumeric(element)) Error("SL2415", "average requires numeric elements.", span); break;
            default: return ErrorExpression("SL2400", $"Unknown intrinsic '{name}'.", span);
        }
        var operation = new BoundIntrinsicOperation(intrinsic, primary.Type, output, span);
        return new BoundApplyExpression(primary, operation, new(AdaptationKind.Direct, primary.Type, output), [], output, span);
    }

    private AdaptationPlan BuildAdaptation(ShellTypeId actual, ShellTypeId expected, bool allowArray, SourceSpan span, ShellTypeId? directOutput = null)
    {
        var output = directOutput ?? expected;
        if (_engine.IsAssignable(actual, expected)) return new(AdaptationKind.Direct, actual, output);
        var entry = _engine.GetTypeEntry(actual);
        if (entry.Kind == ShellTypeKind.Result)
        {
            var inner = BuildAdaptation(entry.SuccessType!.Value, expected, allowArray, span, output);
            return new(AdaptationKind.Result, actual, WrapResultOutput(inner.OutputType, entry.ErrorType!.Value), inner);
        }
        if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field)
        {
            var inner = BuildAdaptation(entry.OutputFields![field], expected, allowArray, span, output);
            return new(AdaptationKind.DefaultOutput, actual, inner.OutputType, inner, field);
        }
        if (allowArray && entry.Kind == ShellTypeKind.Array)
        {
            var inner = BuildAdaptation(entry.ElementType!.Value, expected, true, span, output);
            return new(AdaptationKind.Array, actual, LiftOutput(inner.OutputType), inner);
        }
        Error("SL2004", $"Cannot connect {_engine.TypeName(actual)} to {_engine.TypeName(expected)}.", span, expected, actual,
            ["whole value", "result propagation", "default output", allowArray ? "array lifting" : "array lifting not allowed"]);
        return new(AdaptationKind.Direct, actual, output);
    }

    private ShellTypeId WrapResultOutput(ShellTypeId operationOutput, ShellTypeId outerError)
    {
        var outputEntry = _engine.GetTypeEntry(operationOutput);
        if (outputEntry.Kind != ShellTypeKind.Result) return _engine.ResultOf(operationOutput, outerError);
        return _engine.ResultOf(outputEntry.SuccessType!.Value, _engine.CommonError(outerError, outputEntry.ErrorType!.Value));
    }

    private ShellTypeId LiftOutput(ShellTypeId elementOutput)
    {
        if (elementOutput == _engine.Core.Void) return _engine.Core.Void;
        var entry = _engine.GetTypeEntry(elementOutput);
        if (entry.Kind != ShellTypeKind.Result) return _engine.ArrayOf(elementOutput);
        var success = entry.SuccessType!.Value;
        return _engine.ResultOf(success == _engine.Core.Void ? _engine.Core.Void : _engine.ArrayOf(success), entry.ErrorType!.Value);
    }

    private ShellTypeId CombineSecondaryResults(ShellTypeId output, IReadOnlyList<BoundSecondary> secondaries)
    {
        foreach (var secondary in secondaries)
        {
            var secondaryEntry = _engine.GetTypeEntry(secondary.Adaptation.OutputType);
            if (secondaryEntry.Kind != ShellTypeKind.Result) continue;
            var outputEntry = _engine.GetTypeEntry(output);
            output = outputEntry.Kind == ShellTypeKind.Result
                ? _engine.ResultOf(outputEntry.SuccessType!.Value, _engine.CommonError(outputEntry.ErrorType!.Value, secondaryEntry.ErrorType!.Value))
                : _engine.ResultOf(output, secondaryEntry.ErrorType!.Value);
        }
        return output;
    }

    private bool IsNumeric(ShellTypeId type) => type == _engine.Core.Int32 || type == _engine.Core.Int64 || type == _engine.Core.UInt32 ||
        type == _engine.Core.UInt64 || type == _engine.Core.Float32 || type == _engine.Core.Float64;
    private bool IsInteger(ShellTypeId type) => type == _engine.Core.Int32 || type == _engine.Core.Int64 || type == _engine.Core.UInt32 || type == _engine.Core.UInt64;
    private bool IsSignedNumeric(ShellTypeId type) => type == _engine.Core.Int32 || type == _engine.Core.Int64 || type == _engine.Core.Float32 || type == _engine.Core.Float64;
    private bool HasEquality(ShellTypeId type) => _engine.GetTypeEntry(type).Equality is not null;
    private bool HasOrdering(ShellTypeId type) => _engine.GetTypeEntry(type).Ordering is not null;
    private BoundExpression ErrorExpression(string code, string message, SourceSpan span) { Error(code, message, span); return new BoundErrorExpression(_engine.Core.Int32, span); }
    private void Error(string code, string message, SourceSpan span, ShellTypeId? expected = null, ShellTypeId? actual = null, IReadOnlyList<string>? attempts = null) =>
        _diagnostics.Add(new CompilationDiagnostic(code, message, span, expected, actual, attempts));
}
