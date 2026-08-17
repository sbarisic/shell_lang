using System.Globalization;

namespace ShellLang;

internal sealed record CoreConversion(ShellTypeId SourceType, ShellTypeId TargetType, bool IsFallible);

public sealed partial class ShellEngine
{
	internal bool IsNumericType(ShellTypeId type) => type == Core.Int32 || type == Core.Int64 ||
		type == Core.UInt32 || type == Core.UInt64 || type == Core.Float32 || type == Core.Float64;

	internal bool IsConversionTarget(ShellTypeId type) => IsNumericType(type) || type == Core.String || type == Core.Bool;

	internal bool TryGetConversion(ShellTypeId source, ShellTypeId target, out CoreConversion conversion)
	{
		if (source == target && (IsNumericType(source) || source == Core.String || source == Core.Bool))
		{
			conversion = new(source, target, false);
			return true;
		}
		if (target == Core.String && (source == Core.Bool || IsNumericType(source) || GetTypeEntry(source).Kind == ShellTypeKind.Enum))
		{
			conversion = new(source, target, false);
			return true;
		}
		if (!IsNumericType(source) || !IsNumericType(target))
		{
			conversion = null!;
			return false;
		}
		var guaranteed = (source == Core.Int32 && (target == Core.Int64 || target == Core.Float64)) ||
			(source == Core.UInt32 && (target == Core.Int64 || target == Core.UInt64 || target == Core.Float64)) ||
			(source == Core.Float32 && target == Core.Float64);
		conversion = new(source, target, !guaranteed);
		return true;
	}

	internal string ConversionString(ShellValue value)
	{
		if (value.Type == Core.String)
			return value.Get<string>();
		if (value.Type == Core.Bool)
			return value.Get<bool>() ? "true" : "false";
		if (value.Type == Core.Int32)
			return value.Get<int>().ToString(CultureInfo.InvariantCulture);
		if (value.Type == Core.Int64)
			return value.Get<long>().ToString(CultureInfo.InvariantCulture);
		if (value.Type == Core.UInt32)
			return value.Get<uint>().ToString(CultureInfo.InvariantCulture);
		if (value.Type == Core.UInt64)
			return value.Get<ulong>().ToString(CultureInfo.InvariantCulture);
		if (value.Type == Core.Float32)
			return value.Get<float>().ToString("R", CultureInfo.InvariantCulture);
		if (value.Type == Core.Float64)
			return value.Get<double>().ToString("R", CultureInfo.InvariantCulture);
		var entry = GetTypeEntry(value.Type);
		if (entry.Kind == ShellTypeKind.Enum)
			return entry.EnumMembers.First(x => Equals(x.Value, value.Value)).Name;
		throw new InvalidOperationException("Value does not support String conversion.");
	}

	internal IReadOnlyList<CoreConversion> ConversionsTo(ShellTypeId target)
	{
		var sources = new[] { Core.Bool, Core.Int32, Core.Int64, Core.UInt32, Core.UInt64, Core.Float32, Core.Float64, Core.String }
			.Concat(_typeEntries.Values.Where(x => x.Kind == ShellTypeKind.Enum).Select(x => x.Id));
		return sources.Distinct().Select(source => TryGetConversion(source, target, out var conversion) ? conversion : null)
			.Where(static x => x is not null).Cast<CoreConversion>().ToArray();
	}
}
