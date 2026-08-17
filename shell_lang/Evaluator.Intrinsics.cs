namespace ShellLang;

internal sealed partial class Evaluator
{
	private EvalOutcome InvokeIntrinsic(BoundIntrinsicOperation operation, ShellValue primary,
		IReadOnlyDictionary<string, ShellValue> arguments, IReadOnlyList<int> path)
		=> ShellEngine.IntrinsicSchemasByKind[operation.Intrinsic].Evaluator(this, operation, primary, arguments, path);

	internal static IntrinsicEvaluatorHandler CreateIntrinsicHandler(IntrinsicKind kind) => kind switch
	{
		IntrinsicKind.Require => static (e, o, p, _, _) => e.Require(o, p),
		IntrinsicKind.ValueOr => static (_, _, p, a, _) => ValueOr(p, a["default"]),
		IntrinsicKind.Error => static (e, o, p, _, _) => e.Error(o, p),
		IntrinsicKind.IsOk => static (e, _, p, _, _) => e.IsOk(p),
		IntrinsicKind.Count => static (e, _, p, _, _) => e.Count(p),
		IntrinsicKind.Take => static (e, o, p, a, _) => e.TakeOrSkip(o, p, a["count"].Get<int>(), true),
		IntrinsicKind.Skip => static (e, o, p, a, _) => e.TakeOrSkip(o, p, a["count"].Get<int>(), false),
		IntrinsicKind.At => static (e, o, p, a, _) => e.At(o, (ShellArrayValue)p.Value, a["index"].Get<int>()),
		IntrinsicKind.Slice => static (e, o, p, a, _) => e.Slice(o, p, (ShellArrayValue)p.Value,
			a["start"].Get<int>(), a["count"].Get<int>()),
		IntrinsicKind.First => static (e, o, p, _, _) => e.EmptyOrValue(o, (ShellArrayValue)p.Value,
			((ShellArrayValue)p.Value).Items.FirstOrDefault()),
		IntrinsicKind.Last => static (e, o, p, _, _) => e.EmptyOrValue(o, (ShellArrayValue)p.Value,
			((ShellArrayValue)p.Value).Items.LastOrDefault()),
		IntrinsicKind.Single => static (e, o, p, _, _) => e.Single(o, (ShellArrayValue)p.Value),
		IntrinsicKind.Reverse => static (_, _, p, _, _) => EvalOutcome.Success(new ShellValue(p.Type,
			new ShellArrayValue(((ShellArrayValue)p.Value).Items.Reverse()))),
		IntrinsicKind.Flatten => static (_, o, p, _, _) => EvalOutcome.Success(new ShellValue(o.DirectOutput,
			new ShellArrayValue(((ShellArrayValue)p.Value).Items.SelectMany(item => ((ShellArrayValue)item.Value).Items)))),
		IntrinsicKind.Concat => static (_, _, p, a, _) => EvalOutcome.Success(new ShellValue(p.Type,
			new ShellArrayValue(((ShellArrayValue)p.Value).Items.Concat(((ShellArrayValue)a["other"].Value).Items)))),
		IntrinsicKind.Contains => static (e, o, p, a, _) => e.Contains(o, p, (ShellArrayValue)p.Value, a["value"]),
		IntrinsicKind.Distinct => static (e, o, p, _, path) => o.ContextExpression is null
			? e.Distinct(o, p, (ShellArrayValue)p.Value)
			: e.ContextDistinct(o, p, (ShellArrayValue)p.Value, path),
		IntrinsicKind.Min => static (e, o, p, _, _) => e.EmptyOrValue(o, (ShellArrayValue)p.Value,
			e.Extreme((ShellArrayValue)p.Value, false, o.Span)),
		IntrinsicKind.Max => static (e, o, p, _, _) => e.EmptyOrValue(o, (ShellArrayValue)p.Value,
			e.Extreme((ShellArrayValue)p.Value, true, o.Span)),
		IntrinsicKind.Sum => static (e, o, p, _, _) => e.InvokeSum(o, p),
		IntrinsicKind.Average => static (e, o, p, _, _) => e.Average(o, (ShellArrayValue)p.Value),
		IntrinsicKind.Where => static (e, o, p, _, path) => e.ContextWhere(o, p, (ShellArrayValue)p.Value, path),
		IntrinsicKind.Sort => static (e, o, p, _, path) => e.ContextSort(o, p, (ShellArrayValue)p.Value, path),
		IntrinsicKind.Any => static (e, o, p, _, path) => e.ContextBoolean(o, (ShellArrayValue)p.Value, path, true),
		IntrinsicKind.All => static (e, o, p, _, path) => e.ContextBoolean(o, (ShellArrayValue)p.Value, path, false),
		IntrinsicKind.Select => static (e, o, p, _, path) => e.ContextSelect(o, (ShellArrayValue)p.Value, path),
		_ => throw new ArgumentOutOfRangeException(nameof(kind))
	};

