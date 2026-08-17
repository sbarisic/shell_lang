namespace ShellLang;

internal abstract record BoundStatement(SourceSpan Span);
internal sealed record BoundAssignment(string Name, BoundExpression Expression, SourceSpan Span) : BoundStatement(Span);
internal sealed record BoundExpressionStatement(BoundExpression Expression, SourceSpan Span) : BoundStatement(Span);
internal abstract record BoundExpression(ShellTypeId Type, SourceSpan Span);
internal sealed record BoundErrorExpression(ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundLiteralExpression(ShellValue Value, SourceSpan Span) : BoundExpression(Value.Type, Span);
internal sealed record BoundNameExpression(string Name, bool IsGlobal, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundContextExpression(int ScopeId, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundArrayExpression(IReadOnlyList<BoundExpression> Items, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundUnaryExpression(TokenKind Operator, BoundExpression Operand, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundBinaryExpression(BoundExpression Left, TokenKind Operator, BoundExpression Right, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundApplyExpression(BoundExpression Primary, BoundOperation Operation, AdaptationPlan Adaptation,
	IReadOnlyList<BoundSecondary> Secondary, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);
internal sealed record BoundConstructorExpression(ConstructorDescriptor Constructor,
	IReadOnlyList<BoundConstructorArgument> Arguments, ShellTypeId Type, SourceSpan Span) : BoundExpression(Type, Span);

internal sealed record BoundSecondary(string Name, bool IsInput, BoundExpression Expression, AdaptationPlan Adaptation,
	SourceSpan Span, int? ContextScopeId = null);
internal sealed record BoundConstructorArgument(string Name, BoundExpression Expression, AdaptationPlan Adaptation, SourceSpan Span);
internal enum AdaptationKind
{
	Direct, Result, DefaultOutput, Array
}
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
internal enum IntrinsicKind
{
	Require, ValueOr, Error, IsOk, Where, Sort, Take, Count, Sum, First, Min, Max, Average,
	At, Last, Skip, Slice, Any, All, Select, Contains, Concat, Distinct, Reverse, Single
}
internal sealed record BoundIntrinsicOperation(IntrinsicKind Intrinsic, ShellTypeId ExpectedInput,
	ShellTypeId DirectOutput, SourceSpan Span, BoundExpression? ContextExpression = null,
	int? ContextScopeId = null) : BoundOperation(ExpectedInput, DirectOutput, Span);

internal sealed class BoundProgram
{
	public BoundProgram(IReadOnlyList<BoundStatement> statements) => Statements = statements;
	public IReadOnlyList<BoundStatement> Statements
	{
		get;
	}
}
