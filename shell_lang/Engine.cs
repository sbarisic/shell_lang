namespace ShellLang;

public sealed partial class ShellEngine
{
	private readonly IServiceProvider _services;
	public ShellEngine(IServiceProvider? services = null)
	{
		_services = services ?? EmptyServiceProvider.Instance;
		InitializeCoreTypes();
		Catalog = new DescriptorCatalog(this);
	}

	public DescriptorCatalog Catalog
	{
		get;
	}
	public CoreTypeCatalog Core { get; private set; } = null!;
	public long CatalogRevision
	{
		get; private set;
	}

	public ShellValue CreateValue(ShellTypeId type, object value)
	{
		ArgumentNullException.ThrowIfNull(value);
		var entry = GetTypeEntry(type);
		if (type == Core.Void || entry.Kind is ShellTypeKind.Array or ShellTypeKind.Result or ShellTypeKind.OutputRecord)
			throw new ArgumentException($"Use the specialized factory for {entry.Name}.", nameof(type));
		if (entry.Adapter is null || !entry.Adapter.IsValid(value))
			throw new ArgumentException($"CLR value is invalid for {entry.Name}.", nameof(value));
		return new ShellValue(type, value);
	}

	public ShellValue CreateArray(ShellTypeId elementType, IEnumerable<ShellValue> items)
	{
		ArgumentNullException.ThrowIfNull(items);
		var copy = items.ToArray();
		foreach (var item in copy)
			if (!IsAssignable(item.Type, elementType))
				throw new ArgumentException($"Array item {TypeName(item.Type)} is not assignable to {TypeName(elementType)}.", nameof(items));
		return new ShellValue(ArrayOf(elementType), new ShellArrayValue(copy));
	}

	public IReadOnlyList<ShellValue> GetArrayItems(ShellValue value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (GetTypeEntry(value.Type).Kind != ShellTypeKind.Array || value.Value is not ShellArrayValue array)
			throw new ArgumentException("The value is not an Array<T>.", nameof(value));
		return array.Items;
	}

	public ShellValue CreateSuccess(ShellTypeId successType, ShellTypeId errorType, ShellValue value)
	{
		if (successType == Core.Void)
			throw new ArgumentException("Use CreateVoidSuccess for Result<Void,E>.", nameof(successType));
		if (!IsAssignable(value.Type, successType))
			throw new ArgumentException("Success payload has the wrong type.", nameof(value));
		return new ShellValue(ResultOf(successType, errorType), new ShellResultValue.Success(value));
	}

	public ShellValue CreateVoidSuccess(ShellTypeId errorType) => new(ResultOf(Core.Void, errorType), new ShellResultValue.VoidSuccess());

	public ShellValue CreateError(ShellTypeId successType, ShellTypeId errorType, ShellValue error)
	{
		if (!IsAssignable(error.Type, errorType))
			throw new ArgumentException("Error payload has the wrong type.", nameof(error));
		return new ShellValue(ResultOf(successType, errorType), new ShellResultValue.Error(error));
	}

	public ShellCompilation Compile(string source, ShellSession session, CompilationOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(session);
		var diagnostics = new List<CompilationDiagnostic>();
		var tokens = new Lexer(source, diagnostics).Lex();
		var syntax = new Parser(source, tokens, diagnostics).ParseScript();
		var binder = new Binder(this, session, source, diagnostics);
		var bound = binder.Bind(syntax);
		return new ShellCompilation(this, source, diagnostics.ToArray(), bound.ResultType, CatalogRevision,
			bound.Requirements, diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? null : bound.Program);
	}