	private EvalOutcome IsOk(ShellValue primary) => EvalOutcome.Success(_engine.CreateValue(_engine.Core.Bool,
		primary.Value is not ShellResultValue.Error));

	private EvalOutcome Require(BoundIntrinsicOperation operation, ShellValue primary)
	{
		var result = (ShellResultValue)primary.Value;
		if (result is ShellResultValue.Error error)
			return CoreFault("SL4001", error.Value.ToString(), operation.Span);
		return result is ShellResultValue.VoidSuccess
			? EvalOutcome.Success(null)
			: EvalOutcome.Success(((ShellResultValue.Success)result).Value);
	}

	private static EvalOutcome ValueOr(ShellValue primary, ShellValue defaultValue) => EvalOutcome.Success(
		primary.Value is ShellResultValue.Error ? defaultValue : ((ShellResultValue.Success)primary.Value).Value);

	private EvalOutcome Error(BoundIntrinsicOperation operation, ShellValue primary) =>
		primary.Value is ShellResultValue.Error error
			? EvalOutcome.Success(error.Value)
			: CoreFault("SL4005", "error requires an Err value.", operation.Span);

	private EvalOutcome Count(ShellValue primary) => EvalOutcome.Success(_engine.CreateValue(_engine.Core.Int32,
		((ShellArrayValue)primary.Value).Items.Count));

	private EvalOutcome TakeOrSkip(BoundIntrinsicOperation operation, ShellValue primary, int count, bool take)
	{
		if (count < 0)
			return CoreFault("SL4004", $"{(take ? "take" : "skip")} count cannot be negative.", operation.Span);
		var items = ((ShellArrayValue)primary.Value).Items;
		return EvalOutcome.Success(new ShellValue(primary.Type,
			new ShellArrayValue(take ? items.Take(count) : items.Skip(count))));
	}

	private EvalOutcome InvokeSum(BoundIntrinsicOperation operation, ShellValue primary)
	{
		try
		{
			return EvalOutcome.Success(Sum((ShellArrayValue)primary.Value,
				_engine.GetTypeEntry(primary.Type).ElementType!.Value));
		}
		catch (OverflowException)
		{
			return CoreFault("SL4002", "Integer overflow in sum.", operation.Span);
		}
	}

	private EvalOutcome EmptyOrValue(BoundIntrinsicOperation operation, ShellArrayValue array, ShellValue? value)
	{
		var resultType = operation.DirectOutput;
		if (array.Items.Count == 0)
		{
			var error = _engine.CreateValue(_engine.Core.EmptyCollectionError, new EmptyCollectionError());
			return EvalOutcome.Success(new ShellValue(resultType, new ShellResultValue.Error(error)));
		}
		return EvalOutcome.Success(new ShellValue(resultType, new ShellResultValue.Success(value!)));
	}

	private EvalOutcome At(BoundIntrinsicOperation operation, ShellArrayValue array, int index)
	{
		var normalized = index < 0 ? array.Items.Count + index : index;
		if (normalized < 0 || normalized >= array.Items.Count)
			return CoreFault("SL4006", $"Array index {index} is outside an array of length {array.Items.Count}.", operation.Span);
		var item = array.Items[normalized];
		return EvalOutcome.Success(item.Type == operation.DirectOutput
			? item
			: _engine.CreateValue(operation.DirectOutput, item.Value));
	}

	private EvalOutcome Slice(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array, int start, int count)
	{
		if (count < 0)
			return CoreFault("SL4004", "slice count cannot be negative.", operation.Span);
		var normalized = start < 0 ? array.Items.Count + start : start;
		if (normalized < 0 || normalized > array.Items.Count || count > array.Items.Count - normalized)
			return CoreFault("SL4007", $"Array slice ({start}, {count}) is outside an array of length {array.Items.Count}.", operation.Span);
		return EvalOutcome.Success(new ShellValue(primary.Type, new ShellArrayValue(array.Items.Skip(normalized).Take(count))));
	}

