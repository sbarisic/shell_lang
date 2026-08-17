using System.Globalization;
using System.Text;

namespace ShellLang;

public sealed record ValueFormatOptions
{
	public int MaxDepth
	{
		get; init;
	} = 8;
}

public sealed partial class ShellEngine
{
	public string FormatValue(ShellValue value, ShellSession session, ValueFormatOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(value);
		ArgumentNullException.ThrowIfNull(session);
		options ??= new ValueFormatOptions();
		if (options.MaxDepth < 1)
			throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be at least 1.");
		return new DescriptorValueFormatter(this, session, options.MaxDepth).Format(value);
	}

	private sealed class DescriptorValueFormatter
	{
		private readonly ShellEngine _engine;
		private readonly ShellSession _session;
		private readonly int _maxDepth;
		private readonly HashSet<object> _active = new(ReferenceEqualityComparer.Instance);

		public DescriptorValueFormatter(ShellEngine engine, ShellSession session, int maxDepth)
		{
			_engine = engine;
			_session = session;
			_maxDepth = maxDepth;
		}

		public string Format(ShellValue value) => Format(value, 0, []);

		private string Format(ShellValue value, int depth, IReadOnlyList<int> arrayIndexPath)
		{
			var entry = _engine.GetTypeEntry(value.Type);
			if (TryFormatPrimitive(value, out var primitive))
				return primitive;
			if (entry.Kind == ShellTypeKind.Enum)
				return entry.EnumMembers.FirstOrDefault(member => Equals(member.Value, value.Value))?.Name ?? entry.Name;

			var members = entry.Kind == ShellTypeKind.Host
				? _engine.AccessibleMembers(value.Type).ToArray()
				: [];
			var recursive = entry.Kind is ShellTypeKind.Array or ShellTypeKind.Result or ShellTypeKind.OutputRecord || members.Length != 0;
			if (!recursive)
				return entry.Name;
			if (_active.Contains(value.Value))
				return $"<cycle: {entry.Name}>";
			if (depth >= _maxDepth)
				return $"<max-depth: {entry.Name}>";

			_active.Add(value.Value);
			try
			{
				return entry.Kind switch
				{
					ShellTypeKind.Array => FormatArray(value, depth, arrayIndexPath),
					ShellTypeKind.Result => FormatResult(value, depth, arrayIndexPath),
					ShellTypeKind.OutputRecord => FormatOutputRecord(value, entry, depth, arrayIndexPath),
					_ => FormatHost(value, entry, members, depth, arrayIndexPath)
				};
			}
			finally { _active.Remove(value.Value); }
		}

		private bool TryFormatPrimitive(ShellValue value, out string result)
		{
			if (value.Type == _engine.Core.String)
				result = Quote(value.Get<string>());
			else if (value.Type == _engine.Core.Bool)
				result = value.Get<bool>() ? "true" : "false";
			else if (value.Type == _engine.Core.Int32)
				result = value.Get<int>().ToString(CultureInfo.InvariantCulture);
			else if (value.Type == _engine.Core.Int64)
				result = value.Get<long>().ToString(CultureInfo.InvariantCulture);
			else if (value.Type == _engine.Core.UInt32)
				result = value.Get<uint>().ToString(CultureInfo.InvariantCulture);
			else if (value.Type == _engine.Core.UInt64)
				result = value.Get<ulong>().ToString(CultureInfo.InvariantCulture);
			else if (value.Type == _engine.Core.Float32)
				result = value.Get<float>().ToString("R", CultureInfo.InvariantCulture);
			else if (value.Type == _engine.Core.Float64)
				result = value.Get<double>().ToString("R", CultureInfo.InvariantCulture);
			else
			{
				result = string.Empty;
				return false;
			}
			return true;
		}

		private string FormatArray(ShellValue value, int depth, IReadOnlyList<int> arrayIndexPath)
		{
			if (value.Value is not ShellArrayValue array)
				return Unavailable(value.Type);
			var items = new string[array.Items.Count];
			for (var i = 0; i < items.Length; i++)
				items[i] = Format(array.Items[i], depth + 1, Append(arrayIndexPath, i));
			return $"[{string.Join(", ", items)}]";
		}

		private string FormatResult(ShellValue value, int depth, IReadOnlyList<int> arrayIndexPath) => value.Value switch
		{
			ShellResultValue.Success success => $"Ok({Format(success.Value, depth + 1, arrayIndexPath)})",
			ShellResultValue.VoidSuccess => "Ok",
			ShellResultValue.Error error => $"Err({Format(error.Value, depth + 1, arrayIndexPath)})",
			_ => Unavailable(value.Type)
		};

		private string FormatOutputRecord(ShellValue value, TypeEntry entry, int depth, IReadOnlyList<int> arrayIndexPath)
		{
			if (value.Value is not ShellOutputRecordValue record)
				return Unavailable(value.Type);
			var fields = new List<string>();
			foreach (var field in entry.OutputFields!)
			{
				var rendered = record.Fields.TryGetValue(field.Key, out var fieldValue) && IsValidValue(fieldValue, field.Value)
					? Format(fieldValue, depth + 1, arrayIndexPath)
					: Unavailable(field.Value);
				fields.Add($"{field.Key}: {rendered}");
			}
			return $"{entry.Name} {{ {string.Join(", ", fields)} }}";
		}

		private string FormatHost(ShellValue receiver, TypeEntry entry, IReadOnlyList<MemberDescriptor> members,
			int depth, IReadOnlyList<int> arrayIndexPath)
		{
			var rendered = new List<string>(members.Count);
			foreach (var member in members)
			{
				var memberValue = GetMemberValue(member, receiver, arrayIndexPath);
				rendered.Add($"{member.Name}: {(memberValue is null ? Unavailable(member.ValueType) : Format(memberValue, depth + 1, arrayIndexPath))}");
			}
			return $"{entry.Name} {{ {string.Join(", ", rendered)} }}";
		}

		private ShellValue? GetMemberValue(MemberDescriptor member, ShellValue receiver, IReadOnlyList<int> arrayIndexPath)
		{
			try
			{
				var context = new InvocationContext(_engine, _session, _engine._services, default, arrayIndexPath);
				var value = member.GetValue(context, receiver);
				return IsValidValue(value, member.ValueType) ? value : null;
			}
			catch { return null; }
		}

		private bool IsValidValue(ShellValue? value, ShellTypeId expected)
		{
			if (value is null || !_engine.IsAssignable(value.Type, expected))
				return false;
			var entry = _engine.GetTypeEntry(value.Type);
			if (entry.Adapter is not null)
				return entry.Adapter.IsValid(value.Value);
			return entry.Kind switch
			{
				ShellTypeKind.Array => value.Value is ShellArrayValue,
				ShellTypeKind.Result => value.Value is ShellResultValue,
				ShellTypeKind.OutputRecord => value.Value is ShellOutputRecordValue,
				_ => false
			};
		}

		private string Unavailable(ShellTypeId type) => $"<unavailable: {_engine.TypeName(type)}>";

		private static IReadOnlyList<int> Append(IReadOnlyList<int> path, int index) => path.Concat([index]).ToArray();

		private static string Quote(string value)
		{
			var result = new StringBuilder(value.Length + 2).Append('"');
			foreach (var character in value)
			{
				result.Append(character switch
				{
					'"' => "\\\"",
					'\\' => "\\\\",
					'\n' => "\\n",
					'\r' => "\\r",
					'\t' => "\\t",
					_ when char.IsControl(character) => $"\\u{(int)character:X4}",
					_ => character.ToString()
				});
			}
			return result.Append('"').ToString();
		}
	}
}
