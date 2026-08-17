namespace ShellLang;

internal sealed partial class Binder
{
	private sealed record ContextScope(int Id, ShellTypeId Type);

	private ContextScope? _contextScope;
	private int _nextContextScopeId;

	private ContextScope CreateContext(ShellTypeId type) => new(++_nextContextScopeId, type);

	private BoundExpression BindThis(ThisSyntax syntax)
	{
		if (_contextScope is null)
			return ErrorExpression("SL2308", "'this' is unavailable outside a contextual expression.", syntax.Span);
		return new BoundContextExpression(_contextScope.Id, _contextScope.Type, syntax.Span);
	}

	private T InContext<T>(ContextScope scope, Func<T> bind)
	{
		var previous = _contextScope;
		_contextScope = scope;
		try
		{
			return bind();
		}
		finally
		{
			_contextScope = previous;
		}
	}

	private static ShellTypeId EffectiveContextType(AdaptationPlan plan)
	{
		while (plan.Inner is not null)
			plan = plan.Inner;
		return plan.InputType;
	}

	private static bool UsesContext(BoundExpression expression, int scopeId) => expression switch
	{
		BoundContextExpression context => context.ScopeId == scopeId,
		BoundArrayExpression array => array.Items.Any(x => UsesContext(x, scopeId)),
		BoundUnaryExpression unary => UsesContext(unary.Operand, scopeId),
		BoundBinaryExpression binary => UsesContext(binary.Left, scopeId) || UsesContext(binary.Right, scopeId),
		BoundApplyExpression apply => UsesContext(apply.Primary, scopeId) ||
			apply.Secondary.Any(x => UsesContext(x.Expression, scopeId)) ||
			apply.Operation is BoundIntrinsicOperation { ContextExpression: { } contextual } && UsesContext(contextual, scopeId),
		BoundConstructorExpression constructor => constructor.Arguments.Any(x => UsesContext(x.Expression, scopeId)),
		_ => false
	};

	private IReadOnlyList<BoundSecondary> MarkContextual(
		IReadOnlyList<BoundSecondary> secondaries, ContextScope scope) => secondaries
		.Select(x => UsesContext(x.Expression, scope.Id) ? x with { ContextScopeId = scope.Id } : x)
		.ToArray();

	private ShellTypeId CombineContextualDirectOutput(ShellTypeId directOutput,
		IReadOnlyList<BoundSecondary> secondaries, int scopeId) =>
		CombineSecondaryResults(directOutput, secondaries.Where(x => x.ContextScopeId == scopeId).ToArray());
}
