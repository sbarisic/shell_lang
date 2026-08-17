namespace ShellLang;

public sealed partial class ShellEngine
{
	private readonly Dictionary<ShellTypeId, TypeEntry> _typeEntries = [];
	private readonly Dictionary<string, TypeEntry> _typesByName = new(StringComparer.Ordinal);
	private readonly Dictionary<(ShellTypeId, ShellTypeId), ShellTypeId> _resultTypes = [];
	private readonly Dictionary<ShellTypeId, ShellTypeId> _arrayTypes = [];
	private readonly Dictionary<SymbolId, object> _symbols = [];
	private int _nextSymbol;

	internal List<TypeDescriptor> Types { get; } = [];
	internal List<EnumTypeDescriptor> Enums { get; } = [];
	internal List<ErrorTypeDescriptor> Errors { get; } = [];
	internal Dictionary<string, GlobalDescriptor> Globals { get; } = new(StringComparer.Ordinal);
	internal Dictionary<string, CommandDescriptor> Commands { get; } = new(StringComparer.Ordinal);
	internal Dictionary<string, RuntimeFaultDescriptor> RuntimeFaults { get; } = new(StringComparer.Ordinal);
	internal Dictionary<string, IntrinsicDescriptor> Intrinsics { get; } = new(StringComparer.Ordinal);
	private SymbolId NextSymbol(object value)
	{
		var id = new SymbolId(Interlocked.Increment(ref _nextSymbol));
		_symbols.Add(id, value);
		return id;
	}

	private void InitializeCoreTypes()
	{
		ShellTypeId Add<T>(string name, ShellTypeKind kind = ShellTypeKind.Core,
			ShellTypeId? baseType = null, EqualityDescriptor? equality = null, OrderingDescriptor? ordering = null) where T : notnull
		{
			var id = IdentitySource.NextType();
			var adapter = new ValueAdapter<T> { TypeId = id };
			var entry = new TypeEntry
			{
				Id = id,
				Name = name,
				Description = $"Core {name} type.",
				Kind = kind,
				ClrType = typeof(T),
				Adapter = adapter,
				Bases = baseType is null ? [] : [baseType.Value],
				Equality = equality,
				Ordering = ordering
			};
			_typeEntries.Add(id, entry);
			_typesByName.Add(name, entry);
			return id;
		}

		var any = Add<object>("Any");
		var voidType = IdentitySource.NextType();
		AddEntry(new TypeEntry { Id = voidType, Name = "Void", Description = "Terminal absence of a value.", Kind = ShellTypeKind.Core });
		var boolean = Add<bool>("Bool", equality: new((a, b) => (bool)a == (bool)b));
		var int32 = Add<int>("Int32", equality: new((a, b) => (int)a == (int)b), ordering: new((a, b) => ((int)a).CompareTo((int)b)));
		var int64 = Add<long>("Int64", equality: new((a, b) => (long)a == (long)b), ordering: new((a, b) => ((long)a).CompareTo((long)b)));
		var uint32 = Add<uint>("UInt32", equality: new((a, b) => (uint)a == (uint)b), ordering: new((a, b) => ((uint)a).CompareTo((uint)b)));
		var uint64 = Add<ulong>("UInt64", equality: new((a, b) => (ulong)a == (ulong)b), ordering: new((a, b) => ((ulong)a).CompareTo((ulong)b)));
		var float32 = Add<float>("Float32", equality: new((a, b) => (float)a == (float)b), ordering: new((a, b) => ((float)a).CompareTo((float)b)));
		var float64 = Add<double>("Float64", equality: new((a, b) => (double)a == (double)b), ordering: new((a, b) => ((double)a).CompareTo((double)b)));
		var str = Add<string>("String", equality: new((a, b) => StringComparer.Ordinal.Equals(a, b)), ordering: new((a, b) => StringComparer.Ordinal.Compare((string)a, (string)b)));
		var error = Add<ShellError>("Error", ShellTypeKind.Error);
		var empty = Add<EmptyCollectionError>("EmptyCollectionError", ShellTypeKind.Error, error);
		var cardinality = Add<CollectionCardinalityError>("CollectionCardinalityError", ShellTypeKind.Error, error);
		var conversion = Add<ConversionError>("ConversionError", ShellTypeKind.Error, error);
		Core = new CoreTypeCatalog
		{
			Any = any,
			Void = voidType,
			Bool = boolean,
			Int32 = int32,
			Int64 = int64,
			UInt32 = uint32,
			UInt64 = uint64,
			Float32 = float32,
			Float64 = float64,
			String = str,
			Error = error,
			EmptyCollectionError = empty,
			CollectionCardinalityError = cardinality,
			ConversionError = conversion
		};
		foreach (var name in IntrinsicNames)
		{
			var placeholder = new IntrinsicDescriptor(default, name, IntrinsicDescriptions[name]);
			var id = NextSymbol(placeholder);
			var descriptor = new IntrinsicDescriptor(id, name, placeholder.Description);
			_symbols[id] = descriptor;
			Intrinsics.Add(name, descriptor);
		}
	}

