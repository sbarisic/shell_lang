namespace ShellLang;

internal sealed partial class Evaluator
{
	private readonly Dictionary<int, ShellValue> _contextValues = [];

	private EvalOutcome EvaluateContext(BoundContextExpression expression)
	{
		return _contextValues.TryGetValue(expression.ScopeId, out var value)
			? EvalOutcome.Success(value)
			: EvalOutcome.Host(new HostFault("SL5010", "No contextual value is active.", expression.Span));
	}

	private EvalOutcome WithContext(int scopeId, ShellValue value, Func<EvalOutcome> evaluate)
	{
		var hadPrevious = _contextValues.TryGetValue(scopeId, out var previous);
		_contextValues[scopeId] = value;
		try
		{
			return evaluate();
		}
		finally
		{
			if (hadPrevious)
				_contextValues[scopeId] = previous!;
			else
				_contextValues.Remove(scopeId);
		}
	}
}
