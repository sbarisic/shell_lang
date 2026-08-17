namespace ShellLang;

internal sealed partial class Binder
{
	private BoundExpression BindIntrinsic(string name, IReadOnlyList<InvocationEntrySyntax> entries, BoundExpression? primary, SourceSpan span)
	{
		if (primary is null)
			return ErrorExpression("SL2401", $"Intrinsic '{name}' requires a pipeline input.", span);
		var entry = _engine.GetTypeEntry(primary.Type);
		if (name is "require" or "value_or" or "error" or "is_ok")
		{
			if (entry.Kind != ShellTypeKind.Result)
				return ErrorExpression("SL2402", $"Intrinsic '{name}' requires Result<T,E>.", span);
			var success = entry.SuccessType!.Value;
			var error = entry.ErrorType!.Value;
			var kind = name switch
			{
				"require" => IntrinsicKind.Require,
				"value_or" => IntrinsicKind.ValueOr,
				"error" => IntrinsicKind.Error,
				_ => IntrinsicKind.IsOk
			};
			var resultOutput = kind switch
			{
				IntrinsicKind.Require or IntrinsicKind.ValueOr => success,
				IntrinsicKind.Error => error,
				_ => _engine.Core.Bool
			};
			var secondaries = new List<BoundSecondary>();
			var resultScope = CreateContext(primary.Type);
			if (kind == IntrinsicKind.ValueOr)
			{
				if (success == _engine.Core.Void)
					Error("SL2403", "value_or is unavailable for Result<Void,E>.", span);
				if (entries.Count != 1 || entries[0].Kind == InvocationEntryKind.ExplicitInput ||
					entries[0].Kind == InvocationEntryKind.NamedArgument && entries[0].Name != "default")
					Error("SL2404", "value_or requires one default argument.", span);
				else
				{
					var arg = entries[0];
					var expression = InContext(resultScope, () => BindExpression(arg.Expression, success));
					secondaries.Add(new("default", false, expression, BuildAdaptation(expression.Type, success, false, arg.Span), arg.Span));
				}
			}
			else if (entries.Count != 0)
				Error("SL2405", $"Intrinsic '{name}' takes no arguments.", span);
			secondaries = MarkContextual(secondaries, resultScope).ToList();
			var contextualOutput = CombineContextualDirectOutput(resultOutput, secondaries, resultScope.Id);
			var op = new BoundIntrinsicOperation(kind, primary.Type, resultOutput, span);
			var plan = new AdaptationPlan(AdaptationKind.Direct, primary.Type, contextualOutput);
			var ordinary = secondaries.Where(x => x.ContextScopeId != resultScope.Id).ToArray();
			return new BoundApplyExpression(primary, op, plan, secondaries,
				CombineSecondaryResults(plan.OutputType, ordinary), span);
		}
		var collectionType = FindCollectionInput(primary.Type);
		if (collectionType is null)
			return ErrorExpression("SL2406", $"Intrinsic '{name}' requires Array<T>.", span);
		var collectionEntry = _engine.GetTypeEntry(collectionType.Value);
		var element = collectionEntry.ElementType!.Value;
		var collectionPreliminary = BuildAdaptation(primary.Type, collectionType.Value, false, span, collectionType.Value);
		var collectionScope = CreateContext(EffectiveContextType(collectionPreliminary));

		BoundExpression Apply(IntrinsicKind kind, ShellTypeId directOutput,
			IReadOnlyList<BoundSecondary>? secondaries = null, BoundExpression? context = null)
		{
			secondaries ??= [];
			secondaries = MarkContextual(secondaries, collectionScope);
			var contextualOutput = CombineContextualDirectOutput(directOutput, secondaries, collectionScope.Id);
			var primaryPlan = BuildAdaptation(primary.Type, collectionType.Value, false, span, contextualOutput);
			var ordinary = secondaries.Where(x => x.ContextScopeId != collectionScope.Id).ToArray();
			var finalOutput = CombineSecondaryResults(primaryPlan.OutputType, ordinary);
			var op = new BoundIntrinsicOperation(kind, collectionType.Value, directOutput, span, context);
			return new BoundApplyExpression(primary, op, primaryPlan,
				secondaries, finalOutput, span);
		}

		if (name is "where" or "sort" or "any" or "all" or "select" || (name == "distinct" && entries.Count != 0))
		{
			var parameter = name switch
			{
				"sort" or "distinct" => "by",
				"select" => "selector",
				_ => "predicate"
			};
			if (!TryMatchIntrinsicArguments(name, entries, [parameter], span, out var matched))
				return new BoundErrorExpression(_engine.Core.Any, span);
			var arg = matched[0].Entry;
			var contextScope = CreateContext(element);
			var contextual = InContext(contextScope, () => BindExpression(arg.Expression));
			var contextualEntry = _engine.GetTypeEntry(contextual.Type);
			var contextualValue = contextualEntry.Kind == ShellTypeKind.Result ? contextualEntry.SuccessType!.Value : contextual.Type;
			if (name is "where" or "any" or "all" && contextualValue != _engine.Core.Bool)
				Error("SL2409", $"{name} predicate must produce Bool or Result<Bool,E>.", contextual.Span);
			if (name == "sort" && !HasOrdering(contextualValue))
				Error("SL2410", "sort key must have registered ordering.", contextual.Span);
			if (name == "distinct" && !HasEquality(contextualValue))
				Error("SL2425", "distinct key must have registered equality.", contextual.Span);
			if (name == "select" && contextualValue == _engine.Core.Void)
				Error("SL2424", "select selector cannot produce Void.", contextual.Span);

			var directOutput = name switch
			{
				"any" or "all" => _engine.Core.Bool,
				"select" => _engine.ArrayOf(contextualValue == _engine.Core.Void ? _engine.Core.Any : contextualValue),
				_ => collectionType.Value
			};
			if (contextualEntry.Kind == ShellTypeKind.Result)
				directOutput = _engine.ResultOf(directOutput, contextualEntry.ErrorType!.Value);
			var kind = name switch
			{
				"where" => IntrinsicKind.Where,
				"sort" => IntrinsicKind.Sort,
				"any" => IntrinsicKind.Any,
				"all" => IntrinsicKind.All,
				"select" => IntrinsicKind.Select,
				_ => IntrinsicKind.Distinct
			};
			var primaryPlan = BuildAdaptation(primary.Type, collectionType.Value, false, span, directOutput);
			var contextualOperation = new BoundIntrinsicOperation(kind, collectionType.Value, directOutput, span,
				contextual, contextScope.Id);
			return new BoundApplyExpression(primary, contextualOperation, primaryPlan, [], primaryPlan.OutputType, span);
		}

		if (name is "take" or "skip")
		{
			var secondaries = BindIntrinsicValueArguments(name, entries, [("count", _engine.Core.Int32)], span, collectionScope);
			if (secondaries is null)
				return new BoundErrorExpression(_engine.Core.Any, span);
			if (IsNegativeInt32Literal(secondaries[0].Expression))
				Error("SL2412", $"A literal {name} count cannot be negative.", secondaries[0].Span);
			return Apply(name == "take" ? IntrinsicKind.Take : IntrinsicKind.Skip, collectionType.Value, secondaries);
		}

		if (name == "at")
		{
			var secondaries = BindIntrinsicValueArguments(name, entries, [("index", _engine.Core.Int32)], span, collectionScope);
			return secondaries is null ? new BoundErrorExpression(_engine.Core.Any, span) : Apply(IntrinsicKind.At, element, secondaries);
		}

		if (name == "slice")
		{
			var secondaries = BindIntrinsicValueArguments(name, entries,
				[("start", _engine.Core.Int32), ("count", _engine.Core.Int32)], span, collectionScope);
			if (secondaries is null)
				return new BoundErrorExpression(_engine.Core.Any, span);
			var count = secondaries.Single(x => x.Name == "count");
			if (IsNegativeInt32Literal(count.Expression))
				Error("SL2422", "A literal slice count cannot be negative.", count.Span);
			return Apply(IntrinsicKind.Slice, collectionType.Value, secondaries);
		}

		if (name == "contains")
		{
			if (!HasEquality(element))
				Error("SL2425", "contains requires elements with registered equality.", span);
			var secondaries = BindIntrinsicValueArguments(name, entries, [("value", element)], span, collectionScope);
			return secondaries is null ? new BoundErrorExpression(_engine.Core.Any, span) : Apply(IntrinsicKind.Contains, _engine.Core.Bool, secondaries);
		}

		if (name == "concat")
		{
			var secondaries = BindIntrinsicValueArguments(name, entries, [("other", collectionType.Value)], span, collectionScope);
			return secondaries is null ? new BoundErrorExpression(_engine.Core.Any, span) : Apply(IntrinsicKind.Concat, collectionType.Value, secondaries);
		}

		if (entries.Count != 0)
		{
			Error("SL2405", $"Intrinsic '{name}' takes no arguments.", span);
			return new BoundErrorExpression(_engine.Core.Any, span);
		}
		IntrinsicKind intrinsic;
		ShellTypeId output;
		switch (name)
		{
			case "count":
				intrinsic = IntrinsicKind.Count;
				output = _engine.Core.Int32;
				break;
			case "first":
				intrinsic = IntrinsicKind.First;
				output = _engine.ResultOf(element, _engine.Core.EmptyCollectionError);
				break;
			case "last":
				intrinsic = IntrinsicKind.Last;
				output = _engine.ResultOf(element, _engine.Core.EmptyCollectionError);
				break;
			case "reverse":
				intrinsic = IntrinsicKind.Reverse;
				output = collectionType.Value;
				break;
			case "single":
				intrinsic = IntrinsicKind.Single;
				output = _engine.ResultOf(element, _engine.Core.CollectionCardinalityError);
				break;
			case "distinct":
				intrinsic = IntrinsicKind.Distinct;
				output = collectionType.Value;
				if (!HasEquality(element))
					Error("SL2425", "distinct requires elements with registered equality.", span);
				break;
			case "sum":
				intrinsic = IntrinsicKind.Sum;
				output = element;
				if (!IsNumeric(element))
					Error("SL2413", "sum requires a numeric array.", span);
				break;
			case "min":
				intrinsic = IntrinsicKind.Min;
				output = _engine.ResultOf(element, _engine.Core.EmptyCollectionError);
				if (!HasOrdering(element))
					Error("SL2414", "min requires ordered elements.", span);
				break;
			case "max":
				intrinsic = IntrinsicKind.Max;
				output = _engine.ResultOf(element, _engine.Core.EmptyCollectionError);
				if (!HasOrdering(element))
					Error("SL2414", "max requires ordered elements.", span);
				break;
			case "average":
				intrinsic = IntrinsicKind.Average;
				var averageType = IsInteger(element) ? _engine.Core.Float64 : element;
				output = _engine.ResultOf(averageType, _engine.Core.EmptyCollectionError);
				if (!IsNumeric(element))
					Error("SL2415", "average requires numeric elements.", span);
				break;
			default:
				return ErrorExpression("SL2400", $"Unknown intrinsic '{name}'.", span);
		}
		var operation = new BoundIntrinsicOperation(intrinsic, collectionType.Value, output, span);
		var adaptation = BuildAdaptation(primary.Type, collectionType.Value, false, span, output);
		return new BoundApplyExpression(primary, operation, adaptation, [], adaptation.OutputType, span);
	}