	private EvalOutcome Single(BoundIntrinsicOperation operation, ShellArrayValue array)
	{
		if (array.Items.Count == 1)
			return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Success(array.Items[0])));
		var message = $"Expected exactly one element, but the collection contains {array.Items.Count}.";
		var error = _engine.CreateValue(_engine.Core.CollectionCardinalityError,
			new CollectionCardinalityError(array.Items.Count, message));
		return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Error(error)));
	}

	private EvalOutcome Contains(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array, ShellValue expected)
	{
		var element = _engine.GetTypeEntry(primary.Type).ElementType!.Value;
		var equality = _engine.GetTypeEntry(element).Equality!;
		try
		{
			foreach (var item in array.Items)
				if (equality.CompareEqual(item.Value, expected.Value))
					return EvalOutcome.Success(_engine.CreateValue(_engine.Core.Bool, true));
			return EvalOutcome.Success(_engine.CreateValue(_engine.Core.Bool, false));
		}
		catch (Exception ex)
		{
			return EvalOutcome.Host(new HostFault("SL5102", "A registered comparison failed.", operation.Span, ex));
		}
	}

	private EvalOutcome Distinct(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array)
	{
		var element = _engine.GetTypeEntry(primary.Type).ElementType!.Value;
		var equality = _engine.GetTypeEntry(element).Equality!;
		var result = new List<ShellValue>();
		try
		{
			foreach (var item in array.Items)
				if (!result.Any(existing => equality.CompareEqual(existing.Value, item.Value)))
					result.Add(item);
			return EvalOutcome.Success(new ShellValue(primary.Type, new ShellArrayValue(result)));
		}
		catch (Exception ex)
		{
			return EvalOutcome.Host(new HostFault("SL5102", "A registered comparison failed.", operation.Span, ex));
		}
	}

	private ShellValue? Extreme(ShellArrayValue array, bool max, SourceSpan span)
	{
		if (array.Items.Count == 0)
			return null;
		var ordering = _engine.GetTypeEntry(array.Items[0].Type).Ordering!;
		var best = array.Items[0];
		try
		{
			foreach (var item in array.Items.Skip(1))
			{
				var compare = ordering.Compare(item.Value, best.Value);
				if (max ? compare > 0 : compare < 0)
					best = item;
			}
		}
		catch (Exception ex) { throw new InvalidOperationException("Registered ordering failed.", ex); }
		return best;
	}

	private ShellValue Sum(ShellArrayValue array, ShellTypeId type)
	{
		object sum;
		if (type == _engine.Core.Int32)
			sum = array.Items.Aggregate(0, (a, x) => checked(a + x.Get<int>()));
		else if (type == _engine.Core.Int64)
			sum = array.Items.Aggregate(0L, (a, x) => checked(a + x.Get<long>()));
		else if (type == _engine.Core.UInt32)
			sum = array.Items.Aggregate(0U, (a, x) => checked(a + x.Get<uint>()));
		else if (type == _engine.Core.UInt64)
			sum = array.Items.Aggregate(0UL, (a, x) => checked(a + x.Get<ulong>()));
		else if (type == _engine.Core.Float32)
			sum = array.Items.Aggregate(0F, (a, x) => a + x.Get<float>());
		else
			sum = array.Items.Aggregate(0D, (a, x) => a + x.Get<double>());
		return _engine.CreateValue(type, sum);
	}

	private EvalOutcome Average(BoundIntrinsicOperation operation, ShellArrayValue array)
	{
		if (array.Items.Count == 0)
			return EmptyOrValue(operation, array, null);
		var input = array.Items[0].Type;
		var resultEntry = _engine.GetTypeEntry(operation.DirectOutput);
		var output = resultEntry.SuccessType!.Value;
		object value = input == _engine.Core.Float32 ? array.Items.Average(x => x.Get<float>()) :
			input == _engine.Core.Float64 ? array.Items.Average(x => x.Get<double>()) :
			input == _engine.Core.Int32 ? array.Items.Average(x => (double)x.Get<int>()) :
			input == _engine.Core.Int64 ? array.Items.Average(x => (double)x.Get<long>()) :
			input == _engine.Core.UInt32 ? array.Items.Average(x => (double)x.Get<uint>()) : array.Items.Average(x => (double)x.Get<ulong>());
		var shell = _engine.CreateValue(output, value);
		return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Success(shell)));
	}

	private EvalOutcome ContextWhere(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array, IReadOnlyList<int> path)
	{
		var result = new List<ShellValue>();
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var predicateValue);
				if (stopped is not null)
					return stopped;
				if (predicateValue.Get<bool>())
					result.Add(array.Items[i]);
			}
		}
		finally { _contextValue = old; }
		var filtered = new ShellValue(primary.Type, new ShellArrayValue(result));
		return ContextSuccess(operation, filtered);
	}

	private EvalOutcome ContextSort(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array, IReadOnlyList<int> path)
	{
		var keyed = new List<(ShellValue Item, ShellValue Key, int Index)>();
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var keyValue);
				if (stopped is not null)
					return stopped;
				keyed.Add((array.Items[i], keyValue, i));
			}
		}
		finally { _contextValue = old; }
		try
		{
			var declaredKey = _engine.GetTypeEntry(operation.ContextExpression!.Type);
			var keyType = keyed.FirstOrDefault().Key?.Type ?? (declaredKey.Kind == ShellTypeKind.Result ? declaredKey.SuccessType!.Value : operation.ContextExpression.Type);
			var ordering = _engine.GetTypeEntry(keyType).Ordering!;
			var sorted = keyed.OrderBy(x => x, Comparer<(ShellValue Item, ShellValue Key, int Index)>.Create((a, b) =>
			{
				var compare = ordering.Compare(a.Key.Value, b.Key.Value);
				return compare != 0 ? compare : a.Index.CompareTo(b.Index);
			})).Select(x => x.Item);
			var sortedValue = new ShellValue(primary.Type, new ShellArrayValue(sorted));
			return ContextSuccess(operation, sortedValue);
		}
		catch (Exception ex) { return EvalOutcome.Host(new HostFault("SL5113", "Registered sort ordering failed.", operation.Span, ex)); }
	}

	private EvalOutcome ContextBoolean(BoundIntrinsicOperation operation, ShellArrayValue array,
		IReadOnlyList<int> path, bool any)
	{
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var predicate);
				if (stopped is not null)
					return stopped;
				if (predicate.Get<bool>() == any)
					return ContextSuccess(operation, _engine.CreateValue(_engine.Core.Bool, any));
			}
		}
		finally { _contextValue = old; }
		return ContextSuccess(operation, _engine.CreateValue(_engine.Core.Bool, !any));
	}

	private EvalOutcome ContextSelect(BoundIntrinsicOperation operation, ShellArrayValue array, IReadOnlyList<int> path)
	{
		var selected = new List<ShellValue>();
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var value);
				if (stopped is not null)
					return stopped;
				selected.Add(value);
			}
		}
		finally { _contextValue = old; }
		var outputEntry = _engine.GetTypeEntry(operation.DirectOutput);
		var arrayType = outputEntry.Kind == ShellTypeKind.Result ? outputEntry.SuccessType!.Value : operation.DirectOutput;
		return ContextSuccess(operation, new ShellValue(arrayType, new ShellArrayValue(selected)));
	}

	private EvalOutcome ContextDistinct(BoundIntrinsicOperation operation, ShellValue primary, ShellArrayValue array,
		IReadOnlyList<int> path)
	{
		var keys = new List<ShellValue>();
		var values = new List<ShellValue>();
		var declared = _engine.GetTypeEntry(operation.ContextExpression!.Type);
		var keyType = declared.Kind == ShellTypeKind.Result ? declared.SuccessType!.Value : operation.ContextExpression.Type;
		var equality = _engine.GetTypeEntry(keyType).Equality!;
		var old = _contextValue;
		try
		{
			for (var i = 0; i < array.Items.Count; i++)
			{
				_contextValue = array.Items[i];
				var stopped = EvaluateContextValue(operation, i, path, out var key);
				if (stopped is not null)
					return stopped;
				bool duplicate;
				try
				{
					duplicate = keys.Any(existing => equality.CompareEqual(existing.Value, key.Value));
				}
				catch (Exception ex)
				{
					return EvalOutcome.Host(new HostFault("SL5102", "A registered comparison failed.", operation.Span, ex));
				}
				if (!duplicate)
				{
					keys.Add(key);
					values.Add(array.Items[i]);
				}
			}
		}
		finally { _contextValue = old; }
		return ContextSuccess(operation, new ShellValue(primary.Type, new ShellArrayValue(values)));
	}

	private EvalOutcome? EvaluateContextValue(BoundIntrinsicOperation operation, int index, IReadOnlyList<int> path,
		out ShellValue value)
	{
		var evaluated = WithContext(operation.ContextScopeId!.Value, _contextValue!,
			() => Evaluate(operation.ContextExpression!, Append(path, index)));
		if (evaluated.Failed)
		{
			value = null!;
			return evaluated;
		}
		value = evaluated.Value!;
		if (_engine.GetTypeEntry(value.Type).Kind != ShellTypeKind.Result)
			return null;
		var contextualResult = (ShellResultValue)value.Value;
		if (contextualResult is ShellResultValue.Error error)
		{
			var frames = error.Frames.Concat([new ErrorContextFrame("array", index.ToString(), operation.Span, index)]).ToArray();
			value = null!;
			return EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Error(error.Value, frames)));
		}
		if (contextualResult is ShellResultValue.Success success)
		{
			value = success.Value;
			return null;
		}
		value = null!;
		return EvalOutcome.Host(new HostFault("SL5115", "A contextual expression produced Void.", operation.Span));
	}

	private EvalOutcome ContextSuccess(BoundIntrinsicOperation operation, ShellValue value) =>
		_engine.GetTypeEntry(operation.DirectOutput).Kind == ShellTypeKind.Result
			? EvalOutcome.Success(new ShellValue(operation.DirectOutput, new ShellResultValue.Success(value)))
			: EvalOutcome.Success(value);
}
