namespace ShellLang;

internal sealed partial class Binder
{
	private BoundExpression BindConstructor(TypeEntry type, InvocationSyntax syntax)
	{
		if (type.Constructor is not { } constructor)
			return ErrorExpression("SL2501", $"Type '{type.Name}' is not constructible.", syntax.Span);

		var arguments = new List<BoundConstructorArgument>();
		var supplied = new HashSet<string>(StringComparer.Ordinal);
		var positional = 0;
		var sawNamed = false;
		foreach (var entry in syntax.Entries)
		{
			if (entry.Kind == InvocationEntryKind.ExplicitInput)
			{
				Error("SL2503", $"Constructor '{type.Name}' does not accept explicit input entries.", entry.Span);
				continue;
			}
			if (entry.Kind == InvocationEntryKind.NamedArgument)
				sawNamed = true;
			else if (sawNamed)
				Error("SL2207", "Positional arguments must precede named arguments.", entry.Span);

			var argument = entry.Kind == InvocationEntryKind.NamedArgument
				? constructor.Arguments.FirstOrDefault(x => x.Name == entry.Name)
				: constructor.Arguments.OrderBy(x => x.Position).ElementAtOrDefault(positional++);
			if (argument is null)
			{
				Error("SL2208", $"Unknown argument in constructor '{type.Name}'.", entry.Span);
				continue;
			}
			if (!supplied.Add(argument.Name))
			{
				Error("SL2209", $"Argument '{argument.Name}' is supplied more than once.", entry.Span);
				continue;
			}
			var expression = BindExpression(entry.Expression, argument.Type);
			arguments.Add(new(argument.Name, expression,
				BuildAdaptation(expression.Type, argument.Type, false, entry.Span), entry.Span));
		}
		foreach (var argument in constructor.Arguments)
			if (argument.Required && !supplied.Contains(argument.Name))
				Error("SL2211", $"Required argument '{type.Name}.{argument.Name}' is missing.", syntax.Span);

		var output = constructor.ErrorType is { } error ? _engine.ResultOf(type.Id, error) : type.Id;
		foreach (var argument in arguments)
		{
			var argumentType = _engine.GetTypeEntry(argument.Adaptation.OutputType);
			if (argumentType.Kind != ShellTypeKind.Result)
				continue;
			var outputType = _engine.GetTypeEntry(output);
			output = outputType.Kind == ShellTypeKind.Result
				? _engine.ResultOf(outputType.SuccessType!.Value,
					_engine.CommonError(outputType.ErrorType!.Value, argumentType.ErrorType!.Value))
				: _engine.ResultOf(output, argumentType.ErrorType!.Value);
		}
		return new BoundConstructorExpression(constructor, arguments, output, syntax.Span);
	}
}
