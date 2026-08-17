namespace ShellLang;

internal sealed partial class Evaluator
{
	private EvalOutcome EvaluateConversion(BoundConversionExpression expression, IReadOnlyList<int> path)
	{
		var evaluated = Evaluate(expression.Operand, path);
		if (evaluated.Failed)
			return evaluated;
		if (evaluated.Value is null)
			return EvalOutcome.Host(new HostFault("SL5014", "A conversion operand produced Void.", expression.Span));
		var operand = evaluated.Value;
		var operandEntry = _engine.GetTypeEntry(operand.Type);
		if (operandEntry.Kind == ShellTypeKind.Result)
		{
			if (operand.Value is ShellResultValue.Error)
				return EvalOutcome.Success(RetypeResult(operand, expression.Type));
			if (operand.Value is not ShellResultValue.Success success)
				return EvalOutcome.Host(new HostFault("SL5008", "A conversion received an invalid Result payload.", expression.Span));
			operand = success.Value;
		}

		if (!TryConvert(operand, expression.Conversion.TargetType, out var converted, out var reason))
		{
			var error = _engine.CreateValue(_engine.Core.ConversionError,
				new ConversionError(expression.Conversion.SourceType, expression.Conversion.TargetType, reason!));
			return EvalOutcome.Success(new ShellValue(expression.Type, new ShellResultValue.Error(error)));
		}
		var value = _engine.CreateValue(expression.Conversion.TargetType, converted!);
		return _engine.GetTypeEntry(expression.Type).Kind == ShellTypeKind.Result
			? EvalOutcome.Success(new ShellValue(expression.Type, new ShellResultValue.Success(value)))
			: EvalOutcome.Success(value);
	}

	private bool TryConvert(ShellValue source, ShellTypeId target, out object? value, out string? reason)
	{
		reason = null;
		if (source.Type == target)
		{
			value = source.Value;
			return true;
		}
		if (target == _engine.Core.String)
		{
			value = _engine.ConversionString(source);
			return true;
		}
		if (source.Type == _engine.Core.Int32 && target == _engine.Core.Int64)
		{
			value = (long)source.Get<int>();
			return true;
		}
		if (source.Type == _engine.Core.Int32 && target == _engine.Core.Float64)
		{
			value = (double)source.Get<int>();
			return true;
		}
		if (source.Type == _engine.Core.UInt32 && target == _engine.Core.Int64)
		{
			value = (long)source.Get<uint>();
			return true;
		}
		if (source.Type == _engine.Core.UInt32 && target == _engine.Core.UInt64)
		{
			value = (ulong)source.Get<uint>();
			return true;
		}
		if (source.Type == _engine.Core.UInt32 && target == _engine.Core.Float64)
		{
			value = (double)source.Get<uint>();
			return true;
		}
		if (source.Type == _engine.Core.Float32 && target == _engine.Core.Float64)
		{
			value = (double)source.Get<float>();
			return true;
		}
		return TryCheckedNumeric(source, target, out value, out reason);
	}

	private bool TryCheckedNumeric(ShellValue source, ShellTypeId target, out object? value, out string? reason)
	{
		value = null;
		reason = null;
		var floating = source.Type == _engine.Core.Float32 || source.Type == _engine.Core.Float64;
		var number = floating
			? source.Type == _engine.Core.Float32 ? source.Get<float>() : source.Get<double>()
			: 0D;
		if (floating && !double.IsFinite(number))
			return Fail("The source must be finite.", out reason);

		if (target == _engine.Core.Float32)
		{
			var converted = floating ? (float)number : IntegerToFloat32(source);
			if (!float.IsFinite(converted))
				return Fail("The value is outside the Float32 range.", out reason);
			var exact = floating ? (double)converted == number : Float32MatchesInteger(converted, source);
			if (!exact)
				return Fail("The value cannot be represented exactly as Float32.", out reason);
			value = converted;
			return true;
		}
		if (target == _engine.Core.Float64)
		{
			var converted = IntegerToFloat64(source);
			if (!Float64MatchesInteger(converted, source))
				return Fail("The value cannot be represented exactly as Float64.", out reason);
			value = converted;
			return true;
		}

		decimal integral;
		if (floating)
		{
			if (Math.Truncate(number) != number)
				return Fail("The source must be an integer value.", out reason);
			try { integral = (decimal)number; }
			catch (OverflowException) { return Fail("The value is outside the target range.", out reason); }
		}
		else
			integral = IntegerDecimal(source);

		if (target == _engine.Core.Int32 && integral >= int.MinValue && integral <= int.MaxValue)
			value = (int)integral;
		else if (target == _engine.Core.Int64 && integral >= long.MinValue && integral <= long.MaxValue)
			value = (long)integral;
		else if (target == _engine.Core.UInt32 && integral >= uint.MinValue && integral <= uint.MaxValue)
			value = (uint)integral;
		else if (target == _engine.Core.UInt64 && integral >= ulong.MinValue && integral <= ulong.MaxValue)
			value = (ulong)integral;
		else
			return Fail("The value is outside the target range.", out reason);
		return true;
	}

	private decimal IntegerDecimal(ShellValue value)
	{
		if (value.Type == _engine.Core.Int32)
			return value.Get<int>();
		if (value.Type == _engine.Core.Int64)
			return value.Get<long>();
		if (value.Type == _engine.Core.UInt32)
			return value.Get<uint>();
		return value.Get<ulong>();
	}
	private float IntegerToFloat32(ShellValue value)
	{
		if (value.Type == _engine.Core.Int32)
			return value.Get<int>();
		if (value.Type == _engine.Core.Int64)
			return value.Get<long>();
		if (value.Type == _engine.Core.UInt32)
			return value.Get<uint>();
		return value.Get<ulong>();
	}
	private double IntegerToFloat64(ShellValue value)
	{
		if (value.Type == _engine.Core.Int32)
			return value.Get<int>();
		if (value.Type == _engine.Core.Int64)
			return value.Get<long>();
		if (value.Type == _engine.Core.UInt32)
			return value.Get<uint>();
		return value.Get<ulong>();
	}
	private bool Float32MatchesInteger(float value, ShellValue source)
	{
		if (source.Type == _engine.Core.Int32)
			return value >= int.MinValue && value <= int.MaxValue && (int)value == source.Get<int>();
		if (source.Type == _engine.Core.Int64)
			return value >= long.MinValue && value < 9_223_372_036_854_775_808F && (long)value == source.Get<long>();
		if (source.Type == _engine.Core.UInt32)
			return value >= 0 && value < 4_294_967_296F && (uint)value == source.Get<uint>();
		return value >= 0 && value < 18_446_744_073_709_551_616F && (ulong)value == source.Get<ulong>();
	}
	private bool Float64MatchesInteger(double value, ShellValue source)
	{
		if (source.Type == _engine.Core.Int32)
			return value >= int.MinValue && value <= int.MaxValue && (int)value == source.Get<int>();
		if (source.Type == _engine.Core.Int64)
			return value >= long.MinValue && value < 9_223_372_036_854_775_808D && (long)value == source.Get<long>();
		if (source.Type == _engine.Core.UInt32)
			return value >= 0 && value <= uint.MaxValue && (uint)value == source.Get<uint>();
		return value >= 0 && value < 18_446_744_073_709_551_616D && (ulong)value == source.Get<ulong>();
	}
	private static bool Fail(string message, out string? reason)
	{
		reason = message;
		return false;
	}
}
