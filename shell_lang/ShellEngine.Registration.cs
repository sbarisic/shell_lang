using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace ShellLang;

public sealed partial class ShellEngine
{
	private static readonly Regex IdentifierPattern = IdentifierRegex();
	private static readonly Regex FaultPattern = FaultRegex();

	private List<HostingDiagnostic> Validate(DescriptorSet set,
		out Dictionary<ShellTypeId, IReadOnlyList<ResolvedTypeSymbol>> resolvedSymbols)
	{
		var d = new List<HostingDiagnostic>();
		var newTypeNames = new HashSet<string>(StringComparer.Ordinal);
		void Name(string name, string kind)
		{
			if (!IdentifierPattern.IsMatch(name))
				d.Add(new("SL3001", $"Invalid {kind} name '{name}'."));
			if (name == "this")
				d.Add(new("SL3022", $"The reserved contextual name 'this' cannot be registered as a {kind}."));
		}
		foreach (var item in set.Types.Cast<object>().Concat(set.Enums).Concat(set.Errors))
		{
			var name = item switch
			{
				TypeDescriptor x => x.Name,
				EnumTypeDescriptor x => x.Name,
				ErrorTypeDescriptor x => x.Name,
				_ => ""
			};
			Name(name, "type");
			if (name == "Stream" || name.StartsWith("Stream_", StringComparison.Ordinal))
				d.Add(new("SL3016", "Stream<T> is reserved and cannot be registered in 0.1."));
			if (_typesByName.ContainsKey(name) || !newTypeNames.Add(name))
				d.Add(new("SL3002", $"Duplicate type '{name}'."));
		}
		var available = _typeEntries.Keys.Concat(set.Types.Select(x => x.Id)).Concat(set.Enums.Select(x => x.Id)).Concat(set.Errors.Select(x => x.Id))
			.Concat(set.Commands.Where(x => x.OutputRecordType is not null).Select(x => x.OutputRecordType!.Value)).ToHashSet();
		var newErrorIds = set.Errors.Select(x => x.Id).ToHashSet();
		foreach (var type in set.Types)
		{
			if (string.IsNullOrWhiteSpace(type.Description))
				d.Add(new("SL3003", $"Type '{type.Name}' needs a description."));
			if (type.Adapter is null || type.ClrType != type.Adapter.ClrType)
				d.Add(new("SL3004", $"Type '{type.Name}' has an invalid adapter."));
			foreach (var b in type.DirectBases)
				if (!available.Contains(b))
					d.Add(new("SL3005", $"Type '{type.Name}' has unknown base {b}."));
			ValidateLocalNames(type.Name, type.Members.Select(x => x.Name).Concat(type.Queries.Select(x => x.Name)), d);
			ValidateScopedValueNames(type.Name, type.TypeValues.Select(x => x.Name), d);
			foreach (var member in type.Members)
			{
				Name(member.Name, "member");
				if (string.IsNullOrWhiteSpace(member.Description))
					d.Add(new("SL3003", $"Member '{type.Name}.{member.Name}' needs a description."));
				if (!IsKnownTypeReference(member.ValueType, available))
					d.Add(new("SL3005", $"Member '{type.Name}.{member.Name}' has unknown type."));
			}
			foreach (var query in type.Queries)
			{
				Name(query.Name, "query");
				if (string.IsNullOrWhiteSpace(query.Description))
					d.Add(new("SL3003", $"Query '{type.Name}.{query.Name}' needs a description."));
				if (!IsKnownTypeReference(query.OutputType, available))
					d.Add(new("SL3005", $"Query '{type.Name}.{query.Name}' has unknown output type."));
				ValidateLocalNames($"{type.Name}.{query.Name}", query.Arguments.Select(x => x.Name), d);
				foreach (var argument in query.Arguments)
				{
					Name(argument.Name, "argument");
					if (!IsKnownTypeReference(argument.Type, available))
						d.Add(new("SL3005", $"Query argument '{type.Name}.{query.Name}.{argument.Name}' has unknown type."));
					if ((!argument.Required && argument.DefaultValue is null) || (argument.DefaultValue is not null && argument.DefaultValue.Type != argument.Type))
						d.Add(new("SL3010", $"Query argument '{type.Name}.{query.Name}.{argument.Name}' has an invalid default."));
				}
				if (query.Arguments.Select(x => x.Position).Distinct().Count() != query.Arguments.Count || query.Arguments.Any(x => x.Position < 0))
					d.Add(new("SL3020", $"Query '{type.Name}.{query.Name}' has invalid positional argument indices."));
				if (query.ErrorType is { } queryError && !(newErrorIds.Contains(queryError) || (_typeEntries.TryGetValue(queryError, out var queryErrorEntry) && queryErrorEntry.Kind == ShellTypeKind.Error)))
					d.Add(new("SL3017", $"Query '{type.Name}.{query.Name}' has a non-error ErrorType."));
			}
			if (type.Constructor is { } constructor)
			{
				ValidateLocalNames($"{type.Name} constructor", constructor.Arguments.Select(x => x.Name), d);
				foreach (var argument in constructor.Arguments)
				{
					Name(argument.Name, "constructor argument");
					if (string.IsNullOrWhiteSpace(argument.Description))
						d.Add(new("SL3003", $"Constructor argument '{type.Name}.{argument.Name}' needs a description."));
					if (!IsKnownTypeReference(argument.Type, available))
						d.Add(new("SL3005", $"Constructor argument '{type.Name}.{argument.Name}' has unknown type."));
					if ((!argument.Required && argument.DefaultValue is null) ||
						(argument.DefaultValue is not null && argument.DefaultValue.Type != argument.Type))
						d.Add(new("SL3010", $"Constructor argument '{type.Name}.{argument.Name}' has an invalid default."));
				}
				if (constructor.Arguments.Select(x => x.Position).Distinct().Count() != constructor.Arguments.Count ||
					constructor.Arguments.Any(x => x.Position < 0))
					d.Add(new("SL3020", $"Constructor '{type.Name}' has invalid positional argument indices."));
				if (constructor.ErrorType is { } constructorError &&
					!(newErrorIds.Contains(constructorError) ||
					(_typeEntries.TryGetValue(constructorError, out var constructorErrorEntry) && constructorErrorEntry.Kind == ShellTypeKind.Error)))
					d.Add(new("SL3017", $"Constructor '{type.Name}' has a non-error ErrorType."));
			}
			foreach (var value in type.TypeValues)
			{
				Name(value.Name, "type-scoped value");
				if (string.IsNullOrWhiteSpace(value.Description))
					d.Add(new("SL3003", $"Type value '{type.Name}.{value.Name}' needs a description."));
				if (!IsKnownTypeReference(value.ValueType, available))
					d.Add(new("SL3005", $"Type value '{type.Name}.{value.Name}' has unknown type."));
				if (value.FixedValue is { } fixedValue &&
					(!IsAssignableForRegistration(fixedValue.Type, value.ValueType, set) || !IsValidFixedValue(fixedValue, set)))
					d.Add(new("SL3024", $"Type value '{type.Name}.{value.Name}' has an invalid fixed value."));
			}
		}
		foreach (var type in set.Enums)
		{
			if (type.Members.Count == 0)
				d.Add(new("SL3006", $"Enum '{type.Name}' has no members."));
			ValidateLocalNames(type.Name, type.Members.Select(x => x.Name), d);
			foreach (var member in type.Members)
			{
				Name(member.Name, "enum member");
				if (member.Name == "values")
					d.Add(new("SL3024", $"Enum '{type.Name}' cannot declare the reserved scoped value 'values'."));
			}
		}
		foreach (var error in set.Errors)
		{
			var knownError = newErrorIds.Contains(error.BaseType) || (_typeEntries.TryGetValue(error.BaseType, out var baseEntry) && baseEntry.Kind == ShellTypeKind.Error);
			if (!knownError)
				d.Add(new("SL3017", $"Error '{error.Name}' must have one error base rooted at Error."));
		}
		ValidateTypeCycles(set.Types, d);
		ValidateErrorCycles(set.Errors, d);
		resolvedSymbols = ResolveTypeSymbols(set.Types, d);
		var callableNames = new HashSet<string>(IntrinsicNames, StringComparer.Ordinal);
		callableNames.UnionWith(Commands.Keys);
		callableNames.UnionWith(Types.Where(x => x.Constructor is not null).Select(x => x.Name));
		callableNames.UnionWith(_typeEntries.Values.Where(x => IsConversionTarget(x.Id)).Select(x => x.Name));
		foreach (var type in set.Types.Where(x => x.Constructor is not null))
			if (!callableNames.Add(type.Name))
				d.Add(new("SL3023", $"Constructible type '{type.Name}' collides with an existing callable name."));
		var commandNames = new HashSet<string>(Commands.Keys, StringComparer.Ordinal);
		foreach (var command in set.Commands)
		{
			Name(command.Name, "command");
			if (!commandNames.Add(command.Name) || IntrinsicNames.Contains(command.Name))
				d.Add(new("SL3007", $"Duplicate or reserved command '{command.Name}'."));
			if (!callableNames.Add(command.Name))
				d.Add(new("SL3023", $"Command '{command.Name}' collides with a constructible type or intrinsic."));
			if (string.IsNullOrWhiteSpace(command.Description))
				d.Add(new("SL3003", $"Command '{command.Name}' needs a description."));
			if (command.Inputs.Count(x => x.IsDefault) > 1)
				d.Add(new("SL3008", $"Command '{command.Name}' has more than one default input."));
			if (command.Outputs.Count(x => x.IsDefault) > 1)
				d.Add(new("SL3009", $"Command '{command.Name}' has more than one default output."));
			ValidateLocalNames(command.Name, command.Inputs.Select(x => x.Name).Concat(command.Arguments.Select(x => x.Name)), d);
			ValidateLocalNames(command.Name, command.Outputs.Select(x => x.Name), d);
			foreach (var port in command.Inputs)
			{
				Name(port.Name, "input");
				if (string.IsNullOrWhiteSpace(port.Description))
					d.Add(new("SL3003", $"Input '{command.Name}.{port.Name}' needs a description."));
			}
			foreach (var argument in command.Arguments)
			{
				Name(argument.Name, "argument");
				if (string.IsNullOrWhiteSpace(argument.Description))
					d.Add(new("SL3003", $"Argument '{command.Name}.{argument.Name}' needs a description."));
			}
			foreach (var output in command.Outputs)
			{
				Name(output.Name, "output");
				if (string.IsNullOrWhiteSpace(output.Description))
					d.Add(new("SL3003", $"Output '{command.Name}.{output.Name}' needs a description."));
			}
			foreach (var item in command.Inputs.Select(x => x.Type).Concat(command.Arguments.Select(x => x.Type)).Concat(command.Outputs.Select(x => x.Type)))
				if (!IsKnownTypeReference(item, available))
					d.Add(new("SL3005", $"Command '{command.Name}' references unknown type {item}."));
			foreach (var arg in command.Arguments)
				if ((!arg.Required && arg.DefaultValue is null) || (arg.DefaultValue is not null && arg.DefaultValue.Type != arg.Type))
					d.Add(new("SL3010", $"Argument '{command.Name}.{arg.Name}' has an invalid default."));
			if (command.Arguments.Select(x => x.Position).Distinct().Count() != command.Arguments.Count || command.Arguments.Any(x => x.Position < 0))
				d.Add(new("SL3020", $"Command '{command.Name}' has invalid positional argument indices."));
			if (command.ErrorType is { } et && !(newErrorIds.Contains(et) || (_typeEntries.TryGetValue(et, out var errorEntry) && errorEntry.Kind == ShellTypeKind.Error)))
				d.Add(new("SL3017", $"Command '{command.Name}' has a non-error ErrorType."));
			foreach (var fault in command.RuntimeFaults)
				if (!RuntimeFaults.ContainsKey(fault.Value) && !set.RuntimeFaults.Any(x => x.Code == fault))
					d.Add(new("SL3011", $"Command '{command.Name}' references unknown fault {fault}."));
		}
		foreach (var global in set.Globals)
		{
			Name(global.Name, "global");
			if (Globals.ContainsKey(global.Name) || set.Globals.Count(x => x.Name == global.Name) > 1)
				d.Add(new("SL3012", $"Duplicate global '{global.Name}'."));
			if (!IsKnownTypeReference(global.Type, available))
				d.Add(new("SL3005", $"Global '{global.Name}' has unknown type."));
			if (string.IsNullOrWhiteSpace(global.Description))
				d.Add(new("SL3003", $"Global '{global.Name}' needs a description."));
		}
		foreach (var fault in set.RuntimeFaults)
		{
			if (!FaultPattern.IsMatch(fault.Code.Value) || fault.Code.Value.StartsWith("SL", StringComparison.Ordinal))
				d.Add(new("SL3013", $"Invalid runtime fault code '{fault.Code}'."));
			Name(fault.Name, "runtime fault");
			if (RuntimeFaults.ContainsKey(fault.Code.Value) || set.RuntimeFaults.Count(x => x.Code == fault.Code) > 1)
				d.Add(new("SL3014", $"Duplicate runtime fault '{fault.Code}'."));
			if (RuntimeFaults.Values.Any(x => x.Name == fault.Name) || set.RuntimeFaults.Count(x => x.Name == fault.Name) > 1)
				d.Add(new("SL3014", $"Duplicate runtime fault name '{fault.Name}'."));
			if (string.IsNullOrWhiteSpace(fault.Description))
				d.Add(new("SL3003", $"Runtime fault '{fault.Code}' needs a description."));
		}
		return d;
	}

