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
			bound.Requirements, diagnostics.Count == 0 ? bound.Program : null);
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
		var items = new List<CompletionItem>();
		void Add(string name, CompletionItemKind kind, string type, string description)
		{
			if (name.StartsWith(prefix, StringComparison.Ordinal))
				items.Add(new(span, name, kind, type, description));
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
		foreach (var command in Commands.Values)
		{
			var primary = command.Inputs.FirstOrDefault(x => x.IsDefault);
			if (pipelineType is null || (primary is not null && CanConnect(pipelineType.Value, primary.Type, true)))
				Add(command.Name, CompletionItemKind.Command, DescribeCommand(command), command.Description);
		}
		foreach (var intrinsic in IntrinsicNames)
			if (pipelineType is null || IntrinsicApplies(intrinsic, pipelineType.Value))
				Add(intrinsic, CompletionItemKind.Intrinsic, "intrinsic", "Core compiler intrinsic.");

		var openParen = start == 0 ? -1 : source.LastIndexOf('(', start - 1);
		if (openParen >= 0)
		{
			var commandEnd = openParen;
			var commandStart = commandEnd;
			while (commandStart > 0 && (char.IsLetterOrDigit(source[commandStart - 1]) || source[commandStart - 1] == '_'))
				commandStart--;
			if (Commands.TryGetValue(source[commandStart..commandEnd], out var activeCommand))
			{
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
			if (session.TryGetBinding(receiverName, out var value))
				receiverType = value.Type;
			else if (Globals.TryGetValue(receiverName, out var global))
				receiverType = global.Type;
			else if (TryGetType(receiverName, out var enumType) && enumType.Kind == ShellTypeKind.Enum)
				foreach (var member in enumType.EnumMembers)
					Add(member.Name, CompletionItemKind.EnumMember, enumType.Name, member.Description);
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

	private bool IntrinsicApplies(string name, ShellTypeId type)
	{
		var entry = GetTypeEntry(type);
		if (name is "require" or "value_or" or "error" or "is_ok")
			return entry.Kind == ShellTypeKind.Result;
		if (entry.Kind == ShellTypeKind.Array)
			return true;
		if (entry.Kind == ShellTypeKind.Result)
			return IntrinsicApplies(name, entry.SuccessType!.Value);
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field)
			return IntrinsicApplies(name, entry.OutputFields![field]);
		return false;
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
				c.RuntimeFaults.Select(x => RuntimeFaults[x.Value]).ToArray()),
			GlobalDescriptor g => new HelpItem(g.Id, g.Name, "global", g.Description, outputs: [new("value", g.Type, g.Description)]),
			MemberDescriptor m => new HelpItem(m.Id, m.Name, "member", m.Description, inputs: [new("receiver", m.ReceiverType, "Receiver.")], outputs: [new("value", m.ValueType, m.Description)]),
			QueryDescriptor q => new HelpItem(q.Id, q.Name, "query", q.Description, inputs: [new("receiver", q.ReceiverType, "Receiver.")],
				arguments: q.Arguments.Select(x => new HelpParameter(x.Name, x.Type, x.Description, x.Required, x.DefaultValue)).ToArray(),
				outputs: [new("value", q.OutputType, q.Description)], errorType: q.ErrorType),
			IntrinsicDescriptor i => new HelpItem(i.Id, i.Name, "intrinsic", i.Description),
			TypeDescriptor t => new HelpItem(t.SymbolId, t.Name, "type", t.Description,
				members: GetTypeEntry(t.Id).ResolvedSymbols.Select(x => x.Name).ToArray()),
			EnumTypeDescriptor e => new HelpItem(e.SymbolId, e.Name, "enum", e.Description, members: e.Members.Select(x => x.Name).ToArray()),
			ErrorTypeDescriptor e => new HelpItem(e.SymbolId, e.Name, "error", e.Description),
			_ => null
		};
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