	public ExecutionResult Execute(ShellCompilation compilation, ShellSession session, ExecutionOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(compilation);
		ArgumentNullException.ThrowIfNull(session);
		if (!ReferenceEquals(compilation.Engine, this))
			return HostFailure("SL5001", "Compilation belongs to another engine.");
		if (!compilation.IsValid)
			return HostFailure("SL5002", "Cannot execute an invalid compilation.");
		if (compilation.CatalogRevision != CatalogRevision)
			return HostFailure("SL5003", "Compilation is stale because the descriptor catalog changed.");
		foreach (var requirement in compilation.SessionRequirements)
			if (!session.TryGetBinding(requirement.Name, out var value) || value.Type != requirement.Type)
				return HostFailure("SL5004", $"Session binding '{requirement.Name}' no longer satisfies the compilation requirement.");
		if (session.IsExecuting)
			return HostFailure("SL5005", "The session is already executing.");
		session.IsExecuting = true;
		try
		{
			return new Evaluator(this, session, _services, options).Execute(compilation.Program!);
		}
		finally { session.IsExecuting = false; }
	}

	private static ExecutionResult HostFailure(string code, string message) => new(ExecutionStatus.HostFault, null, null,
		new HostFault(code, message, new SourceSpan(0, 0)), 0);

	public CompletionList GetCompletions(string source, int position, ShellSession session)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(session);
		position = Math.Clamp(position, 0, source.Length);
		var start = position;
		while (start > 0 && (char.IsLetterOrDigit(source[start - 1]) || source[start - 1] == '_'))
			start--;
		var prefix = source[start..position];
		var span = SourceSpan.FromBounds(source, start, position);
		var callableStart = position;
		while (callableStart > 0 && (char.IsLetterOrDigit(source[callableStart - 1]) ||
			source[callableStart - 1] is '_' or ':'))
			callableStart--;
		var callablePrefix = source[callableStart..position];
		var callableSpan = SourceSpan.FromBounds(source, callableStart, position);
		var qualifiedCallablePrefix = callablePrefix.Contains("::", StringComparison.Ordinal);
		var items = new List<CompletionItem>();
		void Add(string name, CompletionItemKind kind, string type, string description,
			IReadOnlyList<IntrinsicSignatureDescriptor>? intrinsicSignatures = null)
		{
			if (qualifiedCallablePrefix)
				return;
			if (name.StartsWith(prefix, StringComparison.Ordinal))
				items.Add(new(span, name, kind, type, description, IntrinsicSignatures: intrinsicSignatures));
		}
		foreach (var binding in session.GetBindings())
			Add(binding.Name, CompletionItemKind.Binding, TypeName(binding.Type), "Session binding.");
		foreach (var global in Globals.Values)
			Add(global.Name, CompletionItemKind.Global, TypeName(global.Type), global.Description);
		foreach (var type in Enums)
			Add(type.Name, CompletionItemKind.Type, type.Name, type.Description);
		ShellTypeId? pipelineType = null;
		var arrow = start == 0 ? -1 : source.LastIndexOf("->", start - 1, StringComparison.Ordinal);
		if (arrow >= 0)
		{
			var left = source[..arrow].TrimEnd();
			var contextCompilation = Compile(left, session);
			if (contextCompilation.IsValid)
				pipelineType = contextCompilation.ResultType;
		}
		var lastOpen = position > 0 ? source.LastIndexOf('(', position - 1) : -1;
		var lastClose = position > 0 ? source.LastIndexOf(')', position - 1) : -1;
		var expressionPosition = lastOpen > Math.Max(arrow, lastClose);
		if (pipelineType is null || expressionPosition)
		{
			foreach (var type in Types.Where(x => x.Constructor is not null || x.TypeValues.Count != 0))
				Add(type.Name, CompletionItemKind.Type,
					type.Constructor is null ? type.Name : DescribeConstructor(type), type.Description);
			foreach (var target in _typeEntries.Values.Where(x => IsConversionTarget(x.Id) && ConversionsTo(x.Id).Count != 0))
				Add(target.Name, CompletionItemKind.Type, DescribeConversions(target.Id), target.Description);
		}
		var completionContext = FindCompletionContextType(source, position, session, arrow, pipelineType);
		if (completionContext is { } contextType)
		{
			Add("this", CompletionItemKind.Context, TypeName(contextType), "Effective contextual input.");
			if (start > 0 && source[start - 1] == '.')
				foreach (var candidate in MemberCompletions(contextType))
					Add(candidate.Name, CompletionItemKind.Member, TypeName(candidate.Type), candidate.Description);
		}
		foreach (var command in Commands.Values)
		{
			var primary = command.Inputs.FirstOrDefault(x => x.IsDefault);
			if (pipelineType is null || (primary is not null && CanConnect(pipelineType.Value, primary.Type, true)))
			{
				if (command.QualifiedName.StartsWith(callablePrefix, StringComparison.Ordinal))
					items.Add(new(callableSpan, command.QualifiedName, CompletionItemKind.Command,
						DescribeCommand(command), command.Description, command.Deprecation is not null,
						command.QualifiedName, command.Category, command.Namespace, command.Deprecation));
			}
		}
		foreach (var intrinsic in IntrinsicSchemas.Values)
			if (pipelineType is null || IntrinsicApplies(intrinsic, pipelineType.Value))
				Add(intrinsic.Name, CompletionItemKind.Intrinsic,
					string.Join(" | ", intrinsic.Signatures.Select(FormatIntrinsicSignature)), intrinsic.Description,
					intrinsic.Signatures);