	private Dictionary<ShellTypeId, IReadOnlyList<ResolvedTypeSymbol>> ResolveTypeSymbols(
		IReadOnlyList<TypeDescriptor> types, List<HostingDiagnostic> diagnostics)
	{
		var descriptors = types.ToDictionary(x => x.Id);
		var resolved = new Dictionary<ShellTypeId, IReadOnlyList<ResolvedTypeSymbol>>();
		var resolving = new HashSet<ShellTypeId>();

		IReadOnlyList<ResolvedTypeSymbol> Resolve(ShellTypeId id)
		{
			if (resolved.TryGetValue(id, out var cached))
				return cached;
			if (_typeEntries.TryGetValue(id, out var existing))
				return existing.ResolvedSymbols;
			if (!descriptors.TryGetValue(id, out var descriptor) || !resolving.Add(id))
				return Array.Empty<ResolvedTypeSymbol>();

			var local = descriptor.Members.Select(x => new ResolvedTypeSymbol(id, x, null))
				.Concat(descriptor.Queries.Select(x => new ResolvedTypeSymbol(id, null, x))).ToArray();
			var inherited = descriptor.DirectBases.SelectMany(Resolve).ToArray();
			var result = new List<ResolvedTypeSymbol>(local);
			foreach (var group in inherited.Where(x => local.All(localSymbol => localSymbol.Name != x.Name))
				.GroupBy(x => x.Name, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
			{
				var candidates = group.GroupBy(x => (x.DeclaringType, x.Descriptor)).Select(x => x.First()).ToArray();
				var winners = candidates.Where(candidate => !candidates.Any(other =>
					!ReferenceEquals(candidate, other) && IsNominalSubtype(other.DeclaringType, candidate.DeclaringType, descriptors))).ToArray();
				if (winners.Length > 1)
				{
					var sources = string.Join(", ", winners.OrderBy(x => TypeName(x.DeclaringType, descriptors), StringComparer.Ordinal)
						.Select(x => $"{TypeName(x.DeclaringType, descriptors)} ({x.Kind})"));
					diagnostics.Add(new("SL3021", $"Type '{descriptor.Name}' inherits ambiguous symbol '{group.Key}' from incomparable bases {sources}; declare '{group.Key}' on '{descriptor.Name}' to resolve it."));
					continue;
				}
				if (winners.Length == 1)
					result.Add(winners[0]);
			}
			resolving.Remove(id);
			resolved[id] = result;
			return result;
		}

		foreach (var type in types)
			Resolve(type.Id);
		return resolved;
	}

	private bool IsNominalSubtype(ShellTypeId actual, ShellTypeId expected,
		IReadOnlyDictionary<ShellTypeId, TypeDescriptor> pending)
	{
		if (actual == expected)
			return true;
		var seen = new HashSet<ShellTypeId>();
		var queue = new Queue<ShellTypeId>();
		queue.Enqueue(actual);
		while (queue.TryDequeue(out var current))
		{
			if (!seen.Add(current))
				continue;
			var bases = pending.TryGetValue(current, out var descriptor)
				? descriptor.DirectBases
				: _typeEntries.TryGetValue(current, out var entry) ? entry.Bases : Array.Empty<ShellTypeId>();
			foreach (var parent in bases)
			{
				if (parent == expected)
					return true;
				queue.Enqueue(parent);
			}
		}
		return false;
	}

	private string TypeName(ShellTypeId type, IReadOnlyDictionary<ShellTypeId, TypeDescriptor> pending) =>
		pending.TryGetValue(type, out var descriptor) ? descriptor.Name : GetTypeEntry(type).Name;

	private static void ValidateLocalNames(string owner, IEnumerable<string> names, List<HostingDiagnostic> diagnostics)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var name in names)
			if (!seen.Add(name))
				diagnostics.Add(new("SL3015", $"Duplicate local name '{owner}.{name}'."));
	}