	private ShellTypeId? FindCollectionInput(ShellTypeId type)
	{
		var entry = _engine.GetTypeEntry(type);
		if (entry.Kind == ShellTypeKind.Array)
			return type;
		if (entry.Kind == ShellTypeKind.Result)
			return FindCollectionInput(entry.SuccessType!.Value);
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field)
			return FindCollectionInput(entry.OutputFields![field]);
		return null;
	}

	private List<BoundSecondary>? BindIntrinsicValueArguments(string intrinsic, IReadOnlyList<InvocationEntrySyntax> entries,
		IReadOnlyList<(string Name, ShellTypeId Type)> parameters, SourceSpan span, ContextScope scope)
	{
		if (!TryMatchIntrinsicArguments(intrinsic, entries, parameters.Select(x => x.Name).ToArray(), span, out var matched))
			return null;
		var parameterTypes = parameters.ToDictionary(x => x.Name, x => x.Type, StringComparer.Ordinal);
		var secondaries = new List<BoundSecondary>();
		foreach (var (name, entry) in matched)
		{
			var expected = parameterTypes[name];
			var expression = InContext(scope, () => BindExpression(entry.Expression, expected));
			secondaries.Add(new(name, false, expression,
				BuildAdaptation(expression.Type, expected, false, entry.Span), entry.Span));
		}
		return secondaries;
	}

	private bool TryMatchIntrinsicArguments(string intrinsic, IReadOnlyList<InvocationEntrySyntax> entries,
		IReadOnlyList<string> parameters, SourceSpan span, out List<(string Name, InvocationEntrySyntax Entry)> matched)
	{
		matched = [];
		var supplied = new HashSet<string>(StringComparer.Ordinal);
		var positional = 0;
		var sawNamed = false;
		var valid = true;
		foreach (var entry in entries)
		{
			if (entry.Kind == InvocationEntryKind.ExplicitInput)
			{
				Error("SL2416", $"Intrinsic '{intrinsic}' does not accept explicit inputs.", entry.Span);
				valid = false;
				continue;
			}

			string? name;
			if (entry.Kind == InvocationEntryKind.NamedArgument)
			{
				sawNamed = true;
				name = parameters.FirstOrDefault(x => x == entry.Name);
				if (name is null)
				{
					Error("SL2417", $"Intrinsic '{intrinsic}' has no argument '{entry.Name}'.", entry.Span);
					valid = false;
					continue;
				}
			}
			else
			{
				if (sawNamed)
				{
					Error("SL2419", "Positional arguments must precede named entries.", entry.Span);
					valid = false;
				}
				name = positional < parameters.Count ? parameters[positional++] : null;
				if (name is null)
				{
					Error("SL2420", $"Too many arguments for intrinsic '{intrinsic}'.", entry.Span);
					valid = false;
					continue;
				}
			}

			if (!supplied.Add(name))
			{
				Error("SL2418", $"Argument '{name}' is supplied more than once.", entry.Span);
				valid = false;
				continue;
			}
			matched.Add((name, entry));
		}

		foreach (var parameter in parameters)
			if (!supplied.Contains(parameter))
			{
				Error("SL2421", $"Required argument '{intrinsic}.{parameter}' is missing.", span);
				valid = false;
			}
		return valid;
	}

	private static bool IsNegativeInt32Literal(BoundExpression expression) =>
		expression is BoundApplyExpression
		{
			Primary: BoundLiteralExpression { Value.Value: int value },
			Operation: BoundPrimitiveOperation { Operator: TokenKind.Minus }
		} && value > 0;
}
