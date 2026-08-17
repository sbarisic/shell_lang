namespace ShellLang;

internal sealed partial class Binder
{
	private AdaptationPlan BuildAdaptation(ShellTypeId actual, ShellTypeId expected, bool allowArray, SourceSpan span, ShellTypeId? directOutput = null)
	{
		var output = directOutput ?? expected;
		if (_engine.IsAssignable(actual, expected))
			return new(AdaptationKind.Direct, actual, output);
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
		if (outputEntry.Kind != ShellTypeKind.Result)
			return _engine.ResultOf(operationOutput, outerError);
		return _engine.ResultOf(outputEntry.SuccessType!.Value, _engine.CommonError(outerError, outputEntry.ErrorType!.Value));
	}

	private ShellTypeId LiftOutput(ShellTypeId elementOutput)
	{
		if (elementOutput == _engine.Core.Void)
			return _engine.Core.Void;
		var entry = _engine.GetTypeEntry(elementOutput);
		if (entry.Kind != ShellTypeKind.Result)
			return _engine.ArrayOf(elementOutput);
		var success = entry.SuccessType!.Value;
		return _engine.ResultOf(success == _engine.Core.Void ? _engine.Core.Void : _engine.ArrayOf(success), entry.ErrorType!.Value);
	}

	private ShellTypeId CombineSecondaryResults(ShellTypeId output, IReadOnlyList<BoundSecondary> secondaries)
	{
		foreach (var secondary in secondaries)
		{
			var secondaryEntry = _engine.GetTypeEntry(secondary.Adaptation.OutputType);
			if (secondaryEntry.Kind != ShellTypeKind.Result)
				continue;
			var outputEntry = _engine.GetTypeEntry(output);
			output = outputEntry.Kind == ShellTypeKind.Result
				? _engine.ResultOf(outputEntry.SuccessType!.Value, _engine.CommonError(outputEntry.ErrorType!.Value, secondaryEntry.ErrorType!.Value))
				: _engine.ResultOf(output, secondaryEntry.ErrorType!.Value);
		}
		return output;
	}
}