		var openParen = start == 0 ? -1 : source.LastIndexOf('(', start - 1);
		if (openParen >= 0)
		{
			var commandEnd = openParen;
			var commandStart = commandEnd;
			while (commandStart > 0 && (char.IsLetterOrDigit(source[commandStart - 1]) || source[commandStart - 1] is '_' or ':'))
				commandStart--;
			if (TryGetCommand(source[commandStart..commandEnd], out var activeCallable))
			{
				var activeCommand = activeCallable.Command;
				foreach (var port in activeCommand.Inputs)
					Add(port.Name, CompletionItemKind.Port, TypeName(port.Type), port.Description);
				foreach (var argument in activeCommand.Arguments)
					Add(argument.Name, CompletionItemKind.Argument, TypeName(argument.Type), argument.Description);
				var colon = source.LastIndexOf(':', Math.Max(openParen, start - 1));
				if (colon > openParen)
				{
					var nameEnd = colon;
					var nameStart = nameEnd;
					while (nameStart > openParen && (char.IsLetterOrDigit(source[nameStart - 1]) || source[nameStart - 1] == '_'))
						nameStart--;
					var argument = activeCommand.Arguments.FirstOrDefault(x => x.Name == source[nameStart..nameEnd]);
					if (argument is not null)
					{
						var argumentType = GetTypeEntry(argument.Type);
						if (argumentType.Kind == ShellTypeKind.Enum)
							foreach (var member in argumentType.EnumMembers)
								Add(member.Name, CompletionItemKind.EnumMember, argumentType.Name, member.Description);
					}
				}
			}
		}