	private bool IsKnownTypeReference(ShellTypeId type, HashSet<ShellTypeId> available)
	{
		if (!available.Contains(type))
			return false;
		if (!_typeEntries.TryGetValue(type, out var entry))
			return true;
		return entry.Kind switch
		{
			ShellTypeKind.Array => available.Contains(entry.ElementType!.Value),
			ShellTypeKind.Result => available.Contains(entry.SuccessType!.Value) && available.Contains(entry.ErrorType!.Value),
			ShellTypeKind.OutputRecord => entry.OutputFields!.Values.All(available.Contains),
			_ => true
		};
	}

	private static void ValidateScopedValueNames(string owner, IEnumerable<string> names,
		List<HostingDiagnostic> diagnostics)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var name in names)
			if (!seen.Add(name))
				diagnostics.Add(new("SL3024", $"Duplicate type-scoped value '{owner}.{name}'."));
	}

	private bool IsValidFixedValue(ShellValue value, DescriptorSet pending)
	{
		if (_typeEntries.TryGetValue(value.Type, out var existing))
			return existing.Adapter?.IsValid(value.Value) ?? existing.Kind switch
			{
				ShellTypeKind.Array => value.Value is ShellArrayValue,
				ShellTypeKind.Result => value.Value is ShellResultValue,
				ShellTypeKind.OutputRecord => value.Value is ShellOutputRecordValue,
				_ => false
			};
		var host = pending.Types.FirstOrDefault(x => x.Id == value.Type);
		if (host is not null)
			return host.Adapter.IsValid(value.Value);
		var @enum = pending.Enums.FirstOrDefault(x => x.Id == value.Type);
		if (@enum is not null)
			return @enum.Adapter.IsValid(value.Value);
		var error = pending.Errors.FirstOrDefault(x => x.Id == value.Type);
		return error is not null && error.Adapter.IsValid(value.Value);
	}

	private bool IsAssignableForRegistration(ShellTypeId actual, ShellTypeId expected, DescriptorSet pending)
	{
		if (actual == expected || expected == Core.Any)
			return actual != Core.Void;
		if (_typeEntries.ContainsKey(actual) && _typeEntries.ContainsKey(expected))
			return IsAssignable(actual, expected);
		var descriptors = pending.Types.ToDictionary(x => x.Id);
		return IsNominalSubtype(actual, expected, descriptors);
	}

	private void ValidateTypeCycles(IReadOnlyList<TypeDescriptor> types, List<HostingDiagnostic> diagnostics)
	{
		var map = types.ToDictionary(x => x.Id, x => x.DirectBases);
		var visiting = new HashSet<ShellTypeId>();
		var visited = new HashSet<ShellTypeId>();
		bool Visit(ShellTypeId id)
		{
			if (visiting.Contains(id))
				return true;
			if (!visited.Add(id) || !map.TryGetValue(id, out var bases))
				return false;
			visiting.Add(id);
			foreach (var parent in bases)
				if (Visit(parent))
					return true;
			visiting.Remove(id);
			return false;
		}
		foreach (var type in types)
			if (Visit(type.Id))
			{
				diagnostics.Add(new("SL3018", $"Nominal type graph containing '{type.Name}' is cyclic."));
				break;
			}
	}

	private void ValidateErrorCycles(IReadOnlyList<ErrorTypeDescriptor> errors, List<HostingDiagnostic> diagnostics)
	{
		var map = errors.ToDictionary(x => x.Id, x => x.BaseType);
		foreach (var error in errors)
		{
			var seen = new HashSet<ShellTypeId>();
			var current = error.Id;
			while (map.TryGetValue(current, out var parent))
			{
				if (!seen.Add(current))
				{
					diagnostics.Add(new("SL3019", $"Error chain containing '{error.Name}' is cyclic."));
					return;
				}
				current = parent;
			}
		}
	}

	public RegistrationResult Register(DescriptorSet descriptors)
	{
		ArgumentNullException.ThrowIfNull(descriptors);
		var diagnostics = Validate(descriptors, out var resolvedSymbols);
		if (diagnostics.Count > 0)
			return new RegistrationResult(diagnostics);
		foreach (var type in descriptors.Types)
		{
			type.SymbolId = NextSymbol(type);
			if (type.Constructor is { } constructor)
				constructor.ConstructedType = type.Id;
			foreach (var member in type.Members)
			{
				member.ReceiverType = type.Id;
				member.Id = NextSymbol(member);
			}
			foreach (var query in type.Queries)
			{
				query.ReceiverType = type.Id;
				query.Id = NextSymbol(query);
			}
			AddEntry(new TypeEntry
			{
				Id = type.Id,
				Name = type.Name,
				Description = type.Description,
				Kind = ShellTypeKind.Host,
				ClrType = type.ClrType,
				Adapter = type.Adapter,
				Bases = type.DirectBases,
				Members = type.Members,
				Queries = type.Queries,
				ResolvedSymbols = resolvedSymbols[type.Id],
				Equality = type.Equality,
				Ordering = type.Ordering,
				Constructor = type.Constructor,
				TypeValues = type.TypeValues
			});
			Types.Add(type);
			RefreshConstructedTypeNames(type.Id, type.Name);
		}
		foreach (var type in descriptors.Enums)
		{
			type.SymbolId = NextSymbol(type);
			AddEntry(new TypeEntry
			{
				Id = type.Id,
				Name = type.Name,
				Description = type.Description,
				Kind = ShellTypeKind.Enum,
				ClrType = type.ClrType,
				Adapter = type.Adapter,
				EnumMembers = type.Members,
				Ordering = type.Ordering,
				Equality = new((a, b) => Equals(a, b))
			});
			Enums.Add(type);
			RefreshConstructedTypeNames(type.Id, type.Name);
		}
		foreach (var error in descriptors.Errors)
		{
			error.SymbolId = NextSymbol(error);
			AddEntry(new TypeEntry
			{
				Id = error.Id,
				Name = error.Name,
				Description = error.Description,
				Kind = ShellTypeKind.Error,
				ClrType = error.ClrType,
				Adapter = error.Adapter,
				Bases = [error.BaseType]
			});
			Errors.Add(error);
			RefreshConstructedTypeNames(error.Id, error.Name);
		}
		foreach (var fault in descriptors.RuntimeFaults)
			RuntimeFaults.Add(fault.Code.Value, fault);
		foreach (var global in descriptors.Globals)
		{
			global.Id = NextSymbol(global);
			Globals.Add(global.Name, global);
		}
		foreach (var command in descriptors.Commands)
		{
			command.Id = NextSymbol(command);
			if (command.Outputs.Count > 1)
			{
				var id = command.OutputRecordType!.Value;
				_typeEntries.Add(id, new TypeEntry
				{
					Id = id,
					Name = $"{ToPascal(command.Name)}.Output",
					Description = $"Outputs of {command.Name}.",
					Kind = ShellTypeKind.OutputRecord,
					ClrType = typeof(ShellOutputRecordValue),
					OutputFields = new ReadOnlyDictionary<string, ShellTypeId>(command.Outputs.ToDictionary(x => x.Name, x => x.Type, StringComparer.Ordinal)),
					DefaultOutput = command.Outputs.FirstOrDefault(x => x.IsDefault)?.Name
				});
			}
			Commands.Add(command.Name, command);
		}
		CatalogRevision++;
		return new RegistrationResult(Array.Empty<HostingDiagnostic>());
	}

	private static string ToPascal(string value) => string.Concat(value.Split('_', StringSplitOptions.RemoveEmptyEntries)
		.Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));

	private void RefreshConstructedTypeNames(ShellTypeId type, string name)
	{
		if (_arrayTypes.TryGetValue(type, out var array))
			_typeEntries[array].Name = $"Array<{name}>";
	}

	[GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
	private static partial Regex IdentifierRegex();
	[GeneratedRegex("^[A-Z][A-Z0-9_]{1,15}[0-9]{4}$", RegexOptions.CultureInvariant)]
	private static partial Regex FaultRegex();
}