	private void AddEntry(TypeEntry entry)
	{
		_typeEntries.Add(entry.Id, entry);
		_typesByName.Add(entry.Name, entry);
	}

	internal TypeEntry GetTypeEntry(ShellTypeId id) => _typeEntries.TryGetValue(id, out var entry)
		? entry : throw new KeyNotFoundException($"Unknown Shell type id {id}.");
	internal bool TryGetType(string name, out TypeEntry entry) => _typesByName.TryGetValue(name, out entry!);

	internal ShellTypeId ArrayOf(ShellTypeId element)
	{
		if (element == Core.Void)
			throw new ArgumentException("Array<Void> is not valid.", nameof(element));
		if (_arrayTypes.TryGetValue(element, out var existing))
			return existing;
		var id = IdentitySource.NextType();
		var entry = new TypeEntry
		{
			Id = id,
			Name = $"Array<#{element.Value}>",
			Description = "Immutable array.",
			Kind = ShellTypeKind.Array,
			ClrType = typeof(ShellArrayValue),
			ElementType = element
		};
		_typeEntries.Add(id, entry);
		_arrayTypes.Add(element, id);
		return id;
	}

	internal ShellTypeId ResultOf(ShellTypeId success, ShellTypeId error)
	{
		_ = GetTypeEntry(success);
		if (!IsAssignable(error, Core.Error))
			throw new ArgumentException("Result error type must derive from Error.", nameof(error));
		if (_resultTypes.TryGetValue((success, error), out var existing))
			return existing;
		var id = IdentitySource.NextType();
		var entry = new TypeEntry
		{
			Id = id,
			Name = $"Result<{GetTypeEntry(success).Name},{GetTypeEntry(error).Name}>",
			Description = "Typed result.",
			Kind = ShellTypeKind.Result,
			ClrType = typeof(ShellResultValue),
			SuccessType = success,
			ErrorType = error
		};
		_typeEntries.Add(id, entry);
		_resultTypes.Add((success, error), id);
		return id;
	}

	internal bool IsAssignable(ShellTypeId actual, ShellTypeId expected)
	{
		if (actual == expected || expected == Core.Any)
			return actual != Core.Void;
		var a = GetTypeEntry(actual);
		var e = GetTypeEntry(expected);
		if (a.Kind == ShellTypeKind.Array && e.Kind == ShellTypeKind.Array)
			return IsAssignable(a.ElementType!.Value, e.ElementType!.Value);
		if (a.Kind == ShellTypeKind.Result && e.Kind == ShellTypeKind.Result)
			return IsAssignable(a.SuccessType!.Value, e.SuccessType!.Value) && IsAssignable(a.ErrorType!.Value, e.ErrorType!.Value);
		var visited = new HashSet<ShellTypeId>();
		var queue = new Queue<ShellTypeId>(a.Bases);
		while (queue.TryDequeue(out var current))
		{
			if (!visited.Add(current))
				continue;
			if (current == expected)
				return true;
			foreach (var parent in GetTypeEntry(current).Bases)
				queue.Enqueue(parent);
		}
		return false;
	}

	internal ShellTypeId CommonError(ShellTypeId left, ShellTypeId right)
	{
		var leftDistances = AncestorDistances(left);
		var rightDistances = AncestorDistances(right);
		return leftDistances.Keys.Intersect(rightDistances.Keys)
			.OrderBy(id => Math.Max(leftDistances[id], rightDistances[id]))
			.ThenBy(id => leftDistances[id] + rightDistances[id])
			.ThenBy(id => GetTypeEntry(id).Name, StringComparer.Ordinal)
			.FirstOrDefault(Core.Error);
	}

	private Dictionary<ShellTypeId, int> AncestorDistances(ShellTypeId type)
	{
		var distances = new Dictionary<ShellTypeId, int> { [type] = 0 };
		var queue = new Queue<ShellTypeId>();
		queue.Enqueue(type);
		while (queue.TryDequeue(out var current))
		{
			var nextDistance = distances[current] + 1;
			foreach (var parent in GetTypeEntry(current).Bases)
				if (!distances.TryGetValue(parent, out var previous) || nextDistance < previous)
				{
					distances[parent] = nextDistance;
					queue.Enqueue(parent);
				}
		}
		return distances;
	}

	internal TypeEntry? FindMemberOwner(ShellTypeId receiver, string name, out MemberDescriptor? member, out QueryDescriptor? query)
	{
		var symbol = GetTypeEntry(receiver).ResolvedSymbols.FirstOrDefault(x => x.Name == name);
		member = symbol?.Member;
		query = symbol?.Query;
		return symbol is null ? null : GetTypeEntry(symbol.DeclaringType);
	}
}