		var dot = start - 1;
		if (dot >= 0 && source[dot] == '.')
		{
			var receiverEnd = dot;
			var receiverStart = receiverEnd;
			while (receiverStart > 0 && (char.IsLetterOrDigit(source[receiverStart - 1]) || source[receiverStart - 1] == '_'))
				receiverStart--;
			var receiverName = source[receiverStart..receiverEnd];
			ShellTypeId? receiverType = null;
			if (receiverName == "this" && completionContext is not null)
				receiverType = completionContext;
			else if (TryGetType(receiverName, out var scopedType))
			{
				if (scopedType.Kind == ShellTypeKind.Enum)
				{
					foreach (var member in scopedType.EnumMembers)
						Add(member.Name, CompletionItemKind.EnumMember, scopedType.Name, member.Description);
					Add("values", CompletionItemKind.Member, TypeName(ArrayOf(scopedType.Id)), "All enum values in declaration order.");
				}
				foreach (var typeValue in scopedType.TypeValues)
					Add(typeValue.Name, CompletionItemKind.Member, TypeName(typeValue.ValueType), typeValue.Description);
			}
			else if (session.TryGetBinding(receiverName, out var value))
				receiverType = value.Type;
			else if (Globals.TryGetValue(receiverName, out var global))
				receiverType = global.Type;
			if (receiverType is { } type)
			{
				foreach (var candidate in MemberCompletions(type))
					Add(candidate.Name, CompletionItemKind.Member, TypeName(candidate.Type), candidate.Description);
			}
		}
		return new CompletionList(items.DistinctBy(x => (x.InsertionText, x.Kind)).OrderBy(x => x.InsertionText, StringComparer.Ordinal).ToArray());
	}

	private bool CanConnect(ShellTypeId actual, ShellTypeId expected, bool allowArray)
	{
		if (IsAssignable(actual, expected))
			return true;
		var entry = GetTypeEntry(actual);
		if (entry.Kind == ShellTypeKind.Result && CanConnect(entry.SuccessType!.Value, expected, allowArray))
			return true;
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field && CanConnect(entry.OutputFields![field], expected, allowArray))
			return true;
		return allowArray && entry.Kind == ShellTypeKind.Array && CanConnect(entry.ElementType!.Value, expected, true);
	}

	public HelpItem? GetHelp(SymbolId symbol)
	{
		if (!_symbols.TryGetValue(symbol, out var value))
			return null;
		return value switch
		{
			CommandDescriptor c => new HelpItem(c.Id, c.Name, "command", c.Description,
				c.Inputs.Select(x => new HelpParameter(x.Name, x.Type, x.Description, IsDefault: x.IsDefault)).ToArray(),
				c.Arguments.Select(x => new HelpParameter(x.Name, x.Type, x.Description, x.Required, x.DefaultValue)).ToArray(),
				c.Outputs.Select(x => new HelpParameter(x.Name, x.Type, x.Description, IsDefault: x.IsDefault)).ToArray(), c.ErrorType,
				c.RuntimeFaults.Select(x => RuntimeFaults[x.Value]).ToArray(),
				contextType: c.Inputs.FirstOrDefault(x => x.IsDefault)?.Type,
				canonicalName: c.QualifiedName, category: c.Category, namespaceName: c.Namespace,
				aliases: c.Aliases, examples: c.Examples, introducedVersion: c.IntroducedVersion,
				deprecation: c.Deprecation),
			GlobalDescriptor g => new HelpItem(g.Id, g.Name, "global", g.Description, outputs: [new("value", g.Type, g.Description)]),
			MemberDescriptor m => new HelpItem(m.Id, m.Name, "member", m.Description, inputs: [new("receiver", m.ReceiverType, "Receiver.")], outputs: [new("value", m.ValueType, m.Description)]),
			QueryDescriptor q => new HelpItem(q.Id, q.Name, "query", q.Description, inputs: [new("receiver", q.ReceiverType, "Receiver.")],
				arguments: q.Arguments.Select(x => new HelpParameter(x.Name, x.Type, x.Description, x.Required, x.DefaultValue)).ToArray(),
				outputs: [new("value", q.OutputType, q.Description)], errorType: q.ErrorType, contextType: q.ReceiverType),
			IntrinsicDescriptor i => new HelpItem(i.Id, i.Name, "intrinsic", i.Description,
				signatures: i.Signatures.Select(FormatIntrinsicSignature).ToArray(),
				intrinsicPrimaryShape: i.PrimaryShape, intrinsicSignatures: i.Signatures),
			TypeDescriptor t => BuildTypeHelp(t.Id, t.SymbolId),
			EnumTypeDescriptor e => BuildTypeHelp(e.Id, e.SymbolId),
			ErrorTypeDescriptor e => new HelpItem(e.SymbolId, e.Name, "error", e.Description),
			_ => null
		};
	}

	public HelpItem? GetTypeHelp(ShellTypeId type)
	{
		if (!_typeEntries.ContainsKey(type))
			return null;
		var symbol = Types.FirstOrDefault(x => x.Id == type)?.SymbolId ??
			Enums.FirstOrDefault(x => x.Id == type)?.SymbolId ??
			Errors.FirstOrDefault(x => x.Id == type)?.SymbolId ?? default;
		return BuildTypeHelp(type, symbol);
	}

	private HelpItem BuildTypeHelp(ShellTypeId type, SymbolId symbol)
	{
		var entry = GetTypeEntry(type);
		var descriptor = Types.FirstOrDefault(x => x.Id == type);
		var values = entry.TypeValues.Select(x => new HelpTypeValue(x.Name, x.ValueType, x.Description, x.IsProviderBacked)).ToList();
		if (entry.Kind == ShellTypeKind.Enum)
			values.Add(new("values", ArrayOf(type), "All enum values in declaration order.", false));
		var conversions = ConversionsTo(type).Select(x => new HelpConversion(x.SourceType,
			x.IsFallible ? ResultOf(type, Core.ConversionError) : type, x.IsFallible,
			x.IsFallible ? "Checked explicit conversion." : "Guaranteed explicit conversion.")).ToArray();
		return new HelpItem(symbol, entry.Name, entry.Kind switch
		{
			ShellTypeKind.Enum => "enum",
			ShellTypeKind.Error => "error",
			_ => "type"
		}, entry.Description,
			arguments: descriptor?.Constructor?.Arguments.Select(x => new HelpParameter(
				x.Name, x.Type, x.Description, x.Required, x.DefaultValue)).ToArray(),
			outputs: descriptor?.Constructor is null ? null : [new("value", type, entry.Description)],
			errorType: descriptor?.Constructor?.ErrorType,
			members: entry.Kind == ShellTypeKind.Enum
				? entry.EnumMembers.Select(x => x.Name).Concat(["values"]).ToArray()
				: entry.ResolvedSymbols.Select(x => x.Name).ToArray(),
			typeValues: values, conversions: conversions);
	}

	internal string TypeName(ShellTypeId type) => GetTypeEntry(type).Name;
	private string DescribeCommand(CommandDescriptor command)
	{
		var success = command.Outputs.Count switch
		{
			0 => "Void",
			1 => TypeName(command.Outputs[0].Type),
			_ => TypeName(command.OutputRecordType!.Value)
		};
		return command.ErrorType is { } e ? $"Result<{success},{TypeName(e)}>" : success;
	}
	private string DescribeConstructor(TypeDescriptor type)
	{
		var arguments = string.Join(", ", type.Constructor!.Arguments.OrderBy(x => x.Position)
			.Select(x => $"{x.Name}: {TypeName(x.Type)}{(x.Required ? "" : " = default")}"));
		var output = type.Constructor.ErrorType is { } error
			? $"Result<{type.Name},{TypeName(error)}>" : type.Name;
		return $"{type.Name}({arguments}) -> {output}";
	}
	private string DescribeConversions(ShellTypeId type) =>
		$"{TypeName(type)}(value) -> {TypeName(type)} or Result<{TypeName(type)},ConversionError>";
	private static string FormatIntrinsicSignature(IntrinsicSignatureDescriptor signature)
	{
		var arguments = signature.Parameters.Count == 0 ? string.Empty :
			", " + string.Join(", ", signature.Parameters.Select(x => $"{x.Name}: {x.TypePattern}"));
		return $"{signature.PrimaryTypePattern}{arguments} -> {signature.ResultTypePattern}";
	}

	private ShellTypeId? FindCompletionContextType(string source, int position, ShellSession session,
		int arrow, ShellTypeId? pipelineType)
	{
		if (arrow >= 0 && pipelineType is { } actual)
		{
			var stageStart = arrow + 2;
			while (stageStart < position && char.IsWhiteSpace(source[stageStart]))
				stageStart++;
			var stageEnd = stageStart;
			while (stageEnd < position && (char.IsLetterOrDigit(source[stageEnd]) || source[stageEnd] is '_' or ':'))
				stageEnd++;
			var open = source.IndexOf('(', stageEnd, Math.Max(0, position - stageEnd));
			if (open >= 0 && open < position)
			{
				var name = source[stageStart..stageEnd];
				if (TryGetCommand(name, out var callable) &&
					callable.Command.Inputs.FirstOrDefault(x => x.IsDefault) is { } input)
					return EffectiveCompletionType(actual, input.Type, true);
				if (name is "where" or "sort" or "any" or "all" or "select" or "distinct")
				{
					var collection = CompletionCollectionType(actual);
					if (collection is { } array)
						return GetTypeEntry(array).ElementType;
				}
			}
		}

		var openParen = position == 0 ? -1 : source.LastIndexOf('(', position - 1);
		if (openParen < 0)
			return null;
		var queryEnd = openParen;
		var queryStart = queryEnd;
		while (queryStart > 0 && (char.IsLetterOrDigit(source[queryStart - 1]) || source[queryStart - 1] == '_'))
			queryStart--;
		if (queryStart == 0 || source[queryStart - 1] != '.')
			return null;
		var receiverSource = source[..(queryStart - 1)].Trim();
		var receiver = Compile(receiverSource, session);
		if (!receiver.IsValid || receiver.ResultType is not { } receiverType)
			return null;
		var query = AccessibleQueries(receiverType).FirstOrDefault(x => x.Name == source[queryStart..queryEnd]);
		return query is null ? null : EffectiveCompletionType(receiverType, query.ReceiverType, true);
	}

	private ShellTypeId? CompletionCollectionType(ShellTypeId type)
	{
		var entry = GetTypeEntry(type);
		if (entry.Kind == ShellTypeKind.Array)
			return type;
		if (entry.Kind == ShellTypeKind.Result)
			return CompletionCollectionType(entry.SuccessType!.Value);
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field)
			return CompletionCollectionType(entry.OutputFields![field]);
		return null;
	}

	private ShellTypeId? EffectiveCompletionType(ShellTypeId actual, ShellTypeId expected, bool allowArray)
	{
		if (IsAssignable(actual, expected))
			return actual;
		var entry = GetTypeEntry(actual);
		if (entry.Kind == ShellTypeKind.Result)
			return EffectiveCompletionType(entry.SuccessType!.Value, expected, allowArray);
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field)
			return EffectiveCompletionType(entry.OutputFields![field], expected, allowArray);
		if (allowArray && entry.Kind == ShellTypeKind.Array)
			return EffectiveCompletionType(entry.ElementType!.Value, expected, true);
		return null;
	}
	private IEnumerable<MemberDescriptor> AccessibleMembers(ShellTypeId type) =>
		GetTypeEntry(type).ResolvedSymbols.Where(x => x.Member is not null).Select(x => x.Member!);
	private IEnumerable<QueryDescriptor> AccessibleQueries(ShellTypeId type) =>
		GetTypeEntry(type).ResolvedSymbols.Where(x => x.Query is not null).Select(x => x.Query!);
	private IEnumerable<(string Name, ShellTypeId Type, string Description)> MemberCompletions(ShellTypeId type)
	{
		var entry = GetTypeEntry(type);
		foreach (var field in entry.OutputFields ?? new Dictionary<string, ShellTypeId>())
			yield return (field.Key, field.Value, "Output field.");
		foreach (var member in AccessibleMembers(type))
			yield return (member.Name, member.ValueType, member.Description);
		foreach (var query in AccessibleQueries(type))
			yield return (query.Name, query.OutputType, query.Description);
		if (entry.Kind == ShellTypeKind.Result)
			foreach (var item in MemberCompletions(entry.SuccessType!.Value))
				yield return item;
		else if (entry.Kind == ShellTypeKind.Array)
			foreach (var item in MemberCompletions(entry.ElementType!.Value))
				yield return item;
		else if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field)
			foreach (var item in MemberCompletions(entry.OutputFields![field]))
				yield return item;
	}
	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public static EmptyServiceProvider Instance { get; } = new();
		public object? GetService(Type serviceType) => null;
	}
}
